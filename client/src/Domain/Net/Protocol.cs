using System.Collections.Generic;

namespace TankGame.Domain.Net;

/// <summary>One client→server input for a simulation tick. <see cref="Seq"/> is the client's
/// monotonically-increasing input number, echoed back in <see cref="SnapshotFrame.Acks"/> so the
/// client can discard acknowledged inputs and re-predict the rest (reconciliation). Move is the
/// raw stick/keys intent; <see cref="Aim"/> is the turret angle in radians; <see cref="Buttons"/>
/// is a bitfield (<see cref="FireBit"/>). <see cref="Slot"/> is the sender — with up to 4 players
/// the host must attribute each relayed frame; the relay overwrites it with the sender socket's
/// real slot, so a client can only ever act as itself. The wire layout is fixed and mirrored
/// byte-for-byte by the TypeScript server (see <c>server/worker/src/protocol/</c> and ADR-0005).</summary>
public readonly record struct InputFrame(uint Seq, float MoveX, float MoveY, float Aim, byte Buttons, byte Slot = 0)
{
    /// <summary><see cref="Buttons"/> bit set while fire is held.</summary>
    public const byte FireBit = 1 << 0;

    public bool Fire => (Buttons & FireBit) != 0;
}

/// <summary>A single tank's authoritative state in a snapshot. <see cref="Slot"/> is the player
/// slot (0 = host, 1 = guest) — a stable per-match id the client pairs with its view.
/// <see cref="Shield"/> is over-shield points (a guest must see a shielded remote tank, ADR-0019
/// step 4) and <see cref="Layer"/> is the elevation layer the tank stands on (ADR-0018), so a
/// mirrored tank renders at the right height. <see cref="Lives"/> counts lives left including the
/// current one: a tank at 0 hp with lives left is waiting to respawn, at 0 lives it is out.</summary>
public readonly record struct TankState(
    byte Slot, float X, float Y, float Rotation, float TurretRotation, byte Hp, byte Team,
    byte Shield, byte Layer, byte Lives = 0)
{
    /// <summary>A tank state with no shield, on the ground layer — the pre-step-4 shape, kept so the
    /// call sites and tests that predate networked shield/elevation stay terse.</summary>
    public TankState(byte slot, float x, float y, float rotation, float turretRotation, byte hp, byte team)
        : this(slot, x, y, rotation, turretRotation, hp, team, 0, 0)
    {
    }
}

/// <summary>A change to one wall cell since the last snapshot — so a brick chipped or broken on
/// the server appears for both clients. <see cref="Material"/> is 0 floor / 1 brick / 2 steel.</summary>
public readonly record struct WallDelta(ushort CellX, ushort CellY, byte Material, byte Hp);

/// <summary>One live shot's authoritative state in a snapshot, so a guest can see shots in flight
/// (ADR-0019 step 4) — without this a guest only ever saw tanks teleport between hits. The full live
/// set rides every snapshot; the guest mirrors it as throwaway view-models. <see cref="Rotation"/> is
/// the travel heading (<c>Atan2(dir.X, dir.Y)</c>, the angle the view points a missile along);
/// <see cref="Style"/> is 0 bullet / 1 missile; <see cref="Layer"/> is the elevation layer the shot
/// rides (ADR-0018).</summary>
public readonly record struct ProjectileState(float X, float Y, float Rotation, byte Style, byte Layer);

/// <summary>The last <see cref="InputFrame.Seq"/> the host applied for one guest slot — the
/// snapshot is a broadcast, so every predicting guest finds its own reconciliation anchor here.</summary>
public readonly record struct InputAck(byte Slot, uint Seq);

/// <summary>One live pickup's authoritative state in a snapshot, so a guest sees the crates the
/// host's boost director drops. <see cref="Id"/> is a small host-assigned handle stable for the
/// pickup's lifetime (a guest keys its mirror views on it); <see cref="Kind"/> is the
/// <c>PowerupKind</c> ordinal; the cell locates it (director drops sit on cell centres and never
/// move); <see cref="Available"/> is 0 while dormant (a carried or cooling-down pickup).</summary>
public readonly record struct PowerupState(ushort Id, byte Kind, ushort CellX, ushort CellY, byte Available);

/// <summary>One server→client world snapshot at <see cref="Tick"/>. <see cref="Acks"/> carries a
/// per-guest-slot reconciliation anchor (the snapshot is broadcast to all guests, so each finds its
/// own — see <see cref="AckFor"/>). Carries the full tank set, the live projectiles, plus any wall
/// changes this snapshot.</summary>
public sealed record SnapshotFrame(
    uint Tick, IReadOnlyList<InputAck> Acks, IReadOnlyList<TankState> Tanks,
    IReadOnlyList<WallDelta> WallDeltas, IReadOnlyList<ProjectileState> Projectiles)
{
    /// <summary>The live pickups on the host's field (boost-director drops). An init property with an
    /// empty default — not a positional parameter — so every pre-pickup call site and byte layout
    /// stays valid; the codec appends it as a tolerated trailing section.</summary>
    public IReadOnlyList<PowerupState> Powerups { get; init; } = System.Array.Empty<PowerupState>();

    /// <summary>A snapshot acking the single 2-player-era guest — kept so the many call sites and
    /// tests that predate 4-player rooms stay terse. The one ack is anchored on both low slots,
    /// because in the 2-player era "the guest" is whichever of them the receiver happens to be.</summary>
    public SnapshotFrame(
        uint tick, uint ackSeq, IReadOnlyList<TankState> tanks, IReadOnlyList<WallDelta> wallDeltas,
        IReadOnlyList<ProjectileState> projectiles)
        : this(tick, new[] { new InputAck(0, ackSeq), new InputAck(1, ackSeq) }, tanks, wallDeltas, projectiles)
    {
    }

    /// <summary>A snapshot with no projectiles — the pre-step-4 shape, kept for the same reason.</summary>
    public SnapshotFrame(
        uint tick, uint ackSeq, IReadOnlyList<TankState> tanks, IReadOnlyList<WallDelta> wallDeltas)
        : this(tick, ackSeq, tanks, wallDeltas, System.Array.Empty<ProjectileState>())
    {
    }

    /// <summary>The 2-player-era single ack: the first (and, pre-4-player, only) entry. Legacy
    /// call sites and tests read this; new code asks <see cref="AckFor"/> its own slot.</summary>
    public uint AckSeq => Acks.Count > 0 ? Acks[0].Seq : 0;

    /// <summary>The reconciliation anchor for <paramref name="slot"/>, or 0 when this snapshot
    /// carries none for it (the guest simply replays all pending inputs).</summary>
    public uint AckFor(byte slot)
    {
        foreach (var ack in Acks)
        {
            if (ack.Slot == slot)
            {
                return ack.Seq;
            }
        }

        return 0;
    }
}
