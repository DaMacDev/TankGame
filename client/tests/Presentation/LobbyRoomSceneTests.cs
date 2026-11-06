using System;
using System.Collections.Generic;
using Godot;
using Chickensoft.GoDotTest;
using TankGame.Domain.Net;
using TankGame.GameLogic;
using TankGame.Infrastructure;
using TankGame.Presentation;
using NetMode = TankGame.Domain.Net.GameMode;

namespace TankGame.Tests.Presentation;

// Drives the game room (plan Phase 5) over a fake transport pushing authoritative LobbyViews — no
// socket. Seats, placeholders, host-only Start, countdown, and the started hand-off all assert here.
public class LobbyRoomSceneTests : TestClass
{
    private sealed class FakeTransport : IMatchTransport
    {
        public List<byte[]> SentLobby { get; } = new();
        public event Action<byte>? WelcomeReceived;
        public event Action<SnapshotFrame> SnapshotReceived { add { } remove { } }
        public event Action<LobbyView>? LobbyStateReceived;

        public void SendInput(InputFrame input) { }
        public void Poll() { }
        public void SendLobby(byte[] command) => SentLobby.Add(command);
        public bool Closed { get; private set; }
        public void Close() => Closed = true;

        public void RaiseWelcome(byte slot) => WelcomeReceived?.Invoke(slot);
        public void RaiseLobby(LobbyView view) => LobbyStateReceived?.Invoke(view);
    }

    private Func<string, IMatchTransport> _originalTransportFactory = default!;
    private FakeTransport _transport = default!;
    private Control _scene = default!;

    public LobbyRoomSceneTests(Node testScene) : base(testScene) { }

    [Setup]
    public void Setup()
    {
        TranslationLoader.EnsureLoaded();
        _originalTransportFactory = NetworkSession.TransportFactory;
        _transport = new FakeTransport();
        NetworkSession.Reset();
        NetworkSession.TransportFactory = _ => _transport;
        NetworkSession.Join("ABC123"); // the room reads NetworkSession.Active + ActiveCode

        GameSetup.PlayerName = "Ada";
        _scene = GD.Load<PackedScene>("res://src/Presentation/LobbyRoom.tscn").Instantiate<Control>();
        TestScene.AddChild(_scene); // runs _Ready, which builds the seats over the transport
    }

    [Cleanup]
    public void Cleanup()
    {
        if (GodotObject.IsInstanceValid(_scene))
        {
            _scene.QueueFree();
        }

        NetworkSession.TransportFactory = _originalTransportFactory;
        NetworkSession.Reset();
    }

    // The real-phone stall: the socket outlives the room, so the next push after the hand-off ran the
    // freed room's handler. Its throw stopped the arena from hearing "started", so no match ever ran.
    [Test]
    public void AFreedRoom_DoesNotReactToPushesOnTheSocketItHandedOff()
    {
        Seat(slot: 0, View(LobbyPhase.Loading, hostSlot: 0, new LobbyPlayer(0, "Ada", 0, false)));
        _scene.Free();
        var arenaHeard = false;
        _transport.LobbyStateReceived += _ => arenaHeard = true;

        _transport.RaiseLobby(View(LobbyPhase.Started, hostSlot: 0, new LobbyPlayer(0, "Ada", 0, false)));

        if (!arenaHeard)
        {
            throw new Exception("The arena must hear \"started\" after the room is gone.");
        }
    }

    [Test]
    public void Leaving_ClosesTheSocket_SoTheSeatIsFreed()
    {
        NetworkSession.Reset();

        if (!_transport.Closed)
        {
            throw new Exception("Dropping the session must close its socket, or the seat blocks everyone else.");
        }
    }

    [Test]
    public void EmptySeats_ShowTheGrayPlaceholderCast()
    {
        var expected = LobbySeats.PlaceholderNames("ABC123", LobbyProtocol.MaxPlayers);
        for (var seat = 0; seat < LobbyProtocol.MaxPlayers; seat++)
        {
            if (Find($"Seat{seat}") is not Label label)
            {
                throw new Exception($"The room must show a row per seat (missing 'Seat{seat}').");
            }

            if (label.Text != expected[seat])
            {
                throw new Exception(
                    $"Seat {seat} must show its placeholder '{expected[seat]}'; showed '{label.Text}'.");
            }
        }
    }

    [Test]
    public void AJoinedPlayer_ReplacesTheirSeatPlaceholder()
    {
        Seat(slot: 1, View(LobbyPhase.Waiting, hostSlot: 0,
            new LobbyPlayer(0, "Ada", 0, false),
            new LobbyPlayer(1, "Bea", 1, false)));

        var seat1 = Find("Seat1") as Label ?? throw new Exception("Missing 'Seat1'.");
        if (seat1.Text != "Bea")
        {
            throw new Exception($"A joined player's real name must replace the placeholder; showed '{seat1.Text}'.");
        }

        var seat2 = Find("Seat2") as Label ?? throw new Exception("Missing 'Seat2'.");
        var placeholders = LobbySeats.PlaceholderNames("ABC123", LobbyProtocol.MaxPlayers);
        if (seat2.Text != placeholders[2])
        {
            throw new Exception("An un-joined seat must keep its placeholder.");
        }
    }

    [Test]
    public void SeatIcons_MarkHumansVsAiSeats()
    {
        // Seat 1 is a joined human; the rest are empty (AI-filled once started).
        Seat(slot: 1, View(LobbyPhase.Waiting, hostSlot: 0,
            new LobbyPlayer(0, "Ada", 0, false),
            new LobbyPlayer(1, "Bea", 1, false)));

        var human = Find("SeatIcon1") as SeatIcon ?? throw new Exception("Missing 'SeatIcon1'.");
        if (!human.IsHuman)
        {
            throw new Exception("A joined player's seat icon must mark it as human.");
        }

        var ai = Find("SeatIcon3") as SeatIcon ?? throw new Exception("Missing 'SeatIcon3'.");
        if (ai.IsHuman)
        {
            throw new Exception("An empty (AI-filled) seat icon must not be marked human.");
        }
    }

    [Test]
    public void SeatingIntroduces_TheBattleName_Once()
    {
        Seat(slot: 1, View(LobbyPhase.Waiting, hostSlot: 0, new LobbyPlayer(1, "Player", 1, false)));
        Seat(slot: 1, View(LobbyPhase.Waiting, hostSlot: 0, new LobbyPlayer(1, "Ada", 1, false)));

        if (!ContainsCommand(LobbyProtocol.EncodeSetName("Ada")))
        {
            throw new Exception("Seating must introduce the player's battle name to the room.");
        }

        if (CountCommand(LobbyProtocol.EncodeSetName("Ada")) != 1)
        {
            throw new Exception("The introduction must be sent exactly once, not per push.");
        }
    }

    [Test]
    public void TheCreator_AppliesTheirStagedModeAndMapPicks()
    {
        NetworkSession.PendingMode = NetMode.Team;
        NetworkSession.PendingMap = "DesertWar";

        Seat(slot: 0, View(LobbyPhase.Waiting, hostSlot: 0, new LobbyPlayer(0, "Player", 0, false)));

        if (!ContainsCommand(LobbyProtocol.EncodeSetMode(NetMode.Team))
            || !ContainsCommand(LobbyProtocol.EncodeSetMap("DesertWar")))
        {
            throw new Exception("The creator must apply the staged mode/map once seated as host.");
        }

        if (NetworkSession.PendingMode is not null || NetworkSession.PendingMap is not null)
        {
            throw new Exception("The staged picks must clear after they are applied.");
        }
    }

    [Test]
    public void Start_IsHostOnly_AndSendsTheStartCommand()
    {
        Seat(slot: 1, View(LobbyPhase.Waiting, hostSlot: 0,
            new LobbyPlayer(0, "Ada", 0, false),
            new LobbyPlayer(1, "Bea", 1, false)));
        if (Find("Start") is not Button { Visible: false })
        {
            throw new Exception("A guest must not see the Start button.");
        }

        _transport.RaiseLobby(View(LobbyPhase.Waiting, hostSlot: 1,
            new LobbyPlayer(1, "Bea", 1, false)));
        var start = Find("Start") as Button ?? throw new Exception("Missing 'Start'.");
        if (!start.Visible)
        {
            throw new Exception("The host must see the Start button while the room waits.");
        }

        start.EmitSignal(BaseButton.SignalName.Pressed);
        if (!ContainsCommand(LobbyProtocol.EncodeStart()))
        {
            throw new Exception("Pressing Start must send the start command.");
        }
    }

    [Test]
    public void TheCountdown_RendersEachTick()
    {
        Seat(slot: 0, View(LobbyPhase.Countdown, hostSlot: 0, new LobbyPlayer(0, "Ada", 0, true)));

        var countdown = Find("Countdown") as Label ?? throw new Exception("Missing 'Countdown'.");
        if (!countdown.Text.Contains('3'))
        {
            throw new Exception($"The countdown must show the seconds left; showed '{countdown.Text}'.");
        }
    }

    [Test]
    public void Started_SnapshotsTheRosterForThePlayScene()
    {
        var final = View(LobbyPhase.Started, hostSlot: 0,
            new LobbyPlayer(0, "Ada", 0, true),
            new LobbyPlayer(2, "Cara", 2, true));
        Seat(slot: 0, final);

        if (!ReferenceEquals(NetworkSession.StartedLobby, final))
        {
            throw new Exception("Start must snapshot the final roster for the networked play scene.");
        }
    }

    [Test]
    public void TheMapLabel_UsesTheLocalizedBuiltInKey()
    {
        Seat(slot: 0, new LobbyView(NetMode.Ffa, LobbyPhase.Waiting, 0, 0,
            new[] { new LobbyPlayer(0, "Ada", 0, false) }, Map: "DesertWar"));

        var map = Find("MapLabel") as Label ?? throw new Exception("Missing 'MapLabel'.");
        if (map.Text != "map.desert_war")
        {
            throw new Exception($"A built-in map must show its localized name key; showed '{map.Text}'.");
        }
    }

    [Test]
    public void TheButtons_MeetTheTouchTargetBaseline()
    {
        foreach (var name in new[] { "Start", "Leave" })
        {
            if (Find(name) is not Button button || button.CustomMinimumSize.Y < 44f)
            {
                throw new Exception($"'{name}' must be at least 44 px tall for touch (a11y baseline).");
            }
        }
    }

    [Test]
    public void Leave_DropsTheTransport()
    {
        Press("Leave");

        if (NetworkSession.Active is not null)
        {
            throw new Exception("Leaving the room must drop the active transport.");
        }
    }

    [Test]
    public void AReadiedGuest_ShowsATick_AndCanToggleTheirReady()
    {
        // Local client is the guest in slot 1 (host is slot 0), already readied up.
        Seat(slot: 1, View(LobbyPhase.Waiting, hostSlot: 0,
            new LobbyPlayer(0, "Ada", 0, false),
            new LobbyPlayer(1, "Bea", 1, true)));

        var seat1 = Find("Seat1") as Label ?? throw new Exception("Missing 'Seat1'.");
        if (!seat1.Text.Contains('✓'))
        {
            throw new Exception($"A readied guest's seat must show a tick; showed '{seat1.Text}'.");
        }

        var ready = Find("Ready") as Button ?? throw new Exception("Missing 'Ready' toggle.");
        if (!ready.Visible)
        {
            throw new Exception("A seated guest must see the Ready toggle.");
        }

        ready.EmitSignal(BaseButton.SignalName.Toggled, false);
        if (!ContainsCommand(LobbyProtocol.EncodeSetReady(false)))
        {
            throw new Exception("Toggling Ready must send the setReady command.");
        }
    }

    [Test]
    public void TheHost_NeverSeesTheReadyToggle()
    {
        // Seated as the host (slot 0) — the host readies by starting, not by a toggle.
        Seat(slot: 0, View(LobbyPhase.Waiting, hostSlot: 0, new LobbyPlayer(0, "Ada", 0, false)));

        if (Find("Ready") is not Button { Visible: false })
        {
            throw new Exception("The host must not see the Ready toggle.");
        }
    }

    [Test]
    public void Start_StaysDisabled_UntilEveryGuestIsReady()
    {
        // Host (slot 0) with an un-ready guest → Start is visible but locked.
        Seat(slot: 0, View(LobbyPhase.Waiting, hostSlot: 0,
            new LobbyPlayer(0, "Ada", 0, false),
            new LobbyPlayer(1, "Bea", 1, false)));

        var start = Find("Start") as Button ?? throw new Exception("Missing 'Start'.");
        if (!start.Visible || !start.Disabled)
        {
            throw new Exception("Start must be visible but disabled while a guest is not ready.");
        }

        _transport.RaiseLobby(View(LobbyPhase.Waiting, hostSlot: 0,
            new LobbyPlayer(0, "Ada", 0, false),
            new LobbyPlayer(1, "Bea", 1, true)));
        if (start.Disabled)
        {
            throw new Exception("Start must enable once every guest is ready.");
        }
    }

    [Test]
    public void WhenTheHostLeaves_TheRoomAnnouncesTheNewHost()
    {
        // Seated as guest slot 1; host is slot 0 (Ada).
        Seat(slot: 1, View(LobbyPhase.Waiting, hostSlot: 0,
            new LobbyPlayer(0, "Ada", 0, false),
            new LobbyPlayer(1, "Bea", 1, false)));

        // Ada drops → the server migrates the host role to Bea (slot 1).
        _transport.RaiseLobby(View(LobbyPhase.Waiting, hostSlot: 1,
            new LobbyPlayer(1, "Bea", 1, false)));

        var notice = Find("HostNotice") as Label ?? throw new Exception("Missing 'HostNotice'.");
        if (!notice.Text.Contains("Bea"))
        {
            throw new Exception($"A host hand-off must name the new host; showed '{notice.Text}'.");
        }
    }

    // Rematch re-entry: the arena returned here on the same socket with the slot + waiting view
    // carried on NetworkSession — no second welcome ever arrives, so the room must adopt them or the
    // host would never see Start again.
    [Test]
    public void RematchReEntry_AdoptsTheCarriedSlotAndView_SoTheHostSeesStart()
    {
        NetworkSession.LocalSlot = 0;
        NetworkSession.StartedLobby = View(LobbyPhase.Waiting, hostSlot: 0,
            new LobbyPlayer(0, "Ada", 0, false),
            new LobbyPlayer(1, "Bea", 1, false));
        var scene = GD.Load<PackedScene>("res://src/Presentation/LobbyRoom.tscn").Instantiate<Control>();
        TestScene.AddChild(scene);

        try
        {
            if (scene.FindChild("Start", recursive: true, owned: false) is not Button { Visible: true })
            {
                throw new Exception("After a rematch the re-entered host must see the Start button again.");
            }
        }
        finally
        {
            scene.QueueFree();
        }
    }

    private static LobbyView View(LobbyPhase phase, int hostSlot, params LobbyPlayer[] players) =>
        new(NetMode.Ffa, phase, hostSlot, phase == LobbyPhase.Countdown ? 3 : 0, players);

    private void Seat(byte slot, LobbyView view)
    {
        _transport.RaiseWelcome(slot);
        _transport.RaiseLobby(view);
    }

    private bool ContainsCommand(byte[] expected) => CountCommand(expected) > 0;

    private int CountCommand(byte[] expected)
    {
        var count = 0;
        foreach (var sent in _transport.SentLobby)
        {
            if (sent.AsSpan().SequenceEqual(expected))
            {
                count++;
            }
        }

        return count;
    }

    private Node? Find(string name) => _scene.FindChild(name, recursive: true, owned: false);

    private void Press(string buttonName)
    {
        var button = Find(buttonName) as Button ?? throw new Exception($"Missing '{buttonName}' button.");
        button.EmitSignal(BaseButton.SignalName.Pressed);
    }
}
