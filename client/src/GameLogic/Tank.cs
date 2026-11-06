using System;
using System.Collections.Generic;
using System.Numerics;
using TankGame.Domain;

namespace TankGame.GameLogic;

/// <summary>A locally-driven tank — a world <see cref="IEntity"/>. Pure C#: reads an
/// <see cref="IInputSource"/> each <see cref="Step"/>, moves at a constant speed, faces its
/// movement direction, aims its turret at the input, and (rate-limited) spawns a
/// <see cref="Projectile"/> into the <see cref="IWorld"/> when fire is held. No Godot — the
/// Presentation layer renders this state.</summary>
public sealed class Tank : ITank
{
    private readonly IInputSource _input;
    private readonly IWorld _world;
    private readonly IArena _arena;
    private readonly ITerrain? _terrain;
    private readonly ITeleporter? _teleporter;
    private readonly Vector2 _spawnPosition;
    private readonly int _spawnLayer;
    private readonly Func<Vector2>? _respawnPoint;
    private readonly Stats _stats;
    private readonly float _projectileSpeed;
    private float _fireCooldown;
    private float _pushTimer;
    private int _livesRemaining;
    private float _respawnTimer;
    private bool _airborne;
    private float _altitude;
    private float _fallSpeed;
    private int _fallTargetLayer;
    private readonly AmmoLoadout _ammo = new();

    /// <param name="input">Per-frame intent source.</param>
    /// <param name="world">The world the tank spawns its shots into.</param>
    /// <param name="arena">Collision source handed to spawned projectiles.</param>
    /// <param name="startPosition">Initial world-space position.</param>
    /// <param name="speed">Movement speed in units per second.</param>
    /// <param name="fireInterval">Minimum seconds between shots.</param>
    /// <param name="projectileSpeed">Speed of spawned projectiles, units per second.</param>
    /// <param name="maxHp">Hit points at full health.</param>
    /// <param name="team">The side this tank fights for (its shots spare the same team).</param>
    /// <param name="lives">Total lives, including the current one: a tank revives at its spawn
    /// after each death until its lives run out, then the world reaps it. Default 1 = no respawn.</param>
    /// <param name="layer">The elevation layer the tank fights on (ADR-0018): it only hits and is hit
    /// by tanks on the same layer, and changes layer only by crossing a ramp. Default 0 = the ground
    /// layer (a flat, single-layer arena).</param>
    /// <param name="displayName">The name shown above the tank (the player's chosen name or an AI's
    /// generated one). Blank = no name tag.</param>
    /// <param name="respawnPoint">Where a revive places the tank. Custom maps deal a fresh random
    /// spawn marker per respawn, so the point is asked for anew on every revive; null (the default,
    /// and the built-in arenas) keeps the fixed original spawn.</param>
    public Tank(
        IInputSource input,
        IWorld world,
        IArena arena,
        Vector2 startPosition,
        float speed,
        float fireInterval,
        float projectileSpeed,
        int maxHp = 3,
        int team = 0,
        int lives = 1,
        ITerrain? terrain = null,
        int layer = 0,
        ITeleporter? teleporter = null,
        string displayName = "",
        Func<Vector2>? respawnPoint = null)
    {
        Id = EntityId.Next();
        MaxHp = maxHp;
        Hp = maxHp;
        Team = team;
        Layer = layer;
        DisplayName = displayName;
        _livesRemaining = lives;
        _input = input;
        _world = world;
        _arena = arena;
        _terrain = terrain;
        _teleporter = teleporter;
        Position = startPosition;
        _spawnPosition = startPosition;
        _spawnLayer = layer;
        _respawnPoint = respawnPoint;
        _stats = new Stats(new Dictionary<StatKind, float>
        {
            [StatKind.Speed] = speed,
            [StatKind.FireInterval] = fireInterval,
            [StatKind.Damage] = 1f,
        });
        _projectileSpeed = projectileSpeed;
    }

    /// <summary>Applies a timed <see cref="StatusEffect"/> to this tank (a powerup or trap):
    /// it modifies the matching stat until it expires. See
    /// <c>docs/adr/0012-stats-and-status-effects.md</c>.</summary>
    public void ApplyEffect(StatusEffect effect) => _stats.Apply(effect);

    /// <summary>Applies an ammo crate: the modifier sets its axis of the loadout (spread or
    /// behaviour) — leaving the others, so pickups stack. The loadout then fires that special shot
    /// for every shot, with no shot limit, until the tank dies and sheds it (a death resets the
    /// loadout to the default straight shot). See <c>docs/adr/0016-composable-ammo.md</c>.</summary>
    public void LoadAmmo(AmmoModifier modifier) => modifier.ApplyTo(_ammo);

    /// <summary>Half-extent of the tank used for wall collision: its leading edge, not its
    /// centre, is what must clear a wall.</summary>
    public const float CollisionRadius = 24f;

    /// <summary>Seconds of sustained pushing between each point of demolition damage. Three
    /// hits (a full-hp brick) break in <c>3 ×</c> this — about 1.2 s of shoving.</summary>
    public const float PushInterval = 0.4f;
    private const int PushDamage = 1;

    /// <summary>Seconds a tank stays down after a death before it revives at its spawn point
    /// (only while it still has lives left).</summary>
    public const float RespawnDelay = 2f;

    /// <summary>At or below this fraction of <see cref="MaxHp"/> a tank is badly wounded: it crawls
    /// (its speed scales by <see cref="LowHealthSpeedFactor"/>) and the view trails dark smoke.</summary>
    public const float LowHealthFraction = 0.4f;
    private const float LowHealthSpeedFactor = 0.6f;

    /// <summary>Downward acceleration while falling off a ledge, in layers per second² (ADR-0020
    /// Wave B). 10 lands a one-layer drop in ~0.45 s.</summary>
    public const float FallGravity = 10f;

    public Guid Id { get; }
    public Vector2 Position { get; private set; }
    public float Rotation { get; private set; }
    public float TurretRotation { get; private set; }

    public int Hp { get; private set; }
    public int MaxHp { get; }
    public int Team { get; }
    public int Shield { get; private set; }
    public string DisplayName { get; }

    /// <summary>The elevation layer this tank fights on (ADR-0018): combat and (later) collision act
    /// only within a layer, and it changes only by crossing a ramp. 0 is the ground layer.</summary>
    public int Layer { get; private set; }

    public float Altitude => _airborne ? _altitude : Layer;
    public bool IsAirborne => _airborne;

    // "In the match": a tank with positive hit points is fighting; one at zero hit points is
    // down but still in the match while it has a life left to respawn. Only once both are gone
    // does the world reap it. Combat, collision, and the view instead key off Hp > 0 (whether
    // it is a tangible, fightable tank right now).
    public bool IsAlive => Hp > 0 || _livesRemaining > 0;

    /// <summary>Lives left including the current one — starts at the constructor's <c>lives</c> and drops
    /// by one on each death. The HUD reads this to show respawns remaining (<c>LivesRemaining - 1</c>: the
    /// future revives, since one life is the tank fighting now).</summary>
    public int LivesRemaining => _livesRemaining;

    /// <summary>Raised with the hit points actually restored by a heal (never the over-heal excess)
    /// — <see cref="BattleStats"/> tallies it for the victory screen.</summary>
    public event Action<int>? Healed;

    /// <summary>Restores hit points up to <see cref="MaxHp"/> (a repair pickup). A no-op on a
    /// downed tank — repair cannot revive; only the respawn timer does.</summary>
    public void Heal(int amount)
    {
        if (amount <= 0 || Hp <= 0)
        {
            return;
        }

        var restored = Math.Min(MaxHp - Hp, amount);
        Hp += restored;
        if (restored > 0)
        {
            Healed?.Invoke(restored);
        }
    }

    /// <summary>Adds over-shield points (a shield pickup): a buffer that incoming damage spends
    /// before hit points. Stacks, but the total is capped at <see cref="MaxHp"/>.</summary>
    public void AddShield(int amount)
    {
        if (amount > 0)
        {
            Shield = Math.Min(Shield + amount, MaxHp);
        }
    }

    public void TakeDamage(int amount)
    {
        if (amount <= 0 || Hp <= 0)
        {
            return; // no damage, or already down (the kill was already credited)
        }

        if (Shield > 0)
        {
            var absorbed = Math.Min(Shield, amount);
            Shield -= absorbed;
            amount -= absorbed;
            if (amount == 0)
            {
                return; // the shield soaked the whole hit
            }
        }

        Hp = Math.Max(0, Hp - amount);
        if (Hp == 0)
        {
            // Death sheds every pickup: the held ammo reverts to the plain shot and all buffs drop,
            // so the tank respawns bare. The pickups themselves fall back onto the field where it died.
            _ammo.Reset();
            _stats.Clear();
            _livesRemaining--; // spend the life just lost; respawn if any remain
            if (_livesRemaining > 0)
            {
                _respawnTimer = RespawnDelay;
            }
        }
    }

    public void Step(float deltaSeconds)
    {
        _stats.Step(deltaSeconds); // age timed effects in real time, even while down

        if (Hp <= 0)
        {
            // Down: inert (no movement, aim, or fire) until the respawn timer elapses, then
            // revive at the spawn point. A tank out of lives has no timer and simply waits to
            // be reaped.
            if (_respawnTimer > 0f)
            {
                _respawnTimer -= deltaSeconds;
                if (_respawnTimer <= 0f)
                {
                    Hp = MaxHp;
                    Position = _respawnPoint?.Invoke() ?? _spawnPosition;
                    Layer = _spawnLayer; // back on the spawn cell's level, grounded — even if it died mid-fall
                    _airborne = false;
                    _fallSpeed = 0f;
                    _fireCooldown = 0f;
                    _pushTimer = 0f;
                }
            }

            return;
        }

        var input = _input.Read();

        var move = input.Move;
        if (move.LengthSquared() > 1f)
        {
            move = Vector2.Normalize(move); // a (1,1) keyboard diagonal must not be faster
        }

        // Terrain underfoot scales the speed — sandbags slow a tank crossing them.
        var speed = _stats.Current(StatKind.Speed) * (_terrain?.SpeedFactorAt(Position) ?? 1f);
        if (Hp <= LowHealthFraction * MaxHp)
        {
            speed *= LowHealthSpeedFactor; // a badly-wounded tank limps
        }

        var desired = Position + (move * speed * deltaSeconds);

        // Resolve each axis independently so the tank slides along a wall instead of sticking.
        // A move is allowed only if the leading edge (centre + radius) clears walls and the new
        // centre does not overlap another tank. Only wall blocks feed push-to-demolish — bumping
        // a tank must never chip a wall behind it, so wall and tank blocks are tracked apart.
        var wallX = false;
        var nextX = Position.X;
        if (move.X != 0f)
        {
            var hitWall = MoveBlocked(new Vector2(desired.X + (MathF.Sign(move.X) * CollisionRadius), Position.Y));
            if (!hitWall && !OverlapsAnotherTank(new Vector2(desired.X, Position.Y)))
            {
                nextX = desired.X;
            }
            else
            {
                wallX = hitWall;
            }
        }

        var wallY = false;
        var nextY = Position.Y;
        if (move.Y != 0f)
        {
            var hitWall = MoveBlocked(new Vector2(nextX, desired.Y + (MathF.Sign(move.Y) * CollisionRadius)));
            if (!hitWall && !OverlapsAnotherTank(new Vector2(nextX, desired.Y)))
            {
                nextY = desired.Y;
            }
            else
            {
                wallY = hitWall;
            }
        }

        var previousPosition = Position;
        Position = new Vector2(nextX, nextY);

        if (_airborne)
        {
            // Mid-fall (ADR-0020 Wave B): the altitude sweeps down under fixed-step gravity while the
            // source Layer holds (combat keeps treating the tank as where it left); ramps and teleport
            // pads cannot grab a tank in the air.
            _fallSpeed += FallGravity * deltaSeconds;
            _altitude -= _fallSpeed * deltaSeconds;
            if (_altitude <= _fallTargetLayer)
            {
                _altitude = _fallTargetLayer;
                Layer = _fallTargetLayer;
                _airborne = false;
                _fallSpeed = 0f;
            }
        }
        else
        {
            // Driving onto a ramp carries the tank up or down to the layer it connects.
            Layer = _arena.LayerAfterMove(previousPosition, Position, Layer);

            if (_arena.DropTargetAt(Position, Layer) is int dropTo)
            {
                // The tank's centre crossed a ledge: leave the ground toward the lower layer.
                _airborne = true;
                _altitude = Layer;
                _fallSpeed = 0f;
                _fallTargetLayer = dropTo;
            }
            else if (_teleporter is not null &&
                _teleporter.TryTeleport(Position, Layer, out var warpTo, out var warpLayer))
            {
                // Driving onto a ready teleport pad warps the tank to its linked pad (and its layer).
                Position = warpTo;
                Layer = warpLayer;
            }
        }

        PushAgainstWall(wallX, wallY, move, deltaSeconds);

        // Lethal terrain underfoot (lava): a grounded tank whose cell is lethal is destroyed outright —
        // the shield can't soak it. A tank falling over lava (airborne) is spared until it lands. Shots
        // fly over lava (BlocksShots is false), and a bridge cell reads as Bridge, so it crosses safely.
        if (!_airborne && CellMaterials.IsLethal(_arena.MaterialAt(Position)))
        {
            TakeDamage(Hp + Shield);
        }

        if (move != Vector2.Zero)
        {
            Rotation = MathF.Atan2(move.Y, move.X); // chassis keeps its last facing when idle
        }

        TurretRotation = input.Aim;

        _fireCooldown -= deltaSeconds;
        if (_fireCooldown <= 0f && input.Fire && !_airborne)
        {
            _fireCooldown = _stats.Current(StatKind.FireInterval);
            var direction = new Vector2(MathF.Cos(TurretRotation), MathF.Sin(TurretRotation));

            // The loadout fires its current shot — the special ammo for as long as it is held (until
            // the tank dies and resets the loadout), else the default straight shot.
            _ammo.Fire(_world, _arena, Position, direction, _projectileSpeed, Team, Id, Layer,
                damageMult: _stats.Current(StatKind.Damage));
        }
    }

    // Driving into a destructible wall demolishes it: while the tank is jammed against a wall
    // it is pushing into, accumulate time and chip that wall once per PushInterval. DamageAt
    // only affects brick, so steel and the border shrug it off. Releasing the stick or
    // breaking through resets the timer, so demolition needs sustained pressure, not a tap.
    private void PushAgainstWall(bool wallX, bool wallY, Vector2 move, float deltaSeconds)
    {
        if (!wallX && !wallY)
        {
            _pushTimer = 0f;
            return;
        }

        _pushTimer += deltaSeconds;
        if (_pushTimer < PushInterval)
        {
            return;
        }

        _pushTimer -= PushInterval;

        var (point, direction) = wallX
            ? (new Vector2(Position.X + (MathF.Sign(move.X) * CollisionRadius), Position.Y),
                new Vector2(MathF.Sign(move.X), 0f))
            : (new Vector2(Position.X, Position.Y + (MathF.Sign(move.Y) * CollisionRadius)),
                new Vector2(0f, MathF.Sign(move.Y)));

        _arena.DamageAt(point, direction, PushDamage, MoveLayer);
    }

    // Movement collides on the layer the tank is (or is about to be) standing on: the source layer
    // when grounded, the landing layer mid-fall — so a falling tank cannot steer back into the cliff
    // it left. A grounded block is forgiven when the arena says the tank can drop off a ledge there.
    private int MoveLayer => _airborne ? _fallTargetLayer : Layer;

    private bool MoveBlocked(Vector2 leadingEdge) =>
        _arena.IsBlocked(leadingEdge, MoveLayer) &&
        (_airborne || _arena.DropTargetAt(leadingEdge, Layer) is null);

    // A tank is a circle of CollisionRadius; two tanks may not overlap. Scans the world for any
    // other live tank whose centre is within a tank-diameter of this candidate centre. Order is
    // tick-order-dependent (the tank that steps first claims the ground), which is fine for a
    // body block — no momentum is transferred, the mover simply stops. Tanks already overlapping (a
    // teleport landing, say) may still move in any direction that separates them; without that, every
    // candidate overlaps and both are pinned for good.
    private bool OverlapsAnotherTank(Vector2 candidateCentre)
    {
        var minDistance = CollisionRadius * 2f;
        foreach (var entity in _world.Entities)
        {
            if (entity is not ITank other || ReferenceEquals(other, this) || other.Hp <= 0)
            {
                continue;
            }

            var candidateGap = Vector2.DistanceSquared(candidateCentre, other.Position);
            if (candidateGap < minDistance * minDistance
                && candidateGap <= Vector2.DistanceSquared(Position, other.Position))
            {
                return true;
            }
        }

        return false;
    }
}
