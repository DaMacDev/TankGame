using System;
using System.Collections.Generic;
using Godot;
using Chickensoft.GoDotTest;
using TankGame.Domain;
using TankGame.Domain.Net;
using TankGame.Presentation;

namespace TankGame.Tests.Presentation;

// Drives the 3D networked play scene (ADR-0019 step 3) against a fake transport, both roles:
// the HOST runs the real World (HostSession) and broadcasts snapshots; a GUEST predicts its own
// tank and mirrors the rest from snapshots. No socket, no relay — the fake delivers frames.
public class NetArena3DSceneTests : TestClass
{
    private sealed class FakeTransport : IMatchTransport
    {
        public List<InputFrame> SentInputs { get; } = new();
        public List<SnapshotFrame> Broadcast { get; } = new();
        public List<byte[]> SentLobby { get; } = new();
        public event Action<byte>? WelcomeReceived;
        public event Action<SnapshotFrame>? SnapshotReceived;
        public event Action<InputFrame>? InputReceived;
        public event Action<LobbyView>? LobbyStateReceived;

        public void SendInput(InputFrame input) => SentInputs.Add(input);
        public void SendSnapshot(SnapshotFrame snapshot) => Broadcast.Add(snapshot);
        public void SendLobby(byte[] command) => SentLobby.Add(command);
        public void Poll() { }

        public void DeliverWelcome(byte slot) => WelcomeReceived?.Invoke(slot);
        public void DeliverSnapshot(SnapshotFrame snapshot) => SnapshotReceived?.Invoke(snapshot);
        public void DeliverInput(InputFrame frame) => InputReceived?.Invoke(frame);
        public void DeliverLobby(LobbyView view) => LobbyStateReceived?.Invoke(view);
    }

    private Func<string, IMatchTransport> _originalFactory = default!;
    private FakeTransport _transport = default!;
    private NetArena3DScene _scene = default!;

    public NetArena3DSceneTests(Node testScene) : base(testScene) { }

    [Setup]
    public void Setup()
    {
        _originalFactory = NetworkSession.TransportFactory;
        _transport = new FakeTransport();
        NetworkSession.TransportFactory = _ => _transport;
        NetworkSession.Join("ABC123");

        _scene = GD.Load<PackedScene>("res://src/Presentation/Arena/NetArena3D.tscn")
            .Instantiate<NetArena3DScene>();
        TestScene.AddChild(_scene);
    }

    // Free immediately + force GC (the Arena3D teardown-leak pattern): no managed wrapper to a freed
    // Godot resource may survive to engine shutdown.
    [Cleanup]
    public void Cleanup()
    {
        if (GodotObject.IsInstanceValid(_scene))
        {
            _scene.Free();
        }

        NetworkSession.TransportFactory = _originalFactory;
        NetworkSession.Reset();
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        System.GC.Collect();
    }

    [Test]
    public void TheHud_CountsEnemies_ThenPointsAtThemAfterAQuietSpell_AndStopsWhenHit()
    {
        TankGame.Infrastructure.TranslationLoader.EnsureLoaded();
        _transport.DeliverWelcome(1);
        void Snapshot(uint tick, byte ownHp) => _transport.DeliverSnapshot(new SnapshotFrame(tick, 0,
            new List<TankState>
            {
                new(0, 96f, 160f, 0f, 0f, 8, 0),
                new(1, 2000f, 2000f, 0f, 0f, ownHp, 1),
                new(2, 3000f, 160f, 0f, 0f, 0, 2),
            },
            new List<WallDelta>()));
        Snapshot(1, 8);
        _scene.Tick(0.1f);

        var label = _scene.FindChild("EnemiesLeft", recursive: true, owned: false) as Label
            ?? throw new Exception("The net arena must show the alive-enemies counter.");
        if (!label.Text.Contains('1'))
        {
            throw new Exception($"One enemy lives and one is out; the counter showed '{label.Text}'.");
        }

        var arrow = _scene.FindChild("EnemyArrow0", recursive: true, owned: false) as Node2D
            ?? throw new Exception("The net arena must have enemy arrows.");
        if (arrow.Visible)
        {
            throw new Exception("No arrow until the player has gone a while without fighting.");
        }

        for (var i = 0; i < 20; i++)
        {
            _scene.Tick(0.5f);
        }

        if (!arrow.Visible)
        {
            throw new Exception("A quiet spell must bring the arrow up.");
        }

        Snapshot(2, 5);
        _scene.Tick(0.1f);
        if (arrow.Visible)
        {
            throw new Exception("Taking a hit means fighting; the arrow must go.");
        }
    }

    // Seen in a real run: a seat spawned on a plateau cell while tanks start on the ground layer, so the
    // tank sat inside the plateau, invisible and unable to move.
    [Test]
    public void OnCliffs_EverySeatStartsOnGroundItCanDriveOn()
    {
        NetworkSession.StartedLobby = new LobbyView(TankGame.Domain.Net.GameMode.Ffa, LobbyPhase.Started, 0, 0,
            new List<LobbyPlayer> { new(0, "Ada", 0, true, true) }, "CliffsAndValleys");
        var grid = TankGame.GameLogic.CliffsArena.Create().Map.BuildGrid();

        for (var seed = 0; seed < 8; seed++)
        {
            NetworkSession.StartedLobby = NetworkSession.StartedLobby with { Seed = seed * 1000 };
            NetworkSession.LocalSlot = 0;
            var scene = GD.Load<PackedScene>("res://src/Presentation/Arena/NetArena3D.tscn")
                .Instantiate<NetArena3DScene>();
            TestScene.AddChild(scene);
            try
            {
                foreach (var (slot, tank) in scene.Tanks)
                {
                    var x = (int)(tank.Position.X / 64f);
                    var y = (int)(tank.Position.Y / 64f);
                    if (grid.IsBlocked(x, y) || grid.LayerAt(x, y) != tank.Layer)
                    {
                        throw new Exception(
                            $"Seed {seed}: seat {slot} on layer {tank.Layer} starts in cell ({x},{y}) of layer {grid.LayerAt(x, y)}.");
                    }
                }
            }
            finally
            {
                scene.Free();
            }
        }
    }

    // The relay routes guest input to the lobby's host seat. If that seat is not slot 0 (the creator
    // left and the role moved on), it must still run the authority, or no client simulates anything.
    [Test]
    public void TheLobbyHostSeat_RunsTheMatch_EvenWhenItIsNotSlotZero()
    {
        NetworkSession.StartedLobby = new LobbyView(TankGame.Domain.Net.GameMode.Ffa, LobbyPhase.Started, 1, 0,
            new List<LobbyPlayer> { new(1, "Bea", 1, true, true), new(2, "Cid", 2, true, true) });
        NetworkSession.LocalSlot = 1;
        var scene = GD.Load<PackedScene>("res://src/Presentation/Arena/NetArena3D.tscn")
            .Instantiate<NetArena3DScene>();
        TestScene.AddChild(scene);

        try
        {
            scene.Tick(0.05f);
            if (_transport.Broadcast.Count == 0)
            {
                throw new Exception("The host seat must broadcast snapshots whichever slot it sits in.");
            }
        }
        finally
        {
            scene.Free();
        }
    }

    [Test]
    public void GuestWelcome_AdoptsTheSlot_AndShowsItsOwnTank()
    {
        _transport.DeliverWelcome(1);

        if (_scene.LocalSlot != 1)
        {
            throw new Exception($"Expected local slot 1, got {_scene.LocalSlot}.");
        }

        if (!_scene.Tanks.ContainsKey(1))
        {
            throw new Exception("The welcomed guest must have its own (predicted) tank.");
        }
    }

    [Test]
    public void GuestSnapshot_MirrorsTheHostTank_AndAppliesWallDeltas()
    {
        _transport.DeliverWelcome(1);
        _transport.DeliverSnapshot(new SnapshotFrame(1, 0,
            new List<TankState> { new(0, 96f, 160f, 0.5f, 1f, 8, 0) },
            new List<WallDelta> { new(3, 1, 0, 0) })); // the cell at (3,1) broke to floor

        if (!_scene.Tanks.TryGetValue(0, out var host))
        {
            throw new Exception("A snapshot slot the guest has not seen must be mirrored into a tank.");
        }

        if (Math.Abs(host.Position.X - 96f) > 0.01f || Math.Abs(host.Position.Y - 160f) > 0.01f)
        {
            throw new Exception($"The remote tank must mirror the snapshot; got {host.Position}.");
        }

        if (_scene.CellAt(3, 1).Material != CellMaterial.Floor)
        {
            throw new Exception("A wall delta must be applied to the shared grid.");
        }
    }

    [Test]
    public void GuestSnapshot_MirrorsRemoteTankShieldAndLayer()
    {
        _transport.DeliverWelcome(1);
        _transport.DeliverSnapshot(new SnapshotFrame(1, 0,
            new List<TankState> { new(0, 96f, 160f, 0f, 0f, 8, 0, Shield: 5, Layer: 2) },
            new List<WallDelta>()));

        if (!_scene.Tanks.TryGetValue(0, out var host))
        {
            throw new Exception("The remote tank must be mirrored.");
        }

        if (host.Shield != 5)
        {
            throw new Exception($"A shielded remote tank must mirror its shield; got {host.Shield}.");
        }

        if (host.Layer != 2)
        {
            throw new Exception($"A remote tank on a plateau must mirror its layer; got {host.Layer}.");
        }
    }

    [Test]
    public void GuestSnapshot_MirrorsProjectiles_AndClearsThemWhenGone()
    {
        _transport.DeliverWelcome(1);
        _transport.DeliverSnapshot(new SnapshotFrame(1, 0,
            new List<TankState>(), new List<WallDelta>(),
            new List<ProjectileState> { new(120f, 200f, 0.5f, (byte)ProjectileStyle.Normal, 0) }));

        if (_scene.MirroredProjectileCount != 1)
        {
            throw new Exception($"The guest must mirror the snapshot's shots; saw {_scene.MirroredProjectileCount}.");
        }

        // A later snapshot with no shots clears them — the shot landed on the host.
        _transport.DeliverSnapshot(new SnapshotFrame(2, 0,
            new List<TankState>(), new List<WallDelta>(), new List<ProjectileState>()));

        if (_scene.MirroredProjectileCount != 0)
        {
            throw new Exception("A snapshot with no shots must clear the mirrored projectiles.");
        }
    }

    [Test]
    public void GuestTick_SendsOneInputFrame_PerFixedTick()
    {
        _transport.DeliverWelcome(1);

        _scene.Tick(0.06f);  // one 20 Hz tick (0.01 carried over)
        _scene.Tick(0.02f);  // not enough for another
        _scene.Tick(0.03f);  // accumulates past the second tick

        if (_transport.SentInputs.Count != 2)
        {
            throw new Exception($"Expected 2 input frames over 0.11s at 20 Hz, sent {_transport.SentInputs.Count}.");
        }

        if (_transport.SentInputs[1].Seq != _transport.SentInputs[0].Seq + 1)
        {
            throw new Exception("Input frames must carry a monotonically increasing seq.");
        }
    }

    [Test]
    public void Ready_ShowsALeaveButton_SoATouchPlayerCanExitTheMatch()
    {
        // The button exists from _Ready — before any welcome — so a player can bail out even while
        // the connection is still "Connecting…" (there is no pause menu in a versus match).
        if (_scene.FindChild("LeaveButton", recursive: true, owned: false) is not Button)
        {
            throw new Exception("The networked match must show a Leave button so a touch player can exit.");
        }
    }

    [Test]
    public void LeaveMatch_DropsTheActiveSession()
    {
        _transport.DeliverWelcome(1);

        _scene.LeaveMatch();

        if (NetworkSession.Active is not null)
        {
            throw new Exception("Leaving the match must drop the active transport so it is not reused.");
        }
    }

    [Test]
    public void HostWelcome_BuildsTheAuthoritativeMatch_AndBroadcastsSnapshots()
    {
        _transport.DeliverWelcome(0);

        if (_scene.LocalSlot != 0)
        {
            throw new Exception($"Expected local slot 0, got {_scene.LocalSlot}.");
        }

        if (!_scene.Tanks.ContainsKey(0) || !_scene.Tanks.ContainsKey(1))
        {
            throw new Exception("The host must build the authoritative world with both players' tanks.");
        }

        _scene.Tick(0.05f);

        if (_transport.Broadcast.Count != 1)
        {
            throw new Exception($"One 20 Hz tick must broadcast one snapshot; sent {_transport.Broadcast.Count}.");
        }
    }

    // Regression: the welcome is a one-shot fired on connect, during the lobby — long before this
    // scene exists. Production hands off with the slot carried on NetworkSession and NO second welcome
    // on the wire; the scene must still initialize from the carried slot (else: no tanks, stuck
    // "Connecting…", "pressing start does nothing"). A fresh scene is built here so its _Ready runs
    // after the slot is set, matching the real handoff order.
    [Test]
    public void CarriedSlot_InitializesTheMatch_WithoutASecondWelcome()
    {
        NetworkSession.LocalSlot = 0;
        var scene = GD.Load<PackedScene>("res://src/Presentation/Arena/NetArena3D.tscn")
            .Instantiate<NetArena3DScene>();
        TestScene.AddChild(scene);

        try
        {
            if (scene.LocalSlot != 0)
            {
                throw new Exception($"The carried lobby slot must be adopted on ready; got {scene.LocalSlot}.");
            }

            if (!scene.Tanks.ContainsKey(0) || !scene.Tanks.ContainsKey(1))
            {
                throw new Exception("Adopting the carried slot must build the authoritative match tanks.");
            }
        }
        finally
        {
            scene.Free();
        }
    }

    // Regression for the loading-handshake deadlock: after the countdown the room hands off during the
    // "loading" phase. The scene MUST report "loaded" (else the server never starts — every net match
    // hangs) and MUST hold the match on the loading banner until the server flips to "started".
    [Test]
    public void LoadingHandoff_ReportsLoaded_ThenHoldsUntilStarted()
    {
        NetworkSession.StartedLobby = new LobbyView(TankGame.Domain.Net.GameMode.Ffa, LobbyPhase.Loading, 0, 0,
            new List<LobbyPlayer> { new(0, "Host", 0, true), new(1, "Guest", 1, true) });
        NetworkSession.LocalSlot = 0;
        var scene = GD.Load<PackedScene>("res://src/Presentation/Arena/NetArena3D.tscn")
            .Instantiate<NetArena3DScene>();
        TestScene.AddChild(scene);

        try
        {
            if (_transport.SentLobby.Count != 1)
            {
                throw new Exception(
                    $"Entering during loading must report loaded exactly once; sent {_transport.SentLobby.Count}.");
            }

            scene.Tick(0.1f); // held: a tick before "started" must not broadcast
            if (_transport.Broadcast.Count != 0)
            {
                throw new Exception("The match must hold (no snapshots) until the server flips to started.");
            }

            _transport.DeliverLobby(new LobbyView(TankGame.Domain.Net.GameMode.Ffa, LobbyPhase.Started, 0, 0,
                new List<LobbyPlayer> { new(0, "Host", 0, true, true), new(1, "Guest", 1, true, true) }));
            scene.Tick(0.1f);
            if (_transport.Broadcast.Count == 0)
            {
                throw new Exception("Once started, the host must run its world and broadcast snapshots.");
            }
        }
        finally
        {
            scene.Free();
        }
    }

    // A themed built-in (Volcano has lava + bridges) picked in a net lobby must build in the net scene,
    // not crash casting the map choice to Desert. Host builds the authoritative world for the full roster.
    [Test]
    public void ThemedMap_BuildsTheNetArena_ForTheFullRoster()
    {
        NetworkSession.StartedLobby = new LobbyView(TankGame.Domain.Net.GameMode.Ffa, LobbyPhase.Started, 0, 0,
            new List<LobbyPlayer> { new(0, "Host", 0, true, true) }, Map: "Volcano");
        NetworkSession.LocalSlot = 0;
        var scene = GD.Load<PackedScene>("res://src/Presentation/Arena/NetArena3D.tscn")
            .Instantiate<NetArena3DScene>();
        TestScene.AddChild(scene);

        try
        {
            // Host fills every empty seat with AI, so a themed net match seats the full room.
            if (scene.Tanks.Count != LobbyProtocol.MaxPlayers)
            {
                throw new Exception(
                    $"A themed net map must build the authoritative roster; saw {scene.Tanks.Count} tanks.");
            }
        }
        finally
        {
            scene.Free();
        }
    }

    [Test]
    public void GuestSnapshot_DetectsTheDecidedRound_AndNamesTheWinner()
    {
        _transport.DeliverWelcome(1);

        // Both tanks standing: still fighting.
        _transport.DeliverSnapshot(new SnapshotFrame(1, 0,
            new List<TankState> { new(0, 96f, 160f, 0f, 0f, 8, 0), new(1, 200f, 96f, 0f, 0f, 8, 1) },
            new List<WallDelta>()));
        if (_scene.RoundResult is not null)
        {
            throw new Exception("Two tanks standing must not end the round.");
        }

        // The guest (slot 1, team 1) falls: the host's tank is the last one standing.
        _transport.DeliverSnapshot(new SnapshotFrame(2, 0,
            new List<TankState> { new(0, 96f, 160f, 0f, 0f, 8, 0), new(1, 200f, 96f, 0f, 0f, 0, 1) },
            new List<WallDelta>()));

        if (_scene.RoundResult is not { Decided: true, WinningTeam: 0 })
        {
            throw new Exception(
                $"A snapshot with one team left must decide the round on the guest; got {_scene.RoundResult}.");
        }
    }

    // The authority decides a round from its own world: every tank but the survivor falls.
    private static void DecideRoundFor(NetArena3DScene scene, byte survivor)
    {
        foreach (var (slot, tank) in scene.Tanks)
        {
            if (slot != survivor)
            {
                tank.TakeDamage(tank.Hp + tank.Shield);
            }
        }

        scene.Tick(0.06f);
    }

    // Online rematch: once the round is decided, the LOBBY host (and only the host) gets a Rematch
    // button beside Leave; pressing it sends the rematch lobby command over the same socket.
    [Test]
    public void RoundOver_RevealsRematch_ForTheLobbyHost_AndPressingSendsTheCommand()
    {
        NetworkSession.StartedLobby = new LobbyView(TankGame.Domain.Net.GameMode.Ffa, LobbyPhase.Started, 1, 0,
            new List<LobbyPlayer> { new(0, "Ada", 0, true, true), new(1, "Bea", 1, true, true) });
        NetworkSession.LocalSlot = 1; // this client sits in the lobby's host seat, so it runs the match
        var scene = GD.Load<PackedScene>("res://src/Presentation/Arena/NetArena3D.tscn")
            .Instantiate<NetArena3DScene>();
        TestScene.AddChild(scene);

        try
        {
            var rematch = scene.FindChild("RematchButton", recursive: true, owned: false) as Button
                ?? throw new Exception("The networked match must have a Rematch button.");
            if (rematch.Visible)
            {
                throw new Exception("Rematch must stay hidden while the round is being fought.");
            }

            DecideRoundFor(scene, survivor: 1);

            if (!rematch.Visible)
            {
                throw new Exception("The lobby host must see the Rematch button once the round is decided.");
            }

            rematch.EmitSignal(BaseButton.SignalName.Pressed);
            if (_transport.SentLobby.Count == 0
                || !System.MemoryExtensions.SequenceEqual<byte>(
                    _transport.SentLobby[^1], LobbyProtocol.EncodeRematch()))
            {
                throw new Exception("Pressing Rematch must send the rematch lobby command.");
            }
        }
        finally
        {
            scene.Free();
        }
    }

    // The server answered the rematch by resetting the room to "waiting": every client returns to the
    // lobby room on the SAME socket, carrying the fresh waiting view for the room's controller.
    [Test]
    public void WaitingPush_ReturnsToTheRoom_KeepingTheTransport()
    {
        NetworkSession.StartedLobby = new LobbyView(TankGame.Domain.Net.GameMode.Ffa, LobbyPhase.Started, 0, 0,
            new List<LobbyPlayer> { new(0, "Ada", 0, true, true), new(1, "Bea", 1, true, true) });
        NetworkSession.LocalSlot = 1;
        var scene = GD.Load<PackedScene>("res://src/Presentation/Arena/NetArena3D.tscn")
            .Instantiate<NetArena3DScene>();
        TestScene.AddChild(scene);

        try
        {
            var waiting = new LobbyView(TankGame.Domain.Net.GameMode.Ffa, LobbyPhase.Waiting, 0, 0,
                new List<LobbyPlayer> { new(0, "Ada", 0, false), new(1, "Bea", 1, false) });
            _transport.DeliverLobby(waiting);

            if (!ReferenceEquals(NetworkSession.StartedLobby, waiting))
            {
                throw new Exception("Returning to the room must carry the fresh waiting view for its controller.");
            }

            if (NetworkSession.Active is null)
            {
                throw new Exception("A rematch reuses the socket — the transport must NOT be dropped.");
            }
        }
        finally
        {
            scene.Free();
        }
    }

    // The real end of a networked match (net victory screen): a GUEST whose deciding snapshot
    // arrives must show the full victory screen v2 — the winner's ribbon, the standing sheet ranked
    // from the hp stream it observed, and a Leave button on the card — with the corner buttons
    // hidden beneath it (the card's buttons take over).
    [Test]
    public void GuestRoundDecided_ShowsTheVictoryScreen_RankedFromObservedHp()
    {
        _transport.DeliverWelcome(1);
        _transport.DeliverSnapshot(new SnapshotFrame(1, 0,
            new List<TankState> { new(0, 96f, 160f, 0f, 0f, 8, 0), new(1, 200f, 96f, 0f, 0f, 8, 1) },
            new List<WallDelta>()));
        _transport.DeliverSnapshot(new SnapshotFrame(2, 0,
            new List<TankState> { new(0, 96f, 160f, 0f, 0f, 8, 0), new(1, 200f, 96f, 0f, 0f, 0, 1) },
            new List<WallDelta>()));

        if (_scene.FindChild("VictoryCard", recursive: true, owned: false) is null)
        {
            throw new Exception("A decided round must show the victory screen on the guest.");
        }

        var winner = _scene.FindChild("WinnerName", recursive: true, owned: false) as Label
            ?? throw new Exception("The victory screen must carry the winner's ribbon.");
        if (winner.Text != _scene.Tanks[0].DisplayName || winner.Text.Length == 0)
        {
            throw new Exception($"The ribbon must name the surviving tank; got '{winner.Text}'.");
        }

        var title = _scene.FindChild("ViewTitle", recursive: true, owned: false) as Label
            ?? throw new Exception("The victory screen must name its ranking sheet.");
        if (title.Text != "stats.standing")
        {
            throw new Exception($"The net board opens on the final standing; got '{title.Text}'.");
        }

        var rows = _scene.FindChild("LeaderboardRows", recursive: true, owned: false) as Control
            ?? throw new Exception("The victory screen must show the ranked rows.");
        if (rows.GetChildCount() != 2)
        {
            throw new Exception($"Both tanks must rank on the sheet; saw {rows.GetChildCount()} rows.");
        }

        if (_scene.FindChild("VictoryLeave", recursive: true, owned: false) is not Button)
        {
            throw new Exception("A guest must keep a Leave affordance on the victory screen.");
        }

        if (_scene.FindChild("VictoryRematch", recursive: true, owned: false) is not null)
        {
            throw new Exception("A non-host must not be offered Rematch.");
        }

        var corner = _scene.FindChild("LeaveLayer", recursive: true, owned: false) as CanvasLayer
            ?? throw new Exception("The corner button layer must still exist.");
        if (corner.Visible)
        {
            throw new Exception("The corner buttons must hide once the victory screen carries them.");
        }
    }

    // The HOST reaches the same screen from its authoritative world: when its last opponent falls,
    // the next tick decides the round and raises the victory screen there too.
    [Test]
    public void HostRoundDecided_ShowsTheVictoryScreen()
    {
        _transport.DeliverWelcome(0);

        _scene.Tanks[1].TakeDamage(8); // the guest tank falls (net tanks have a single life)
        _scene.Tick(0.05f);            // the deciding authoritative tick

        if (_scene.RoundResult is not { Decided: true })
        {
            throw new Exception("Downing the only opponent must decide the round on the host.");
        }

        if (_scene.FindChild("VictoryCard", recursive: true, owned: false) is null)
        {
            throw new Exception("A decided round must show the victory screen on the host.");
        }

        if (_scene.FindChild("VictoryLeave", recursive: true, owned: false) is not Button)
        {
            throw new Exception("The host must keep a Leave affordance on the victory screen.");
        }
    }

    // Requirement: the online rematch keeps working from the new screen — the LOBBY host's victory
    // card carries a Rematch button whose press sends the same lobby command as the old corner one.
    [Test]
    public void VictoryScreenRematch_SendsTheRematchCommand_ForTheLobbyHost()
    {
        NetworkSession.StartedLobby = new LobbyView(TankGame.Domain.Net.GameMode.Ffa, LobbyPhase.Started, 1, 0,
            new List<LobbyPlayer> { new(0, "Ada", 0, true, true), new(1, "Bea", 1, true, true) });
        NetworkSession.LocalSlot = 1; // this client sits in the lobby's host seat, so it runs the match
        var scene = GD.Load<PackedScene>("res://src/Presentation/Arena/NetArena3D.tscn")
            .Instantiate<NetArena3DScene>();
        TestScene.AddChild(scene);

        try
        {
            // Bea (slot 1) falls — the round is decided.
            DecideRoundFor(scene, survivor: 1);

            var winner = scene.FindChild("WinnerName", recursive: true, owned: false) as Label
                ?? throw new Exception("The victory screen must show on the lobby host.");
            if (winner.Text != "Bea")
            {
                throw new Exception($"The ribbon must carry the roster name of the survivor; got '{winner.Text}'.");
            }

            var rematch = scene.FindChild("VictoryRematch", recursive: true, owned: false) as Button
                ?? throw new Exception("The lobby host's victory screen must offer Rematch.");
            rematch.EmitSignal(BaseButton.SignalName.Pressed);

            if (_transport.SentLobby.Count == 0
                || !System.MemoryExtensions.SequenceEqual<byte>(
                    _transport.SentLobby[^1], LobbyProtocol.EncodeRematch()))
            {
                throw new Exception("Pressing the card's Rematch must send the rematch lobby command.");
            }
        }
        finally
        {
            scene.Free();
        }
    }

    [Test]
    public void HostTick_DrivesTheGuestTank_FromARelayedInput()
    {
        _transport.DeliverWelcome(0);
        var guestStart = _scene.Tanks[1].Position;
        // Slot 1: the relay stamps the sender's slot on every input, so the host routes it there.
        _transport.DeliverInput(new InputFrame(Seq: 1, MoveX: 1f, MoveY: 0f, Aim: 0f, Buttons: 0, Slot: 1));

        for (var i = 0; i < 4; i++)
        {
            _scene.Tick(0.05f);
        }

        if (_scene.Tanks[1].Position.X <= guestStart.X)
        {
            throw new Exception("A relayed guest input must drive the guest tank in the host world.");
        }

        var last = _transport.Broadcast[^1];
        if (last.AckSeq != 1)
        {
            throw new Exception($"The broadcast snapshot must ack the applied guest input; got {last.AckSeq}.");
        }
    }

    // ── Teleport pads in the net world (multiplayer plan: "Cliffs teleport pads") ──
    // Pads are static map features every member derives from the same map resolution, so nothing
    // about them travels on the wire: the host's authoritative tanks warp inside World.Step and the
    // new position/layer rides the normal snapshot; every peer renders the same rings.

    private const float NetTile = 64f;

    private static System.Numerics.Vector2 NetCellCentre(int x, int y) =>
        new((x + 0.5f) * NetTile, (y + 0.5f) * NetTile);

    private static LobbyView CliffsLobby(int seed)
    {
        // Eight humans fill every seat — no AI, so the host world only moves tanks we drive.
        var players = new List<LobbyPlayer>();
        for (var slot = 0; slot < LobbyProtocol.MaxPlayers; slot++)
        {
            players.Add(new LobbyPlayer(slot, $"P{slot}", slot, true, true));
        }

        return new LobbyView(TankGame.Domain.Net.GameMode.Ffa, LobbyPhase.Started, 0, 0, players,
            Map: "CliffsAndValleys", Seed: seed);
    }

    // Cliffs authors a cross-layer pad pair: the net scene must derive the same two rings the solo
    // arena shows — one down on the valley floor, one up at plateau height — on every role, with
    // nothing on the wire (a guest builds them straight from the shared map resolution too).
    [Test]
    public void CliffsNetMatch_BuildsTheCrossLayerPadRings()
    {
        NetworkSession.StartedLobby = CliffsLobby(seed: 1);
        NetworkSession.LocalSlot = 1; // a guest — the role with no world must still show the rings
        var scene = GD.Load<PackedScene>("res://src/Presentation/Arena/NetArena3D.tscn")
            .Instantiate<NetArena3DScene>();
        TestScene.AddChild(scene);

        try
        {
            var ground = 0;
            var raised = 0;
            foreach (var child in scene.GetChildren())
            {
                if (child is TeleportPad3DView view)
                {
                    if (view.Position.Y >= GroundProjection.LayerHeight - 0.5f)
                    {
                        raised++;
                    }
                    else
                    {
                        ground++;
                    }
                }
            }

            if (ground != 1 || raised != 1)
            {
                throw new Exception(
                    $"A Cliffs net match must place the cross-layer pad pair (one valley ring, one plateau ring); saw {ground} ground + {raised} raised.");
            }
        }
        finally
        {
            scene.Free();
        }
    }

    // End-to-end host-side warp: a guest tank relayed onto the valley pad (cell 2,2) must come out
    // on the plateau pad (cell 22,18) — across the map AND one layer up — through the authoritative
    // world alone. Deterministic: every seat is human, so no AI moves or shoots, and the seeded
    // spawn placement is fixed per lobby seed.
    [Test]
    public void CliffsNetHost_WarpsARelayedTank_AcrossTheMapAndUpALayer()
    {
        NetworkSession.StartedLobby = CliffsLobby(seed: 1);
        NetworkSession.LocalSlot = 0; // this client hosts the authoritative world
        var scene = GD.Load<PackedScene>("res://src/Presentation/Arena/NetArena3D.tscn")
            .Instantiate<NetArena3DScene>();
        TestScene.AddChild(scene);

        try
        {
            // Spawns are seeded-random, so WHICH guest starts nearest the valley pad is fixed per
            // seed but opaque — find it and drive it. The host (slot 0) has no relayed input, so it
            // is excluded; if a placement change ever strands every guest behind a wall on the way
            // to the pad, pick a different seed above.
            var padA = NetCellCentre(2, 2);
            byte? padSlot = null;
            var best = float.MaxValue;
            foreach (var (slot, seated) in scene.Tanks)
            {
                var d = System.Numerics.Vector2.Distance(seated.Position, padA);
                if (slot != 0 && d < best)
                {
                    best = d;
                    padSlot = slot;
                }
            }

            if (padSlot is not byte driven)
            {
                throw new Exception("Expected at least one guest tank to drive onto the pad.");
            }

            // Home toward the valley pad, re-aiming each tick; the warp fires inside World.Step the
            // moment the tank enters the pad's trigger radius, lifting it to the plateau layer.
            uint seq = 0;
            for (var i = 0; i < 400 && scene.Tanks[driven].Layer != 1; i++)
            {
                var pos = scene.Tanks[driven].Position;
                var dir = System.Numerics.Vector2.Normalize(padA - pos);
                _transport.DeliverInput(new InputFrame(Seq: ++seq, MoveX: dir.X, MoveY: dir.Y,
                    Aim: 0f, Buttons: 0, Slot: driven));
                scene.Tick(0.05f);
            }

            var tank = scene.Tanks[driven];
            var padB = NetCellCentre(22, 18);
            var distance = System.Numerics.Vector2.Distance(tank.Position, padB);
            if (distance > 200f)
            {
                throw new Exception(
                    $"The relayed tank must warp to the plateau pad {padB}; ended {distance:0} away at {tank.Position}.");
            }

            if (tank.Layer != 1)
            {
                throw new Exception($"The warp must lift the tank to the plateau layer; got layer {tank.Layer}.");
            }

            // The warped position rides the normal snapshot — no new wire section for teleports.
            var last = _transport.Broadcast[^1];
            foreach (var state in last.Tanks)
            {
                if (state.Slot == driven && state.Layer != 1)
                {
                    throw new Exception("The broadcast snapshot must carry the warped tank's new layer.");
                }
            }
        }
        finally
        {
            scene.Free();
        }
    }

    // The guest's own tank teleports host-side; the snapshot brings the jump back. Reconcile snaps
    // the prediction to the pad, and the local view-model must follow — including the LAYER, or a
    // cross-layer warp would leave the guest's own tank rendered down in the valley.
    [Test]
    public void GuestSnapshot_LiftsItsOwnTank_ToTheTeleportedLayer()
    {
        _transport.DeliverWelcome(1);
        _transport.DeliverSnapshot(new SnapshotFrame(1, 0,
            new List<TankState> { new(1, 1440f, 1184f, 0f, 0f, 8, 1, Shield: 0, Layer: 1) },
            new List<WallDelta>()));

        var own = _scene.Tanks[1];
        if (Math.Abs(own.Position.X - 1440f) > 0.01f || Math.Abs(own.Position.Y - 1184f) > 0.01f)
        {
            throw new Exception($"The guest's own tank must snap to the authoritative warp; got {own.Position}.");
        }

        if (own.Layer != 1)
        {
            throw new Exception($"The guest's own tank must follow the warp's layer; got {own.Layer}.");
        }
    }
}
