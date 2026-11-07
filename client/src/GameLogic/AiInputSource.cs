using System;
using System.Collections.Generic;
using System.Numerics;
using TankGame.Domain;

namespace TankGame.GameLogic;

/// <summary>A per-match, per-tank AI temperament. Rolled from a seeded <see cref="Random"/> so every
/// bot in a match behaves differently — one charges (high <see cref="Aggression"/>), one hoards crates
/// (<see cref="Greed"/>), one lurks and picks its distance (<see cref="Caution"/>), one chases every
/// gunshot (<see cref="Curiosity"/>), one just roams (<see cref="Wanderlust"/>). Weights multiply the
/// normalized utility of each candidate action in the state machine; <see cref="Vision"/> and
/// <see cref="Standoff"/> tune how far it sees and how close it fights. Weights are unitless multipliers
/// centred near 1; the two ranges are in world units.</summary>
public readonly record struct AiPersonality(
    float Aggression,
    float Greed,
    float Caution,
    float Curiosity,
    float Wanderlust,
    float Vision,
    float Standoff)
{
    // Bounds are deliberately narrow on Vision/Standoff so the varied bots still respect the arena's
    // engagement ranges (a tank must see an enemy at ~600 and hold outside ~100 / advance from ~300).
    /// <summary>Rolls a fresh random temperament. Same <paramref name="rng"/> state → same result.</summary>
    public static AiPersonality Roll(Random rng) => new(
        Aggression: Range(rng, 0.6f, 1.6f),
        Greed: Range(rng, 0.4f, 1.5f),
        Caution: Range(rng, 0.3f, 1.4f),
        Curiosity: Range(rng, 0.4f, 1.4f),
        Wanderlust: Range(rng, 0.4f, 1.3f),
        Vision: Range(rng, 620f, 720f),
        Standoff: Range(rng, 130f, 200f));

    private static float Range(Random rng, float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);
}

/// <summary>Drives an enemy tank by emitting the same <see cref="TankInput"/> intent a human would —
/// the multiplayer-safe seam. AI runs host-only (guests mirror host snapshots), so its randomness is
/// free; it is kept <em>seedable</em> only for reproducible tests. Each tick it scores the actions it
/// could take — Hunt a visible enemy, Seek a pickup, Investigate distant gunfire, or Wander — as
/// <c>personalityWeight × normalizedUtility</c>, commits to the winning state for a short window
/// (hysteresis, so it doesn't flip-flop each tick), and picks <em>which</em> enemy/pickup by
/// closeness-weighted random rather than always the nearest, so two bots in the same spot diverge.
/// With nothing to do it roams across the map, usually toward the players' area, so no player drives
/// around alone for long; after <see cref="EnemyCompass.QuietSeconds"/> without a fight it heads straight
/// for its nearest enemy (the same nudge the HUD arrows give a player).
/// A visible enemy within firing range always overrides to engage-and-fire. Line-of-sight, bush
/// concealment, and vision-range gating are unchanged; with a wall grid it routes around cover via A*.
/// Pure C#: no Godot. Bind it to the tank it drives after construction.</summary>
public sealed class AiInputSource : IInputSource
{
    private const float FireRange = 420f;
    private const float BushRevealRange = 96f;
    private const float PowerupSeekRange = 700f;
    private const float AmbushSeekRange = 600f;
    private const float EarshotRange = 900f;
    private const int WanderTicks = 75;
    private const float RepathSeconds = 2f;
    private const float RoamGiveUpSeconds = 30f;
    private const int PlayerAreaSpreadCells = 4;
    private const int FarCellTries = 12;
    private const int MinRoamCells = 12;
    private const float RetaliateSeconds = 4f;

    // Ticks a tank stays committed to a chosen state before it is allowed to re-evaluate, so a bot
    // pursues one intent rather than dithering between a pickup and a fight every frame.
    private const int StateCommitTicks = 20;

    private enum AiState { Wander, Hunt, SeekPowerup, Investigate }

    private readonly IWorld _world;
    private readonly IArena _arena;
    private readonly IConcealment? _concealment;
    private readonly bool _ambusher;
    private readonly IWallGrid? _grid;
    private readonly float _tileSize;
    private readonly Vector2 _origin;
    private readonly AiPersonality? _explicitPersonality;
    private readonly int? _seed;
    private readonly DifficultyPreset _preset;
    private readonly IReadOnlyList<ITank>? _players;
    private readonly bool _respectTeams;
    private readonly int _quietTicksLimit;
    private readonly int _repathTicks;
    private readonly int _roamGiveUpTicks;
    private readonly int _retaliateTicks;

    private int _quietTicks;
    private int _ticksSinceHurt = int.MaxValue;
    private int _lastHealth;
    private Vector2? _roamGoal;
    private IReadOnlyList<(int X, int Y)> _route = Array.Empty<(int, int)>();
    private int _routeIndex;
    private int _routeAge;
    private int _repathCountdown;
    private Vector2 _progressMark;

    private ITank? _self;
    private Random _rng = new();
    private AiPersonality _personality; // rolled in Bind; Read() no-ops until _self is set, so default is never used
    private AiState _state = AiState.Wander;
    private int _stateCooldown;
    private ITank? _target;
    private IPowerup? _committedPowerup;
    private Vector2 _wanderDirection;
    private int _wanderCooldown;

    /// <param name="ambusher">When true (and concealment exists), this tank prefers to slip into grass
    /// and snipe from cover rather than charge. Bind it after construction.</param>
    /// <param name="grid">The wall grid to path around. Omit (null) to disable pathfinding.</param>
    /// <param name="tileSize">World-space size of one tile. Ignored when <paramref name="grid"/> is null.</param>
    /// <param name="origin">World position of cell (0,0)'s minimum corner.</param>
    /// <param name="personality">A pre-built temperament. Omit to roll one from <paramref name="seed"/>
    /// (or the tank id) at <see cref="Bind"/> — the wiring slice can pass an explicit one per slot.</param>
    /// <param name="seed">Seeds the per-tank RNG (personality roll, target/pickup choice, wander heading).
    /// The host passes <c>matchSeed ^ slot</c> so each bot differs yet a match is reproducible. Omit to
    /// derive from the tank id, as before.</param>
    /// <param name="difficulty">Bot skill preset: scales vision and (on Easy) adds a seeded aim error
    /// when firing, and sets how often a roaming bot heads for the players.</param>
    /// <param name="players">The human players' tanks: a roaming bot is drawn to their area, and while
    /// one lives the bot only fights other bots when shot at. May still be filling up when passed. Null:
    /// every other tank is equally a target.</param>
    /// <param name="respectTeams">True in Team mode: tanks on this bot's team are never targets. False
    /// where bots share a team yet fight each other (solo) or every tank has its own team (FFA).</param>
    /// <param name="ticksPerSecond">How often <see cref="Read"/> is called, so the time-based roaming
    /// rules hold at both the solo (60 Hz) and networked (20 Hz) tick.</param>
    public AiInputSource(
        IWorld world,
        IArena arena,
        IConcealment? concealment = null,
        bool ambusher = false,
        IWallGrid? grid = null,
        float tileSize = 0f,
        Vector2 origin = default,
        AiPersonality? personality = null,
        int? seed = null,
        Difficulty difficulty = Difficulty.Normal,
        IReadOnlyList<ITank>? players = null,
        bool respectTeams = false,
        float ticksPerSecond = 60f)
    {
        _world = world;
        _arena = arena;
        _concealment = concealment;
        _ambusher = ambusher;
        _grid = grid;
        _tileSize = tileSize;
        _origin = origin;
        _explicitPersonality = personality;
        _seed = seed;
        _preset = DifficultyPreset.For(difficulty);
        _players = players;
        _respectTeams = respectTeams;
        _quietTicksLimit = (int)(EnemyCompass.QuietSeconds * ticksPerSecond);
        _repathTicks = (int)(RepathSeconds * ticksPerSecond);
        _roamGiveUpTicks = (int)(RoamGiveUpSeconds * ticksPerSecond);
        _retaliateTicks = (int)(RetaliateSeconds * ticksPerSecond);
    }

    /// <summary>Links this controller to the tank it drives and finalises its seeded RNG + temperament.</summary>
    public void Bind(ITank self)
    {
        _self = self;
        _lastHealth = self.Hp + self.Shield;
        _rng = new Random(_seed ?? self.Id.GetHashCode());
        var rolled = _explicitPersonality ?? AiPersonality.Roll(_rng);
        // Difficulty scales how far this bot sees, on top of whatever temperament it rolled.
        _personality = rolled with { Vision = rolled.Vision * _preset.VisionScale };
    }

    public TankInput Read()
    {
        if (_self is null || !_self.IsAlive)
        {
            return Hold();
        }

        // Getting hurt counts as fighting even when the shooter is out of sight.
        var health = _self.Hp + _self.Shield;
        _quietTicks = health < _lastHealth ? 0 : _quietTicks + 1;
        _ticksSinceHurt = health < _lastHealth ? 0 : _ticksSinceHurt == int.MaxValue ? int.MaxValue : _ticksSinceHurt + 1;
        _lastHealth = health;

        // Ambusher pre-empt: slip into grass and lie in wait when there is prey to ambush.
        if (_ambusher && _concealment is not null && Ambush() is { } ambushIntent)
        {
            return ambushIntent;
        }

        var enemy = PickEnemy();
        if (enemy is not null)
        {
            _quietTicks = 0;
        }

        // Hard override: an enemy within firing range is always engaged, holding at stand-off — combat
        // never yields to a pickup or a distraction. This preserves the core "fight what's on top of you".
        if (enemy is not null)
        {
            var distance = Vector2.Distance(enemy.Position, _self.Position);
            if (distance <= FireRange)
            {
                var move = distance > _personality.Standoff && StraightLineIsSafe(enemy.Position)
                    ? DirectionTo(enemy.Position)
                    : Vector2.Zero;
                return new TankInput(move, JitteredAim(AimAt(enemy.Position)), Fire: true);
            }
        }

        // Otherwise score the open actions and commit to the winning state for a while.
        var powerup = PickPowerup();
        var shot = NearestAudibleShot();

        var huntU = enemy is not null
            ? _personality.Aggression * Closeness(Vector2.Distance(enemy.Position, _self.Position), _personality.Vision)
            : 0f;
        var seekU = powerup is not null
            ? _personality.Greed * Closeness(Vector2.Distance(powerup.Position, _self.Position), PowerupSeekRange)
            : 0f;
        var investU = shot is not null
            ? _personality.Curiosity * Closeness(Vector2.Distance(shot.Value, _self.Position), EarshotRange)
            : 0f;

        var state = CommitState(TopState(huntU, seekU, investU), huntU, seekU, investU);

        return state switch
        {
            AiState.Hunt => Engage(enemy!),
            AiState.SeekPowerup => SeekPickup(powerup!, enemy),
            AiState.Investigate => DriveToward(shot!.Value),
            _ => Roam(),
        };
    }

    // Highest-scoring state; Wander when nothing else has a candidate (all utilities zero).
    private static AiState TopState(float hunt, float seek, float invest)
    {
        if (hunt <= 0f && seek <= 0f && invest <= 0f)
        {
            return AiState.Wander;
        }

        if (hunt >= seek && hunt >= invest)
        {
            return AiState.Hunt;
        }

        return seek >= invest ? AiState.SeekPowerup : AiState.Investigate;
    }

    // Stay in the current state while it still has a candidate and the commit window is open; otherwise
    // switch to the freshly-computed top state and restart the window. Keeps a bot from thrashing when
    // two actions score nearly the same tick-to-tick.
    private AiState CommitState(AiState top, float hunt, float seek, float invest)
    {
        var currentValid = _state switch
        {
            AiState.Hunt => hunt > 0f,
            AiState.SeekPowerup => seek > 0f,
            AiState.Investigate => invest > 0f,
            _ => false,
        };

        if (_stateCooldown > 0 && currentValid)
        {
            _stateCooldown--;
            return _state;
        }

        _state = top;
        _stateCooldown = StateCommitTicks;
        return top;
    }

    // A seen enemy out of firing range: close in (path-aware) with the gun trained on it.
    private TankInput Engage(ITank enemy)
    {
        var delta = enemy.Position - _self!.Position;
        return new TankInput(SteerToward(enemy.Position), MathF.Atan2(delta.Y, delta.X), Fire: false);
    }

    // Break off for a pickup: steer to it, but keep the turret on a visible enemy if there is one (so a
    // detour mid-fight still threatens), else aim along the travel direction.
    private TankInput SeekPickup(IPowerup powerup, ITank? enemy)
    {
        var move = SteerToward(powerup.Position);
        var aim = enemy is not null ? AimAt(enemy.Position) : AimAt(powerup.Position);
        return new TankInput(move, aim, Fire: false);
    }

    private static float Closeness(float distance, float range) => Math.Clamp(1f - distance / range, 0f, 1f);

    // Steer toward a point (aim along travel); used when investigating gunfire with no enemy to track.
    private TankInput DriveToward(Vector2 point)
    {
        var delta = point - _self!.Position;
        return new TankInput(SteerToward(point), MathF.Atan2(delta.Y, delta.X), Fire: false);
    }

    private Vector2 DirectionTo(Vector2 point)
    {
        var delta = point - _self!.Position;
        return delta == Vector2.Zero ? Vector2.Zero : Vector2.Normalize(delta);
    }

    // Path-aware steering: with a grid, head for the next A* waypoint so it rounds cover; else straight.
    private Vector2 SteerToward(Vector2 goal)
    {
        if (_grid is null || _tileSize <= 0f)
        {
            return DirectionTo(goal);
        }

        var start = ToCell(_self!.Position);
        var goalCell = ToCell(goal);
        if (start == goalCell)
        {
            return DirectionTo(goal);
        }

        var path = GridPathfinder.FindPath(_grid, start, goalCell);
        if (path.Count < 2)
        {
            return StraightLineIsSafe(goal) ? DirectionTo(goal) : Vector2.Zero;
        }

        return DirectionTo(CellCentre(path[1]));
    }

    private (int X, int Y) ToCell(Vector2 worldPoint)
    {
        var local = worldPoint - _origin;
        return ((int)MathF.Floor(local.X / _tileSize), (int)MathF.Floor(local.Y / _tileSize));
    }

    private Vector2 CellCentre((int X, int Y) cell) =>
        _origin + new Vector2((cell.X + 0.5f) * _tileSize, (cell.Y + 0.5f) * _tileSize);

    // Ambusher mode: slip into the nearest grass and snipe from cover. Returns null when no grass is in
    // reach or nothing is in sight to ambush, so the AI falls back to fighting normally.
    private TankInput? Ambush()
    {
        var enemy = PickEnemy();
        if (enemy is null)
        {
            return null;
        }

        var aim = AimAt(enemy.Position);
        var fire = Vector2.Distance(enemy.Position, _self!.Position) <= FireRange;

        if (fire)
        {
            aim = JitteredAim(aim);
        }

        if (_concealment!.ConcealsAt(_self.Position))
        {
            return new TankInput(Vector2.Zero, aim, fire);
        }

        var spot = _concealment.NearestConcealment(_self.Position, AmbushSeekRange);
        return spot is null || !StraightLineIsSafe(spot.Value) ? null : new TankInput(DirectionTo(spot.Value), aim, fire);
    }

    // Nothing in sight: travel with purpose. A quiet spell sends the bot straight at its nearest enemy;
    // otherwise it follows a route to a goal it re-picks on arrival. Routes are cached because roam goals
    // sit across the map, and running A* over that distance on every tick would be wasted work.
    private TankInput Roam()
    {
        var quarry = _quietTicks >= _quietTicksLimit ? NearestPlayer() : null;
        if (_grid is null || _tileSize <= 0f)
        {
            return quarry is not null ? DriveToward(quarry.Position) : Wander();
        }

        _routeAge++;
        _repathCountdown--;
        var repathDue = _repathCountdown <= 0;
        var stuck = repathDue && Vector2.Distance(_self!.Position, _progressMark) < _tileSize / 2f;

        // No progress means the quarry is out of reach (up on a plateau, across water) or the way is
        // jammed: forget it for one more quiet spell and go exploring instead of pressing the obstacle.
        if (quarry is not null && stuck)
        {
            quarry = null;
            _quietTicks = 0;
        }

        if (quarry is not null)
        {
            if (repathDue || _roamGoal is null)
            {
                SetRoute(quarry.Position);
            }
        }
        else if (_roamGoal is not { } goal
            || stuck
            || _routeAge > _roamGiveUpTicks
            || Vector2.Distance(_self!.Position, goal) < _tileSize * 1.5f)
        {
            SetRoute(PickRoamGoal());
        }
        else if (repathDue)
        {
            SetRoute(goal); // walls fall during a match, so the old route may no longer be the best
        }

        return FollowRoute();
    }

    private void SetRoute(Vector2 goal)
    {
        if (goal != _roamGoal)
        {
            _routeAge = 0;
        }

        _roamGoal = goal;
        _route = GridPathfinder.FindPath(_grid!, ToCell(_self!.Position), ToCell(goal));
        _routeIndex = 1;
        _repathCountdown = _repathTicks;
        _progressMark = _self.Position;
    }

    private TankInput FollowRoute()
    {
        // No route (the goal is walled in by bricks): drive straight, since pushing breaks bricks, unless
        // that line crosses lava. Then drop the goal; the next tick picks a new one.
        var next = _roamGoal!.Value;
        if (_route.Count == 0 && !StraightLineIsSafe(next))
        {
            _roamGoal = null;
            _quietTicks = 0;
            return Hold();
        }

        while (_routeIndex < _route.Count)
        {
            var waypoint = CellCentre(_route[_routeIndex]);
            if (Vector2.Distance(_self!.Position, waypoint) >= _tileSize * 0.35f)
            {
                next = waypoint;
                break;
            }

            _routeIndex++;
        }

        var delta = next - _self!.Position;
        return new TankInput(DirectionTo(next), MathF.Atan2(delta.Y, delta.X), Fire: false);
    }

    // Half-tile samples can't step over a whole lethal cell.
    private bool StraightLineIsSafe(Vector2 goal)
    {
        if (_grid is null || _tileSize <= 0f)
        {
            return true;
        }

        var from = _self!.Position;
        var samples = (int)(Vector2.Distance(from, goal) / (_tileSize / 2f));
        for (var i = 1; i <= samples; i++)
        {
            var (x, y) = ToCell(Vector2.Lerp(from, goal, i / (float)samples));
            if (CellMaterials.IsLethal(_grid.GetCell(x, y).Material))
            {
                return false;
            }
        }

        return true;
    }

    private Vector2 PickRoamGoal()
    {
        var player = (float)_rng.NextDouble() < _preset.PlayerSeekChance ? NearestPlayer() : null;
        if (player is not null)
        {
            var (x, y) = ToCell(player.Position);
            for (var i = 0; i < FarCellTries; i++)
            {
                var cell = (
                    X: x + _rng.Next(-PlayerAreaSpreadCells, PlayerAreaSpreadCells + 1),
                    Y: y + _rng.Next(-PlayerAreaSpreadCells, PlayerAreaSpreadCells + 1));
                if (CanRoamTo(cell))
                {
                    return CellCentre(cell);
                }
            }

            return player.Position;
        }

        // Exploring: the farthest of a few random open cells, so a bot crosses the map, not its corner.
        var here = ToCell(_self!.Position);
        var best = here;
        var bestDistance = -1;
        for (var i = 0; i < FarCellTries; i++)
        {
            var cell = (X: _rng.Next(_grid!.Width), Y: _rng.Next(_grid.Height));
            var distance = Math.Abs(cell.X - here.X) + Math.Abs(cell.Y - here.Y);
            if (CanRoamTo(cell) && distance > bestDistance)
            {
                best = cell;
                bestDistance = distance;
                if (distance >= MinRoamCells * 2)
                {
                    break;
                }
            }
        }

        return CellCentre(best);
    }

    private bool CanRoamTo((int X, int Y) cell) =>
        !_grid!.IsBlocked(cell.X, cell.Y) && !CellMaterials.IsLethal(_grid.GetCell(cell.X, cell.Y).Material);

    // While a player is alive, bots save their fire for players: in measured matches bots that dueled
    // whichever bot they met next used up all their lives across the map from the player, who met nobody.
    // Getting shot still gets an answer, whoever fired.
    private bool IsAlly(ITank tank) => _respectTeams && tank.Team == _self!.Team;

    private bool IgnoresRival(ITank tank)
    {
        if (_players is null || _ticksSinceHurt <= _retaliateTicks)
        {
            return false;
        }

        var anyPlayerAlive = false;
        for (var i = 0; i < _players.Count; i++)
        {
            if (ReferenceEquals(_players[i], tank))
            {
                return false;
            }

            anyPlayerAlive |= _players[i].Hp > 0 && !IsAlly(_players[i]);
        }

        return anyPlayerAlive;
    }

    private ITank? NearestPlayer()
    {
        ITank? nearest = null;
        if (_players is not null)
        {
            for (var i = 0; i < _players.Count; i++)
            {
                if (_players[i].Hp > 0 && !ReferenceEquals(_players[i], _self) && !IsAlly(_players[i])
                    && IsCloser(_players[i], nearest))
                {
                    nearest = _players[i];
                }
            }
        }

        return nearest ?? NearestEnemy();
    }

    // The bot's arrow: any other live tank, seen or not.
    private ITank? NearestEnemy()
    {
        ITank? nearest = null;
        foreach (var entity in _world.Entities)
        {
            if (entity is ITank { Hp: > 0 } tank && !ReferenceEquals(tank, _self) && !IsAlly(tank) && IsCloser(tank, nearest))
            {
                nearest = tank;
            }
        }

        return nearest;
    }

    private bool IsCloser(ITank tank, ITank? than) =>
        than is null
        || Vector2.DistanceSquared(tank.Position, _self!.Position) < Vector2.DistanceSquared(than.Position, _self!.Position);

    // Roam without a grid to route over: hold a heading for a while, then pick a fresh one.
    private TankInput Wander()
    {
        if (_wanderCooldown <= 0 || _wanderDirection == Vector2.Zero)
        {
            var angle = _rng.NextDouble() * Math.Tau;
            _wanderDirection = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
            _wanderCooldown = WanderTicks;
        }

        _wanderCooldown--;
        return new TankInput(_wanderDirection, MathF.Atan2(_wanderDirection.Y, _wanderDirection.X), Fire: false);
    }

    // Easy bots shoot with a fresh seeded error each shot instead of laser accuracy; Normal/Hard
    // presets have zero jitter, so this is the identity for them.
    private float JitteredAim(float aim)
    {
        var bound = _preset.AimJitterRadians;
        return bound <= 0f ? aim : aim + ((float)_rng.NextDouble() * 2f - 1f) * bound;
    }

    private float AimAt(Vector2 point)
    {
        var delta = point - _self!.Position;
        return MathF.Atan2(delta.Y, delta.X);
    }

    private TankInput Hold() => new(Vector2.Zero, _self?.TurretRotation ?? 0f, Fire: false);

    // The enemy this tank pursues: a closeness-weighted-random pick among the tanks it can see (not the
    // nearest-always), committed until that target drops out of view — so two bots at the same spot may
    // chase different enemies and a bot doesn't re-roll its aim every tick.
    private ITank? PickEnemy()
    {
        var visible = VisibleEnemies();
        if (visible.Count == 0)
        {
            _target = null;
            return null;
        }

        if (_target is not null)
        {
            foreach (var t in visible)
            {
                if (t.Id == _target.Id)
                {
                    return _target;
                }
            }
        }

        _target = WeightedPick(visible, e => Closeness(Vector2.Distance(e.Position, _self!.Position), _personality.Vision));
        return _target;
    }

    // Every tank the AI can see — ANY tank but itself, within its personal Vision, with clear line of
    // sight, and not lurking in a bush beyond brushing range.
    private List<ITank> VisibleEnemies()
    {
        var found = new List<ITank>();
        foreach (var entity in _world.Entities)
        {
            if (entity is not ITank tank || tank.Hp <= 0 || tank.Id == _self!.Id || IsAlly(tank) || IgnoresRival(tank))
            {
                continue;
            }

            var distance = Vector2.Distance(tank.Position, _self.Position);
            if (distance > _personality.Vision)
            {
                continue;
            }

            if (!HasLineOfSight(_self.Position, tank.Position, distance))
            {
                continue;
            }

            if (distance > BushRevealRange && _concealment?.ConcealsAt(tank.Position) == true)
            {
                continue;
            }

            found.Add(tank);
        }

        return found;
    }

    // The pickup this tank goes for: a closeness-weighted-random choice among the reachable ones,
    // committed until collected/gone or out of view — variety over always-nearest.
    private IPowerup? PickPowerup()
    {
        var reachable = ReachablePowerups();
        if (reachable.Count == 0)
        {
            _committedPowerup = null;
            return null;
        }

        if (_committedPowerup is not null && _committedPowerup.IsAvailable)
        {
            foreach (var p in reachable)
            {
                if (p.Id == _committedPowerup.Id)
                {
                    return _committedPowerup;
                }
            }
        }

        _committedPowerup = WeightedPick(reachable, p => Closeness(Vector2.Distance(p.Position, _self!.Position), PowerupSeekRange));
        return _committedPowerup;
    }

    private List<IPowerup> ReachablePowerups()
    {
        var found = new List<IPowerup>();
        foreach (var entity in _world.Entities)
        {
            if (entity is not IPowerup powerup || !powerup.IsAvailable)
            {
                continue;
            }

            var distance = Vector2.Distance(powerup.Position, _self!.Position);
            if (distance > PowerupSeekRange)
            {
                continue;
            }

            if (!HasLineOfSight(_self.Position, powerup.Position, distance))
            {
                continue;
            }

            found.Add(powerup);
        }

        return found;
    }

    // The position of the nearest gunfire the AI can hear — any live enemy-team projectile within
    // earshot. No line-of-sight check: gunfire carries through walls.
    private Vector2? NearestAudibleShot()
    {
        Vector2? nearest = null;
        var nearestDistance = float.PositiveInfinity;

        foreach (var entity in _world.Entities)
        {
            if (entity is not IProjectile shot || shot.Team == _self!.Team)
            {
                continue;
            }

            var distance = Vector2.Distance(shot.Position, _self.Position);
            if (distance <= EarshotRange && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = shot.Position;
            }
        }

        return nearest;
    }

    // Roulette-wheel pick weighted by each item's utility (a tiny floor keeps zero-weight items possible
    // but rare). One candidate → that candidate, so single-target scenes stay deterministic.
    private T WeightedPick<T>(IReadOnlyList<T> items, Func<T, float> weight)
    {
        if (items.Count == 1)
        {
            return items[0];
        }

        var total = 0f;
        foreach (var item in items)
        {
            total += MathF.Max(weight(item), 0.0001f);
        }

        var roll = (float)_rng.NextDouble() * total;
        foreach (var item in items)
        {
            roll -= MathF.Max(weight(item), 0.0001f);
            if (roll <= 0f)
            {
                return item;
            }
        }

        return items[^1];
    }

    private bool HasLineOfSight(Vector2 from, Vector2 to, float distance)
    {
        var direction = to - from;
        if (direction == Vector2.Zero)
        {
            return true;
        }

        var hit = _arena.RaycastFirstHit(from, direction, distance);
        return hit is null || hit.Value.Distance >= distance;
    }
}
