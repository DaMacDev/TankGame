using System.Collections.Generic;
using System.Numerics;
using TankGame.Domain;
using TankGame.Domain.Net;

namespace TankGame.GameLogic;

/// <summary>Client-side prediction + reconciliation for the local player's tank (M3-T7). The
/// host is authoritative, but waiting a round-trip before the local tank moves would feel laggy,
/// so the guest <see cref="Predict"/>s each input immediately and buffers it. When a snapshot
/// arrives, <see cref="Reconcile"/> snaps the tank to the host's authoritative state and replays
/// only the inputs the host has not yet acknowledged — so a correct prediction is invisible and a
/// wrong one is pulled smoothly toward the truth.
///
/// The movement model mirrors the host's real GameLogic <see cref="Tank"/> exactly (ADR-0019
/// step 4): each tick advances by <c>move · speed · dt</c> (the same per-tick distance the host's
/// <see cref="Tank.Step"/> produces when stepped at the guest's fixed tick), resolves each axis
/// independently, allows a move only when the leading edge (centre + <see cref="Tank.CollisionRadius"/>)
/// clears a wall, faces the movement direction, and aims the turret at the input. Because the host
/// runs the real <see cref="Tank"/> at the same 20 Hz tick (see <c>NetArena3DScene</c>), replaying
/// the same inputs reproduces the same path, so reconciliation does not jitter. Pure C#: the wall
/// source is the same <see cref="IArena"/> the host drives against; no Godot, no transport (the
/// caller wires snapshot delivery to <see cref="Reconcile"/>).</summary>
public sealed class PredictedTank
{
    private readonly IArena _arena;
    private readonly float _speed;
    private readonly float _tickSeconds;
    private readonly List<InputFrame> _pending = new();

    /// <param name="slot">This tank's authoritative slot (0 host / 1 guest) — the one to read from
    /// each snapshot and the inputs of which the host acknowledges.</param>
    /// <param name="arena">Wall source for collision, the same the host's <see cref="Tank"/> drives
    /// against.</param>
    /// <param name="spawn">Initial predicted position until the first snapshot arrives.</param>
    /// <param name="speed">Movement speed in units per second — must equal the host tank's speed so a
    /// replayed input covers the same distance. Defaults to 200, the net arena's tank speed.</param>
    /// <param name="tickSeconds">The fixed tick the prediction (and the host) steps at — must equal the
    /// host's per-step dt so per-tick distance matches. Defaults to 1/20 s (20 Hz, the net cadence).</param>
    public PredictedTank(byte slot, IArena arena, Vector2 spawn, float speed = 200f, float tickSeconds = 1f / 20f)
    {
        Slot = slot;
        _arena = arena;
        Position = spawn;
        _speed = speed;
        _tickSeconds = tickSeconds;
    }

    public byte Slot { get; }
    public Vector2 Position { get; private set; }
    public float Rotation { get; private set; }
    public float TurretRotation { get; private set; }

    /// <summary>Authoritative hit points from the last reconciled snapshot.</summary>
    public int Hp { get; private set; }

    /// <summary>Authoritative lives left (including the current one) from the last reconciled snapshot.</summary>
    public int Lives { get; private set; }

    /// <summary>Authoritative team from the last reconciled snapshot.</summary>
    public int Team { get; private set; }

    /// <summary>Authoritative over-shield points from the last reconciled snapshot (ADR-0019 step 4).</summary>
    public int Shield { get; private set; }

    /// <summary>Authoritative elevation layer from the last reconciled snapshot (ADR-0018).</summary>
    public int Layer { get; private set; }

    /// <summary>Inputs sent to the server but not yet acknowledged by a snapshot — the ones replayed
    /// on top of each authoritative correction.</summary>
    public int PendingInputCount => _pending.Count;

    /// <summary>Applies one tick of local intent: buffers it (so it can be replayed after a
    /// correction) and advances the predicted transform immediately.</summary>
    public void Predict(InputFrame input)
    {
        _pending.Add(input);
        if (!IsDown)
        {
            ApplyMovement(input);
        }
    }

    // A downed tank waits for its respawn and the host ignores its input, so prediction holds still.
    private bool IsDown => _reconciled && Hp <= 0;

    private bool _reconciled;

    /// <summary>Reconciles against an authoritative snapshot: snap this slot's transform, health,
    /// shield and elevation to the server, drop every input the server has acknowledged, and replay the rest so the
    /// predicted position stays ahead of the acknowledged truth. A snapshot that does not carry this
    /// slot is ignored (nothing authoritative to correct against).</summary>
    public void Reconcile(SnapshotFrame snapshot)
    {
        if (!TryFindSlot(snapshot, out var authoritative))
        {
            return;
        }

        Position = new Vector2(authoritative.X, authoritative.Y);
        Rotation = authoritative.Rotation;
        TurretRotation = authoritative.TurretRotation;
        Hp = authoritative.Hp;
        Lives = authoritative.Lives;
        _reconciled = true;
        Team = authoritative.Team;
        Shield = authoritative.Shield;
        Layer = authoritative.Layer;

        var acked = snapshot.AckFor(Slot); // the broadcast carries one anchor per guest — read ours
        _pending.RemoveAll(frame => frame.Seq <= acked);
        if (IsDown)
        {
            return;
        }

        foreach (var frame in _pending)
        {
            ApplyMovement(frame);
        }
    }

    private bool TryFindSlot(SnapshotFrame snapshot, out TankState state)
    {
        foreach (var tank in snapshot.Tanks)
        {
            if (tank.Slot == Slot)
            {
                state = tank;
                return true;
            }
        }

        state = default;
        return false;
    }

    // The host Tank's Step movement, reproduced exactly (ADR-0019 step 4): clamp the intent to unit
    // magnitude, advance by move·speed·dt, resolve each axis independently and only if the leading edge
    // (centre + Tank.CollisionRadius) clears a wall, face the movement direction, and aim the turret at
    // the input. The Y leading edge is checked against the X already resolved this tick — Tank's
    // axis-separated wall slide — so a tank slides along a wall instead of sticking.
    private void ApplyMovement(InputFrame input)
    {
        var move = new Vector2(input.MoveX, input.MoveY);
        if (move.LengthSquared() > 1f)
        {
            move = Vector2.Normalize(move);
        }

        var desired = Position + (move * _speed * _tickSeconds);

        var position = Position;
        if (move.X != 0f &&
            !_arena.IsBlocked(new Vector2(desired.X + (System.MathF.Sign(move.X) * Tank.CollisionRadius), position.Y)))
        {
            position.X = desired.X;
        }
        if (move.Y != 0f &&
            !_arena.IsBlocked(new Vector2(position.X, desired.Y + (System.MathF.Sign(move.Y) * Tank.CollisionRadius))))
        {
            position.Y = desired.Y;
        }
        Position = position;

        if (move != Vector2.Zero)
        {
            Rotation = System.MathF.Atan2(move.Y, move.X);
        }
        TurretRotation = input.Aim;
    }
}
