using System.Collections.Generic;
using System.Globalization;
using Godot;
using TankGame.Domain;
using TankGame.Domain.Net;
using TankGame.GameLogic;
using TankGame.Infrastructure;
using NVector2 = System.Numerics.Vector2;

namespace TankGame.Presentation;

/// <summary>The 3D networked play scene (ADR-0019 step 3): the welcome's slot decides the role.
/// The HOST (the lobby's host seat) is the authority — it runs the REAL <see cref="World"/> (the same GameLogic as
/// single-player) with its own tank on keyboard/mouse and the guest's tank on a
/// <see cref="RelayedInputSource"/>, and a <see cref="HostSession"/> broadcasts a snapshot per fixed
/// 20 Hz tick. A GUEST predicts its own tank (<see cref="PredictedTank"/>), mirrors every other slot
/// straight from the snapshots into <see cref="NetTank"/> view-models, and applies authoritative wall
/// deltas to the shared grid. Both roles render the shared <see cref="Battlefield01"/> map with the
/// same 3D views the local arena uses. "Client sends intent, the host resolves outcome."</summary>
public partial class NetArena3DScene : Node3D
{
    private const float TileSize = 64f;
    private const float TickSeconds = 1f / 20f; // the fixed authoritative tick (PredictedTank's cadence)

    // Match PredictedTank's movement model (200 u/s) so guest prediction replays the host's real
    // Tank faithfully; combat numbers mirror Arena3DScene.
    private const float TankSpeed = 200f;
    private const float FireInterval = 0.6f; // half the old rate of fire (owner ask 2026-07-08)
    private const float ProjectileSpeed = 600f;
    private const float CombatHitRadius = 28f;
    private const float PickupRadius = 28f;
    private const int TankMaxHp = 8;
    private const byte DefaultHostSlot = 0; // a session with no lobby (old 2-player tests)
    private const float TeleportPadRadius = 40f; // a tank centred within this of a pad warps (as solo)

    private static readonly NVector2 GridOrigin = NVector2.Zero;

    // Camera: the same eyeballed ¾ ortho as Arena3DScene.
    private const float CamPitchDeg = 52f;
    private const float CamYawDeg = 45f;
    private const float CamDistance = 2500f;
    private const float CamOrthoSize = 820f;

    private IMatchTransport _transport = null!;
    private LevelMap _level = null!;
    private WallGrid _grid = null!;
    private GridArena _arena = null!;
    private Camera3D _camera = null!;
    private NetStatusOverlay _status = null!;

    private readonly Dictionary<byte, ITank> _tanks = new();
    private readonly Dictionary<byte, Tank3DView> _tankViews = new();

    // The map's teleport pads (Cliffs' cross-layer pair): static map features every member derives
    // from the same resolved map, so nothing about them travels on the wire. The host's authoritative
    // tanks consult the Teleporter inside World.Step and the warped positions/layers ride the normal
    // snapshot; a guest renders the same rings and mirrors the outcomes.
    private Teleporter _teleporter = null!;
    private readonly List<TeleportPad3DView> _padViews = new();
    private IReadOnlyList<TeleportPadLink> _authoredPads = System.Array.Empty<TeleportPadLink>();
    private readonly List<(int X, int Y)> _spawnAnchors = new(); // cells the map's own layout starts tanks on
    private float _accumulator;
    private byte? _localSlot;
    private int _matchSeed; // the lobby's match seed: drives spawn placement and per-bot AI seeding

    // The loading handshake (issue #2): when we enter during the lobby's "loading" phase we report our
    // arena is built and then hold — no ticks, no snapshots — until the server flips to "started"
    // (every seated player loaded). A session with no lobby (old 2-player tests) plays immediately.
    private bool _awaitingStart;
    private bool _returningToRoom;

    /// <summary>The host disconnected, so the match is over for this guest and only Leave remains.</summary>
    public bool HostLeft { get; private set; }

    // The seating plan from the lobby's final roster (placeholder-named AI on empty seats), the
    // seeded spawn table both roles derive identically, and the host's authoritative round state.
    private IReadOnlyList<NetRoster.Seat> _roster = System.Array.Empty<NetRoster.Seat>();
    private IReadOnlyList<(int X, int Y)> _spawns = System.Array.Empty<(int, int)>();
    private readonly List<(byte Slot, ITank Tank)> _rosterTanks = new();
    private readonly List<(int Team, bool Alive)> _roundStatus = new();

    // The net match's stat book (no stats on the wire): the host feeds it from its authoritative
    // world each tick, a guest from every snapshot — both derive the identical leaderboard.
    private readonly NetMatchStats _netStats = new();
    private VictoryScreen? _victory;
    private CanvasLayer _leaveLayer = null!;
    private MatchHud _matchHud = null!;
    private readonly List<ITank> _hudTanks = new();

    /// <summary>The decided round (FFA: last tank standing; Team: last team standing). The host
    /// evaluates it each authoritative tick; a guest derives the same verdict from the snapshot's
    /// tank states, so both roles show the winner banner. Null while the round is being fought.</summary>
    public (bool Decided, int WinningTeam)? RoundResult { get; private set; }

    // Host role: the authoritative world and its session.
    private World? _world;
    private HostSession? _session;
    private IInputSource? _hostInput;

    // Guest role: prediction for the local tank, an input seq counter for the frames sent up.
    private PredictedTank? _predicted;
    private IInputSource? _guestLocalInput;
    private uint _inputSeq;

    // The local player's input — twin-stick touch on a phone (the shipped APK), keyboard/mouse on
    // desktop. One overlay serves whichever role this client took; tests report no touchscreen.
    private TouchControls? _touch;

    private IInputSource BuildLocalInput()
    {
        if (!DisplayServer.IsTouchscreenAvailable())
        {
            return new KeyboardMouse3DInputSource(_camera);
        }

        _touch = new TouchControls { Name = "TouchControls" };
        var layer = new CanvasLayer { Name = "TouchControlsLayer" };
        layer.AddChild(_touch);
        AddChild(layer);
        return new TouchInput3DSource(_camera, () => _touch.MoveOutput, () => _touch.AimOutput);
    }

    // Guest role: the shots mirrored from the latest snapshot (ADR-0019 step 4) — the guest has no
    // world, so each snapshot rebuilds this set of throwaway projectile views.
    private readonly List<Projectile3DView> _snapshotProjectiles = new();

    /// <summary>The slot the relay assigned this client, or null before the welcome.</summary>
    public byte? LocalSlot => _localSlot;

    /// <summary>Each known slot's tank — the host's real <see cref="Tank"/>s, a guest's mirrored
    /// view-models. For the camera, the views, and the tests.</summary>
    public IReadOnlyDictionary<byte, ITank> Tanks => _tanks;

    /// <summary>The current state of a shared-grid cell — lets a test assert an applied wall delta.</summary>
    public WallCell CellAt(int x, int y) => _grid.GetCell(x, y);

    /// <summary>How many shots the guest is currently mirroring from the latest snapshot (ADR-0019
    /// step 4). Zero on the host, which renders its own shots from the world. For the tests.</summary>
    public int MirroredProjectileCount => _snapshotProjectiles.Count;

    public override void _Ready()
    {
        _transport = NetworkSession.Active
            ?? throw new System.InvalidOperationException("NetArena3DScene entered without an active session.");

        // The room's map (multiplayer plan Phase 7): every member resolves the lobby's map id into
        // the same level — the Desert seed and the "random" pick both derive from the shared lobby
        // code, so host truth and guest rendering agree without map bytes on the wire. A session
        // without a lobby roster (the 2-player era, old tests) keeps the classic battlefield.
        bool[,] sandbags;
        if (NetworkSession.StartedLobby is { } lobby)
        {
            switch (NetMapPick.Resolve(lobby.Map, NetworkSession.ActiveCode))
            {
                case NetMapPick.Cliffs:
                    var cliffs = CliffsArena.Create();
                    _level = cliffs.Map;
                    sandbags = cliffs.Sandbags;
                    _authoredPads = cliffs.Pads; // the cross-layer valley↔plateau pair (teleport pads T3)
                    _spawnAnchors.Add(cliffs.PlayerSpawn);
                    _spawnAnchors.AddRange(cliffs.EnemySpawns);
                    break;
                case NetMapPick.BuiltIn builtIn when ArenaBuilders.TryGet(builtIn.ArenaId, out var builder):
                    // A themed code arena (Forest/Volcano/City/…): every member builds the identical layout
                    // from its id + the shared match seed, so host truth and guest rendering agree without
                    // map bytes on the wire.
                    var themed = builder.Build(lobby.Seed);
                    _level = themed.Map;
                    sandbags = themed.Sandbags;
                    _authoredPads = themed.Pads; // themed arenas may author pad pairs too
                    _spawnAnchors.Add(themed.PlayerSpawn);
                    _spawnAnchors.AddRange(themed.EnemySpawns);
                    break;
                default:
                    // Desert War (and the safe fallback for any unrecognised pick): a seeded generation.
                    var seed = (NetMapPick.Resolve(lobby.Map, NetworkSession.ActiveCode) as NetMapPick.Desert)?.Seed
                        ?? StableHash.Of(NetworkSession.ActiveCode);
                    var dim = Mathf.Max(GameSetup.ArenaWidth, GameSetup.ArenaHeight);
                    var layout = new ArenaGenerator().Generate(
                        new ArenaGenParams(dim, dim, seed, EnemyCount: 0, PickupCount: 0));
                    _level = layout.Map;
                    sandbags = layout.Sandbags;
                    _spawnAnchors.Add(layout.PlayerSpawn);
                    _spawnAnchors.AddRange(layout.EnemySpawns);
                    break;
            }
        }
        else
        {
            _level = LevelMap.Parse(Battlefield01.Text);
            sandbags = new bool[_level.Width, _level.Height];
        }

        _grid = _level.BuildGrid();
        _arena = new GridArena(_grid, TileSize, GridOrigin);
        BuildTeleporter(); // before any tank exists — the host's tanks consult it from tick one

        BuildEnvironment();
        BuildGround();

        var terrain = new Terrain3DView { Name = "Terrain3DView" };
        AddChild(terrain);
        terrain.Bind(_grid, _level.Bushes, sandbags, TileSize);

        _status = new NetStatusOverlay { Name = "NetStatusOverlay" };
        AddChild(_status); // shows "Connecting…" until the welcome arrives

        BuildLeaveButton();
        _matchHud = new MatchHud { Name = "MatchHud" };
        AddChild(_matchHud);

        _transport.WelcomeReceived += OnWelcome;
        _transport.SnapshotReceived += OnSnapshot;
        _transport.LobbyStateReceived += OnLobbyState;

        // If the room handed off during "loading", hold the match until every seated player is in.
        _awaitingStart = NetworkSession.StartedLobby?.Phase == LobbyPhase.Loading;

        // The welcome is a one-shot fired on connect — during the lobby, before this scene existed.
        // The room scene carried our slot across the handoff, so adopt it now rather than waiting for
        // a second welcome that never comes. (A live test still drives OnWelcome via the transport;
        // that path sets LocalSlot itself, so both routes converge.)
        if (NetworkSession.LocalSlot is byte carried)
        {
            OnWelcome(carried);
        }

        // Our arena is fully built — report loaded so the server can start once everyone else is too.
        if (_awaitingStart)
        {
            _transport.SendLobby(LobbyProtocol.EncodeLoaded());
        }
    }

    // The lobby keeps pushing state through the loading phase; the flip to "started" (all seated
    // players loaded, or the last laggard left — server-side) releases the match to run.
    private void OnLobbyState(LobbyView view)
    {
        if (_awaitingStart && view.Phase == LobbyPhase.Started)
        {
            _awaitingStart = false;
            _status.SetStatus(NetStatusOverlay.ConnectedKey);
        }

        // The host pressed Rematch: the server reset the room to "waiting". Every client returns to
        // the lobby room on the SAME socket, carrying the fresh waiting view for the room's controller
        // (the welcome was a one-shot, so the room re-adopts the carried slot instead).
        if (view.Phase == LobbyPhase.Waiting)
        {
            ReturnToRoom(view);
            return;
        }

        // The relay moves the host seat when the host disconnects, but only the original host's machine
        // was simulating, so the match can't go on. A decided round already has its own Leave.
        if (!HostLeft && _victory is null && NetworkSession.StartedLobby is { } started && view.HostSlot != started.HostSlot)
        {
            ShowHostLeft();
        }
    }

    private void ShowHostLeft()
    {
        HostLeft = true;
        _leaveLayer.Visible = false;
        _matchHud.Visible = false;

        var message = new Label { Text = "net.host_left", HorizontalAlignment = HorizontalAlignment.Center };
        message.AddThemeFontSizeOverride("font_size", 28);
        var leave = new Button { Name = "HostLeftLeave", Text = "net.leave", CustomMinimumSize = new Vector2(200f, 64f) };
        leave.Pressed += LeaveMatch;
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 16);
        rows.AddChild(message);
        rows.AddChild(leave);
        var card = new PanelContainer { Name = "HostLeftCard" };
        card.AddChild(rows);
        var centre = new CenterContainer();
        centre.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        centre.AddChild(card);
        var layer = new CanvasLayer { Name = "HostLeftLayer", Layer = 4 };
        layer.AddChild(centre);
        AddChild(layer);
        leave.GrabFocus();
    }

    private void ReturnToRoom(LobbyView view)
    {
        // Unhook from the shared transport — it outlives this scene, and a push into a freed node
        // would explode. LeaveMatch doesn't need this: Reset drops the transport entirely.
        _transport.WelcomeReceived -= OnWelcome;
        _transport.SnapshotReceived -= OnSnapshot;
        _transport.LobbyStateReceived -= OnLobbyState;
        NetworkSession.StartedLobby = view;
        _returningToRoom = true;
        if (GetTree().CurrentScene == this)
        {
            GetTree().ChangeSceneToFile(LobbyBrowserScene.RoomScenePath);
        }
    }

    // A versus match has no pause (you can't freeze the other player), so the pause-menu route to an
    // exit that the solo arena offers doesn't apply here — yet a touch player still needs a way out,
    // and on a phone there is no Escape key (owner ask 2026-07-04). A always-visible corner button
    // leaves the match back to the main menu; Escape does the same on desktop. Present from _Ready so
    // a player can bail even while the connection is still "Connecting…".
    private void BuildLeaveButton()
    {
        var layer = new CanvasLayer { Name = "LeaveLayer", Layer = 3 };
        _leaveLayer = layer;
        var leave = new Button { Name = "LeaveButton", Text = "net.leave" };
        leave.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        leave.GrowHorizontal = Control.GrowDirection.Begin; // or it runs off the right edge of the screen
        leave.Position += new Vector2(-16f, 16f);
        leave.CustomMinimumSize = new Vector2(112f, 64f); // a comfortable thumb target
        leave.Pressed += LeaveMatch;
        layer.AddChild(leave);

        // Online rematch: hidden until the round is decided, and only ever shown to the lobby host —
        // pressing it asks the server to reset the room to "waiting" (the server enforces host-only).
        _rematch = new Button { Name = "RematchButton", Text = "net.rematch", Visible = false };
        _rematch.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        _rematch.GrowHorizontal = Control.GrowDirection.Begin;
        _rematch.Position += new Vector2(-16f, 96f); // stacked under Leave
        _rematch.CustomMinimumSize = new Vector2(112f, 64f);
        _rematch.Pressed += () => _transport.SendLobby(LobbyProtocol.EncodeRematch());
        layer.AddChild(_rematch);
        AddChild(layer);
    }

    private Button _rematch = null!;

    // Only the lobby host may trigger a rematch (mirrors the server's hostSlot check).
    private bool IsLobbyHost =>
        NetworkSession.StartedLobby is { } lobby && _localSlot is byte slot && lobby.HostSlot == slot;

    // Escape leaves the match on desktop — the same exit the corner Leave button gives touch players.
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel"))
        {
            LeaveMatch();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Leave the match: drop the shared transport (so it is not reused) and return to the
    /// main menu. Public so a test can drive it; the scene change is guarded like the title screen's
    /// so the test harness — which parents this scene rather than making it current — isn't swapped
    /// out from under itself.</summary>
    public void LeaveMatch()
    {
        NetworkSession.Reset();
        if (GetTree().CurrentScene == this)
        {
            GetTree().ChangeSceneToFile("res://src/Presentation/Title.tscn");
        }
    }

    public override void _Process(double delta) => Tick((float)delta);

    /// <summary>One networked frame: pump the transport and run any due fixed ticks (20 Hz, the
    /// snapshot/input cadence both roles share). Public so tests can step it deterministically.</summary>
    public void Tick(float delta)
    {
        _transport.Poll();

        // A rematch push swaps the scene from inside Poll; this one is on its way out.
        if (_returningToRoom || HostLeft)
        {
            return;
        }

        if (_localSlot is null || _awaitingStart)
        {
            return; // not yet welcomed, or holding on the loading banner until every player is in
        }

        _accumulator += delta;
        while (_accumulator >= TickSeconds)
        {
            _accumulator -= TickSeconds;
            StepFixedTick();
        }

        FollowLocalTank();
        RefreshHud(delta);
    }

    private void RefreshHud(float delta)
    {
        if (_localSlot is not byte slot || !_tanks.TryGetValue(slot, out var self))
        {
            return;
        }

        _hudTanks.Clear();
        _hudTanks.AddRange(_tanks.Values);
        _matchHud.Refresh(delta, _camera, self, _hudTanks);
    }

    private void StepFixedTick()
    {
        if (_session is not null)
        {
            if (RoundResult is not null)
            {
                return; // decided — the world freezes; guests see the match hold still
            }

            // Host: the world reads every input source (keyboard + relayed + AI) and the session broadcasts.
            _teleporter.Step(TickSeconds); // pad cooldowns age in sim time (tanks warp inside world.Step)
            _session.Step(TickSeconds);
            UpdatePadViews();
            EvaluateRound();
            return;
        }

        if (_predicted is not null && _guestLocalInput is not null)
        {
            var input = _guestLocalInput.Read();
            var frame = new InputFrame(++_inputSeq, input.Move.X, input.Move.Y, input.Aim,
                input.Fire ? InputFrame.FireBit : (byte)0, _localSlot ?? 0);
            _transport.SendInput(frame);
            _predicted.Predict(frame);
            SyncLocalFromPrediction();
        }
    }

    // The relay welcomed us: the host seat becomes the authority, anyone else a predicting guest. Both
    // roles derive the same roster (from the lobby's snapshotted final state) and the same spawn
    // table, so host truth and guest prediction agree from the first tick.
    private void OnWelcome(byte slot)
    {
        if (_localSlot is not null)
        {
            return; // already welcomed (slot carried from the lobby, or a duplicate push) — one setup only
        }

        _localSlot = slot;
        _roster = NetRoster.Build(
            NetworkSession.StartedLobby, slot, NetworkSession.ActiveCode, LobbyProtocol.MaxPlayers);
        // Seeded random placement, min-distance separated: every peer derives the same spawn list from
        // the shared match seed, so host truth and guest prediction agree — and the seed varies the
        // cells each match (issue #4). Same footing rules as a respawn: tanks start on the ground layer,
        // so a raised cell would bury one inside the plateau, where it can't be seen or moved.
        _matchSeed = NetworkSession.StartedLobby?.Seed ?? 0;
        if (_spawnAnchors.Count == 0)
        {
            _spawnAnchors.Add(FirstOpenCell()); // the classic battlefield names no spawns of its own
        }

        var allowed = new HashSet<(int X, int Y)>(new RespawnPlacer(_grid, TileSize, GridOrigin, _spawnAnchors,
            new System.Random(_matchSeed), reserved: OnTeleportPad).AllowedCells());
        _spawns = SpawnTable.For(_level.Width, _level.Height, SpawnTable.MaxSpawns, _matchSeed,
            (x, y) => !allowed.Contains((x, y)));
        foreach (var seat in _roster)
        {
            _netStats.Register(seat.Slot, seat.Name, seat.Team); // both roles book the same rows
        }

        // The room hands the host role on when its creator leaves, so the seat decides the authority:
        // the relay routes every guest's input to lobby.HostSlot and nobody else would simulate.
        if (slot == (NetworkSession.StartedLobby?.HostSlot ?? DefaultHostSlot))
        {
            BecomeHost();
        }
        else
        {
            BecomeGuest(slot);
        }

        _status.SetStatus(_awaitingStart ? NetStatusOverlay.LoadingKey : NetStatusOverlay.ConnectedKey);
    }

    private (int X, int Y) FirstOpenCell()
    {
        for (var y = 0; y < _grid.Height; y++)
        {
            for (var x = 0; x < _grid.Width; x++)
            {
                if (!_grid.IsBlocked(x, y) && _grid.LayerAt(x, y) == 0)
                {
                    return (x, y);
                }
            }
        }

        return (0, 0);
    }

    private bool OnTeleportPad(NVector2 point)
    {
        foreach (var pad in _teleporter.PadStatuses())
        {
            if (NVector2.Distance(pad.Position, point) <= TeleportPadRadius + Tank.CollisionRadius)
            {
                return true;
            }
        }

        return false;
    }

    private void BecomeHost()
    {
        _world = new World(new CombatResolver(CombatHitRadius));
        _world.EntitySpawned += OnEntitySpawned;
        _world.EntityDespawned += OnEntityDespawned;

        _hostInput = BuildLocalInput();
        var guestInputs = new Dictionary<byte, RelayedInputSource>();
        var humans = new List<ITank>(); // fills up below; bots read it live, so seat order doesn't matter

        // One tank per roster seat: the host on local input, each remote human on a relayed input
        // keyed by its slot, and every un-joined seat an AI tank carrying the placeholder name the
        // room showed in gray (owner ask).
        foreach (var seat in _roster)
        {
            AiInputSource? ai = null;
            IInputSource input = seat.Kind switch
            {
                NetRoster.SeatKind.LocalHuman => _hostInput,
                NetRoster.SeatKind.RemoteHuman => guestInputs[seat.Slot] = new RelayedInputSource(),
                // Each bot seeded matchSeed ^ slot: distinct temperaments, yet identical on every host run
                // of the same match (issue #3). Host-only, so no cross-peer determinism needed.
                // Host's persisted difficulty governs net bots (guests mirror host snapshots anyway).
                _ => ai = new AiInputSource(_world, _arena, grid: _grid, tileSize: TileSize, origin: GridOrigin,
                    seed: _matchSeed ^ seat.Slot, difficulty: GameSetup.BotDifficulty,
                    players: humans, respectTeams: NetworkSession.StartedLobby?.Mode == TankGame.Domain.Net.GameMode.Team,
                    ticksPerSecond: 1f / TickSeconds),
            };

            var spawn = _spawns[seat.Slot % _spawns.Count];
            var fireInterval = ai is null
                ? FireInterval
                : FireInterval * DifficultyPreset.For(GameSetup.BotDifficulty).FireIntervalScale;
            var tank = new Tank(input, _world, _arena, CellCentre(spawn.X, spawn.Y),
                TankSpeed, fireInterval, ProjectileSpeed, maxHp: TankMaxHp, team: seat.Team,
                teleporter: _teleporter, displayName: seat.Name);
            ai?.Bind(tank);
            if (ai is null)
            {
                humans.Add(tank);
            }

            _tanks[seat.Slot] = tank;
            _rosterTanks.Add((seat.Slot, tank));
            _world.Spawn(tank);
        }

        _session = new HostSession(_transport, _world, _grid, _rosterTanks, guestInputs);
        SpawnPowerupDirector();
    }

    // Net matches have no initial pickups — the boost director is their only source, seeded by the
    // match seed so a re-host of the same match drops the same crates. Only the authoritative world
    // runs it; guests mirror the drops from the snapshot's powerup states.
    private void SpawnPowerupDirector()
    {
        if (_world is null)
        {
            return;
        }

        var world = _world;
        var fieldMax = new NVector2(_level.Width * TileSize, _level.Height * TileSize);
        var effects = PowerupEffects.Catalogue(GridOrigin, fieldMax, TileSize);
        var floorCells = new List<(int X, int Y)>();
        for (var x = 1; x < _grid.Width - 1; x++)
        {
            for (var y = 1; y < _grid.Height - 1; y++)
            {
                if (!_grid.IsBlocked(x, y))
                {
                    floorCells.Add((x, y));
                }
            }
        }

        world.Spawn(new PowerupDirector(world, _matchSeed, floorCells, GridOrigin, TileSize,
            (kind, pos) => new Powerup(world, pos, kind, effects[kind], PickupRadius,
                despawnAfter: PowerupDirector.DespawnSeconds)));
    }

    private void BecomeGuest(byte slot)
    {
        _guestLocalInput = BuildLocalInput();
        var spawn = _spawns[slot % _spawns.Count];
        _predicted = new PredictedTank(slot, _arena, CellCentre(spawn.X, spawn.Y));
        EnsureMirroredTank(slot);
        SyncLocalFromPrediction();
    }

    // Host-side world events: every spawned entity gets the same 3D view single-player uses, so the
    // host's match looks identical to a local one. The host renders shots straight from its world;
    // a guest mirrors them from the snapshot's projectile set instead (ADR-0019 step 4).
    private void OnEntitySpawned(IEntity entity)
    {
        switch (entity)
        {
            case ITank tank:
                foreach (var (slot, known) in _tanks)
                {
                    if (ReferenceEquals(known, tank))
                    {
                        AddTankView(slot, tank);
                    }
                }

                break;
            case IProjectile projectile:
                var view = new Projectile3DView { Name = "Projectile3DView" };
                view.Bind(projectile);
                AddChild(view);
                _projectileViews[projectile] = view;
                break;
            case IPowerup pickup:
                // A director drop in the host's world — same view + collected floater as solo.
                var pickupView = new Powerup3DView { Name = "Powerup3DView" };
                pickupView.Bind(pickup);
                AddChild(pickupView);
                _projectileViews[pickup] = pickupView; // reaped through the same despawn path
                pickup.Collected += kind => ShowPickupFloater(pickup.Position, kind);
                break;
        }
    }

    private readonly Dictionary<IEntity, Node3D> _projectileViews = new();

    private void OnEntityDespawned(IEntity entity)
    {
        if (_projectileViews.TryGetValue(entity, out var view))
        {
            _projectileViews.Remove(entity);
            view.QueueFree();
        }
    }

    // Both roles derive the map's teleport pads from the shared map resolution (the same derivation
    // the solo arena uses), so the rings sit identically on every peer with nothing on the wire.
    // Only the host's tanks consult the Teleporter — a guest's copy exists for the ring views (its
    // cooldowns never fire, so a guest's rings read "ready"; the warp itself arrives via snapshot).
    private void BuildTeleporter()
    {
        var (teleporter, pads) = AuthoredTeleporter.Build(
            _authoredPads, _grid, TileSize, GridOrigin, TeleportPadRadius);
        _teleporter = teleporter;
        foreach (var pad in pads)
        {
            var view = new TeleportPad3DView { Name = "TeleportPad" };
            view.Configure(GroundProjection.ToWorld(pad.Position, pad.Layer), TeleportPadRadius);
            AddChild(view);
            _padViews.Add(view);
        }
    }

    // Host only: mirror the authoritative pad cooldowns onto the rings (dim while dormant), in the
    // shared link order both hold.
    private void UpdatePadViews()
    {
        if (_padViews.Count == 0)
        {
            return;
        }

        var statuses = _teleporter.PadStatuses();
        for (var i = 0; i < _padViews.Count && i < statuses.Count; i++)
        {
            _padViews[i].SetState(statuses[i].Ready, statuses[i].CooldownFraction);
        }
    }

    // A guest's authoritative snapshot: reconcile the prediction, mirror every other slot, apply walls.
    private void OnSnapshot(SnapshotFrame snapshot)
    {
        if (_session is not null)
        {
            return; // the host is the authority — it never consumes snapshots
        }

        _predicted?.Reconcile(snapshot);

        foreach (var state in snapshot.Tanks)
        {
            if (state.Slot == _localSlot)
            {
                SyncLocalFromPrediction();
                continue;
            }

            var firstSighting = !_tanks.ContainsKey(state.Slot);
            var tank = EnsureMirroredTank(state.Slot);
            tank.Position = new NVector2(state.X, state.Y);
            tank.Rotation = state.Rotation;
            tank.TurretRotation = state.TurretRotation;
            tank.Hp = state.Hp;
            tank.Team = state.Team;
            tank.Shield = state.Shield; // a shielded remote tank shows its shield bar (ADR-0019 step 4)
            tank.Layer = state.Layer;   // and renders at the right elevation
            _tankViews[state.Slot].ApplyTeamTint(tank.Team); // the mirrored team arrives with the snapshot
            if (firstSighting)
            {
                _status.SetStatus(NetStatusOverlay.Player2JoinedKey);
            }
        }

        foreach (var delta in snapshot.WallDeltas)
        {
            _grid.SetCell(delta.CellX, delta.CellY, new WallCell((CellMaterial)delta.Material, delta.Hp));
        }

        MirrorProjectiles(snapshot.Projectiles);
        MirrorPowerups(snapshot);
        DetectRoundOverFromSnapshot(snapshot);
    }

    // Guest role: the pickups mirrored from the latest snapshot, keyed by the host's stable wire id.
    private readonly Dictionary<ushort, (NetPowerup Model, Powerup3DView View)> _snapshotPickups = new();

    /// <summary>How many pickups the guest is currently mirroring. Zero on the host. For the tests.</summary>
    public int MirroredPickupCount => _snapshotPickups.Count;

    // Diff the mirrored pickup set against the snapshot's: new ids appear, known ids update their
    // availability, absent ids vanish. Collection is not signalled on the wire — a pickup that
    // vanishes while a tank sits on it was collected (pop the floater), otherwise it just faded.
    private void MirrorPowerups(SnapshotFrame snapshot)
    {
        foreach (var state in snapshot.Powerups)
        {
            if (_snapshotPickups.TryGetValue(state.Id, out var known))
            {
                known.Model.IsAvailable = state.Available != 0;
                continue;
            }

            var model = new NetPowerup
            {
                Position = CellCentre(state.CellX, state.CellY),
                Kind = (PowerupKind)state.Kind,
                IsAvailable = state.Available != 0,
            };
            var view = new Powerup3DView { Name = "NetPowerup3DView" };
            view.Bind(model);
            AddChild(view);
            _snapshotPickups[state.Id] = (model, view);
        }

        List<ushort>? gone = null;
        foreach (var (id, mirror) in _snapshotPickups)
        {
            var stillLive = false;
            foreach (var state in snapshot.Powerups)
            {
                if (state.Id == id)
                {
                    stillLive = true;
                    break;
                }
            }

            if (stillLive)
            {
                continue;
            }

            if (mirror.Model.IsAvailable && TankNear(snapshot, mirror.Model.Position))
            {
                ShowPickupFloater(mirror.Model.Position, mirror.Model.Kind); // collected, not faded
            }

            mirror.View.Free(); // not deferred — a synchronous re-read must see the fresh set
            (gone ??= new List<ushort>()).Add(id);
        }

        if (gone is not null)
        {
            foreach (var id in gone)
            {
                _snapshotPickups.Remove(id);
            }
        }
    }

    // ponytail: "a tank within a tile" approximates "collected it" — good enough for a floater.
    private static bool TankNear(SnapshotFrame snapshot, NVector2 position)
    {
        foreach (var tank in snapshot.Tanks)
        {
            if (tank.Hp > 0 && NVector2.Distance(new NVector2(tank.X, tank.Y), position) <= TileSize)
            {
                return true;
            }
        }

        return false;
    }

    private void ShowPickupFloater(NVector2 position, PowerupKind kind)
    {
        var floater = new PickupFloater3D { Name = "PickupFloater" };
        AddChild(floater);
        floater.Show(GroundProjection.ToWorld(position, 40f), PickupFloater.LabelKeyFor(kind));
    }

    // Rebuild the guest's shot views from the snapshot's live projectile set (ADR-0019 step 4). The
    // guest runs no world, so each snapshot replaces the lot — Free() (not deferred QueueFree) so a
    // synchronous re-read sees the fresh set, and a heading rebuilt from the wire angle points a
    // missile bolt along its travel exactly as the host renders it.
    private void MirrorProjectiles(IReadOnlyList<Domain.Net.ProjectileState> shots)
    {
        foreach (var view in _snapshotProjectiles)
        {
            view.Free();
        }

        _snapshotProjectiles.Clear();
        foreach (var shot in shots)
        {
            var model = new NetProjectile
            {
                Position = new NVector2(shot.X, shot.Y),
                Direction = new NVector2(Mathf.Sin(shot.Rotation), Mathf.Cos(shot.Rotation)),
                Style = (ProjectileStyle)shot.Style,
                Layer = shot.Layer,
            };
            var view = new Projectile3DView { Name = "NetProjectile3DView" };
            view.Bind(model);
            AddChild(view);
            _snapshotProjectiles.Add(view);
        }
    }

    private void SyncLocalFromPrediction()
    {
        if (_predicted is null || _localSlot is not byte slot)
        {
            return;
        }

        var tank = EnsureMirroredTank(slot);
        tank.Position = _predicted.Position;
        tank.Rotation = _predicted.Rotation;
        tank.TurretRotation = _predicted.TurretRotation;
        tank.Hp = _predicted.Hp;
        tank.Team = _predicted.Team;
        tank.Layer = _predicted.Layer; // a cross-layer teleport must lift the guest's own tank too
        tank.Shield = _predicted.Shield;
        _tankViews[slot].ApplyTeamTint(tank.Team);
    }

    // The round ends when one team is left standing — in FFA every tank is its own team, so this
    // is also last-tank-standing. Evaluated host-side after each authoritative tick.
    private void EvaluateRound()
    {
        _roundStatus.Clear();
        foreach (var (slot, tank) in _rosterTanks)
        {
            _netStats.Observe(slot, tank.Hp); // the host's side of the shared stat book
            _roundStatus.Add((tank.Team, tank.IsAlive));
        }

        var result = LastStanding.Evaluate(_roundStatus);
        if (result.Decided)
        {
            RoundResult = result;
            _status.SetStatus(RoundOverText(result.WinningTeam));
            _rematch.Visible = IsLobbyHost;
            ShowVictoryScreen(result.WinningTeam);
        }
    }

    // A guest has no world, but the snapshot carries every tank's hp and team — the same
    // last-standing rule the host runs gives it the same verdict at the same moment. Hp 0 means
    // out for good: net tanks have a single life (no respawns in a networked round).
    private void DetectRoundOverFromSnapshot(SnapshotFrame snapshot)
    {
        if (RoundResult is not null || snapshot.Tanks.Count < 2)
        {
            return;
        }

        _roundStatus.Clear();
        foreach (var state in snapshot.Tanks)
        {
            _netStats.Observe(state.Slot, state.Hp); // the guest's side of the shared stat book
            _roundStatus.Add((state.Team, state.Hp > 0));
        }

        var result = LastStanding.Evaluate(_roundStatus);
        if (result.Decided)
        {
            RoundResult = result;
            _status.SetStatus(RoundOverText(result.WinningTeam));
            _rematch.Visible = IsLobbyHost;
            ShowVictoryScreen(result.WinningTeam);
        }
    }

    /// <summary>The real end of a networked match (multiplayer plan: "net victory screen"): the same
    /// <see cref="VictoryScreen"/> the solo arena shows, on BOTH roles, ranked from the shared stat
    /// book — final standing, damage taken, and repairs, all derived from the hp streams each peer
    /// already observes, so nothing new travels on the wire. The card carries the online affordances
    /// (host: Rematch + Leave; guest: Leave) and the corner copies hide beneath it.</summary>
    private void ShowVictoryScreen(int winningTeam)
    {
        if (_victory is not null)
        {
            return;
        }

        var standings = _netStats.Standings();
        var standingRows = new List<VictoryScreen.Row>(standings.Count);
        foreach (var tally in standings)
        {
            // The value column shows the hit points the tank went out with — the winner's margin.
            standingRows.Add(new VictoryScreen.Row(tally.Name, tally.Hp.ToString(CultureInfo.InvariantCulture)));
        }

        var sheets = new List<VictoryScreen.Sheet>
        {
            new("stats.standing", standingRows),
            RankedSheet("stats.taken", t => t.DamageTaken, lowerIsBetter: true),
            RankedSheet("stats.repairs", t => t.Repairs, lowerIsBetter: false),
        };

        var buttons = new List<VictoryScreen.ButtonSpec>();
        if (IsLobbyHost)
        {
            buttons.Add(new VictoryScreen.ButtonSpec("VictoryRematch", "net.rematch",
                () => _transport.SendLobby(LobbyProtocol.EncodeRematch())));
        }

        buttons.Add(new VictoryScreen.ButtonSpec("VictoryLeave", "net.leave", LeaveMatch));

        _victory = VictoryScreen.Build(
            GetViewport().GetVisibleRect().Size, ChampionName(winningTeam), sheets, buttons);
        AddChild(_victory);
        _leaveLayer.Visible = false; // the screen's buttons take over — no doubled corner controls
        _matchHud.Visible = false;
    }

    private VictoryScreen.Sheet RankedSheet(
        string titleKey, System.Func<NetMatchStats.SlotTally, int> value, bool lowerIsBetter)
    {
        var rows = new List<VictoryScreen.Row>();
        foreach (var tally in LeaderboardOrder.Rank(_netStats.Tallies, value, lowerIsBetter))
        {
            rows.Add(new VictoryScreen.Row(tally.Name, value(tally).ToString(CultureInfo.InvariantCulture)));
        }

        return new VictoryScreen.Sheet(titleKey, rows);
    }

    // The ribbon's headliner: the winner's name when the winning team is a single tank (always, in
    // FFA), "Team N" for a squad, and null (= the ribbon says draw) when nobody survived.
    private string? ChampionName(int winningTeam)
    {
        if (winningTeam == LastStanding.NoWinner)
        {
            return null;
        }

        string? sole = null;
        var teamSize = 0;
        foreach (var seat in _roster)
        {
            if (seat.Team == winningTeam)
            {
                sole = seat.Name;
                teamSize++;
            }
        }

        return teamSize == 1 && sole is not null
            ? sole
            : string.Format(CultureInfo.InvariantCulture, Tr("net.team_label"), winningTeam + 1);
    }

    // "{name} wins!" when one tank owns the winning team (always true in FFA), "Team N wins!"
    // for a multi-tank team, and the plain round-over banner for a drawn round (nobody left).
    private string RoundOverText(int winningTeam)
    {
        if (winningTeam == LastStanding.NoWinner)
        {
            return Tr(NetStatusOverlay.RoundOverKey);
        }

        string? soleWinner = null;
        var teamSize = 0;
        foreach (var seat in _roster)
        {
            if (seat.Team == winningTeam)
            {
                soleWinner = seat.Name;
                teamSize++;
            }
        }

        return teamSize == 1 && soleWinner is not null
            ? string.Format(CultureInfo.InvariantCulture, Tr("net.round_winner"), soleWinner)
            : string.Format(CultureInfo.InvariantCulture, Tr("net.round_winner_team"), winningTeam + 1);
    }

    private NetTank EnsureMirroredTank(byte slot)
    {
        if (_tanks.TryGetValue(slot, out var existing))
        {
            return (NetTank)existing;
        }

        var tank = new NetTank(TankMaxHp);
        if (slot < _roster.Count)
        {
            tank.DisplayName = _roster[slot].Name; // a guest names its mirrors from the shared roster
        }

        _tanks[slot] = tank;
        AddTankView(slot, tank);
        return tank;
    }

    private void AddTankView(byte slot, ITank tank)
    {
        var view = new Tank3DView { Name = $"Tank3DView{slot}" };
        view.Bind(tank);
        AddChild(view);
        _tankViews[slot] = view;
        view.ApplyTeamTint(tank.Team);
    }

    private void FollowLocalTank()
    {
        if (_localSlot is not byte slot || !_tanks.TryGetValue(slot, out var local))
        {
            return;
        }

        var target = GroundProjection.ToWorld(local.Position);
        _camera.Position = target + CameraOffset();
        _camera.LookAt(target, Vector3.Up);
    }

    private static Vector3 CameraOffset()
    {
        var pitch = Mathf.DegToRad(CamPitchDeg);
        var yaw = Mathf.DegToRad(CamYawDeg);
        var dir = new Vector3(Mathf.Cos(pitch) * Mathf.Sin(yaw), Mathf.Sin(pitch), Mathf.Cos(pitch) * Mathf.Cos(yaw));
        return dir * CamDistance;
    }

    // Plain daylight (no fog of war in versus — both players deserve the same information), the same
    // ortho ¾ camera as the local arena, and a flat dusty ground plane.
    private void BuildEnvironment()
    {
        _camera = new Camera3D
        {
            Name = "GameCamera",
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = CamOrthoSize,
            Far = 12000f,
            Near = 1f,
            PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off,
        };
        AddChild(_camera);
        var centre = GroundProjection.ToWorld(new NVector2(_level.Width * TileSize / 2f, _level.Height * TileSize / 2f));
        _camera.Position = centre + CameraOffset();
        _camera.LookAt(centre, Vector3.Up);

        AddChild(new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-55f, -40f, 0f),
            LightEnergy = 1f,
            ShadowEnabled = true,
        });

        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.55f, 0.62f, 0.70f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.65f, 0.65f, 0.62f),
                AmbientLightEnergy = 1f,
                TonemapMode = Godot.Environment.ToneMapper.Aces,
            },
        });
    }

    private void BuildGround()
    {
        var w = _level.Width * TileSize;
        var h = _level.Height * TileSize;
        AddChild(new MeshInstance3D
        {
            Name = "Ground",
            Mesh = new PlaneMesh { Size = new Vector2(w, h) },
            Position = new Vector3(w / 2f, 0f, h / 2f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.66f, 0.58f, 0.40f), // dusty sand
                Roughness = 1f,
                SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            },
        });
    }

    private static NVector2 CellCentre(int x, int y) =>
        new(GridOrigin.X + ((x + 0.5f) * TileSize), GridOrigin.Y + ((y + 0.5f) * TileSize));
}
