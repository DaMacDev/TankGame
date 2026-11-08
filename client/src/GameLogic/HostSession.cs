using System.Collections.Generic;
using TankGame.Domain;
using TankGame.Domain.Net;

namespace TankGame.GameLogic;

/// <summary>The authority of a networked match (ADR-0019 step 3): the host client's loop that runs
/// the REAL <see cref="IWorld"/> — the same GameLogic as single-player, no second implementation —
/// from the host's local input plus the guest's relayed input, and broadcasts one
/// <see cref="SnapshotFrame"/> per fixed tick. Wall changes are captured from the grid's own
/// <see cref="IWallGrid.CellChanged"/> event and ride the next snapshot once, so a brick broken on
/// the host breaks on every guest. Pure C# — the net scene owns the fixed-tick cadence and the
/// world's contents; this class only wires relay→input and world→snapshot.</summary>
public sealed class HostSession
{
    private readonly IMatchTransport _transport;
    private readonly IWorld _world;
    private readonly IReadOnlyList<(byte Slot, ITank Tank)> _tanks;
    private readonly IReadOnlyDictionary<byte, RelayedInputSource> _guestInputs;
    private readonly List<WallDelta> _pendingWallDeltas = new();

    // Small stable wire ids for the live pickups (a Guid is too fat for the snapshot). Entries for
    // reaped pickups linger — a match drops a few dozen at most, so the map stays tiny.
    private readonly Dictionary<System.Guid, ushort> _pickupIds = new();
    private ushort _nextPickupId;

    // Both 3D scenes lay their grid at origin zero with 64-unit tiles; the snapshot's pickup cell is
    // derived from that shared convention. ponytail: promote to a ctor arg if a scene ever moves it.
    private const float PickupTileSize = 64f;

    /// <param name="transport">The relay link: relayed guest inputs in, snapshots out.</param>
    /// <param name="world">The authoritative world, already populated by the scene with every
    /// tank in <paramref name="tanks"/>.</param>
    /// <param name="walls">The shared maze; its change events become the snapshots' wall deltas.</param>
    /// <param name="tanks">Each player's slot and the tank that drives it, in snapshot order.</param>
    /// <param name="guestInputs">One relayed input source per HUMAN guest slot; each incoming frame
    /// routes to its sender's source (the relay stamped the slot). AI-filled slots have none.</param>
    public HostSession(
        IMatchTransport transport,
        IWorld world,
        IWallGrid walls,
        IReadOnlyList<(byte Slot, ITank Tank)> tanks,
        IReadOnlyDictionary<byte, RelayedInputSource> guestInputs)
    {
        _transport = transport;
        _world = world;
        _tanks = tanks;
        _guestInputs = guestInputs;

        transport.InputReceived += frame =>
        {
            if (_guestInputs.TryGetValue(frame.Slot, out var source))
            {
                source.Receive(frame); // an unknown slot's frame is dropped (no seat to drive)
            }
        };
        walls.CellChanged += changed => _pendingWallDeltas.Add(new WallDelta(
            (ushort)changed.X, (ushort)changed.Y, (byte)changed.Cell.Material, (byte)changed.Cell.Hp));
    }

    /// <summary>The 2-player-era shape: one guest, every frame routed to it whether or not the
    /// relay stamped a slot (there is only one sender it could be). Kept so the pre-4-player call
    /// sites and tests stay terse; the roster-driven scene uses the per-slot constructor.</summary>
    public HostSession(
        IMatchTransport transport,
        IWorld world,
        IWallGrid walls,
        IReadOnlyList<(byte Slot, ITank Tank)> tanks,
        RelayedInputSource guestInput)
        : this(transport, world, walls, tanks,
            new Dictionary<byte, RelayedInputSource> { [0] = guestInput, [1] = guestInput })
    {
    }

    /// <summary>The authoritative tick counter, stamped on each outgoing snapshot.</summary>
    public uint Tick { get; private set; }

    /// <summary>One authoritative tick: advances the world (every tank reads its input source —
    /// keyboard for the host, the relayed frames for the guest), then broadcasts the resulting
    /// snapshot with any wall changes since the last one.</summary>
    public void Step(float deltaSeconds)
    {
        _world.Step(deltaSeconds);
        Tick++;

        var states = new List<TankState>(_tanks.Count);
        foreach (var (slot, tank) in _tanks)
        {
            states.Add(new TankState(
                slot, tank.Position.X, tank.Position.Y, tank.Rotation, tank.TurretRotation,
                (byte)tank.Hp, (byte)tank.Team, (byte)tank.Shield, (byte)tank.Layer, (byte)tank.LivesRemaining));
        }

        var deltas = _pendingWallDeltas.Count == 0
            ? (IReadOnlyList<WallDelta>)System.Array.Empty<WallDelta>()
            : new List<WallDelta>(_pendingWallDeltas);
        _pendingWallDeltas.Clear();

        // Every live shot rides the snapshot so a guest sees it in flight (ADR-0019 step 4). The
        // heading is the angle the view points a missile along; bullets ignore it.
        var projectiles = new List<Domain.Net.ProjectileState>();
        foreach (var entity in _world.Entities)
        {
            if (entity is IProjectile shot)
            {
                projectiles.Add(new Domain.Net.ProjectileState(
                    shot.Position.X, shot.Position.Y,
                    System.MathF.Atan2(shot.Direction.X, shot.Direction.Y),
                    (byte)shot.Style, (byte)shot.Layer));
            }
        }

        // Every live pickup rides the snapshot (id/kind/cell/available) so a guest can mirror the
        // boost director's drops; collection resolves here, in the authoritative world.
        var pickups = new List<Domain.Net.PowerupState>();
        foreach (var entity in _world.Entities)
        {
            if (entity is IPowerup pickup)
            {
                pickups.Add(new Domain.Net.PowerupState(
                    IdFor(pickup.Id), (byte)pickup.Kind,
                    (ushort)(pickup.Position.X / PickupTileSize),
                    (ushort)(pickup.Position.Y / PickupTileSize),
                    (byte)(pickup.IsAvailable ? 1 : 0)));
            }
        }

        // One reconciliation anchor per human guest — the snapshot is a broadcast, so every
        // predicting guest must find its own slot's ack in it.
        var acks = new List<InputAck>(_guestInputs.Count);
        foreach (var (slot, source) in _guestInputs)
        {
            acks.Add(new InputAck(slot, source.LastAppliedSeq));
        }

        _transport.SendSnapshot(new SnapshotFrame(Tick, acks, states, deltas, projectiles)
        {
            Powerups = pickups,
        });
    }

    private ushort IdFor(System.Guid pickupId)
    {
        if (!_pickupIds.TryGetValue(pickupId, out var id))
        {
            _pickupIds[pickupId] = id = _nextPickupId++;
        }

        return id;
    }
}
