using System.Collections.Generic;
using System.Globalization;
using Godot;
using TankGame.Domain.Net;
using TankGame.GameLogic;
using NetMode = TankGame.Domain.Net.GameMode; // Presentation has its own local-play GameMode enum

namespace TankGame.Presentation;

/// <summary>The game room (multiplayer plan Phase 5): four seat rows driven by the server's
/// authoritative <see cref="LobbyView"/> pushes via <see cref="LobbyController"/>. A joined player
/// shows their real name; an empty seat shows a gray placeholder from the derpy cast — the same
/// name the AI tank filling that seat will carry once the match starts. The host (creator) sees a
/// Start button; everyone sees the mode, the map, and the countdown. When the server flips to
/// "started" the roster is snapshotted for the networked play scene and the room hands off.</summary>
public partial class LobbyRoomScene : Control
{
    public const string NetArenaScenePath = "res://src/Presentation/Arena/NetArena3D.tscn";
    public const string BrowserScenePath = "res://src/Presentation/LobbyBrowser.tscn";

    private static readonly Color PlaceholderGray = new(0.55f, 0.55f, 0.55f);
    private static readonly Color JoinedWhite = new(1f, 1f, 1f);

    private IMatchTransport _transport = null!;
    private LobbyController _controller = null!;
    private readonly Label[] _seatNames = new Label[LobbyProtocol.MaxPlayers];
    private readonly SeatIcon[] _seatIcons = new SeatIcon[LobbyProtocol.MaxPlayers];
    private IReadOnlyList<string> _placeholders = System.Array.Empty<string>();
    private Label _modeLabel = null!;
    private Label _mapLabel = null!;
    private Label _countdown = null!;
    private Label _hostNotice = null!;
    private Button _ready = null!;
    private Button _start = null!;
    private bool _introduced; // name + the creator's mode/map picks sent once, on first seating
    private int? _lastHostSlot; // to notice when the server hands the host role to another seat

    public override void _Ready()
    {
        if (NetworkSession.Active is not { } transport)
        {
            Go(BrowserScenePath); // deep-linked here without a connection — nothing to show
            return;
        }

        Theme = MenuStyle.Shared;
        MenuStyle.AddBackdrop(this);

        _transport = transport;
        _placeholders = LobbySeats.PlaceholderNames(NetworkSession.ActiveCode, LobbyProtocol.MaxPlayers);
        _controller = new LobbyController(_transport);
        _controller.Changed += OnLobbyChanged;

        var menu = new VBoxContainer { Name = "Menu" };
        menu.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        menu.GrowHorizontal = GrowDirection.Both;
        menu.GrowVertical = GrowDirection.Both;
        menu.AddThemeConstantOverride("separation", 12);

        var heading = new Label
        {
            Name = "Heading",
            Text = "room.heading",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        heading.AddThemeFontSizeOverride("font_size", 34);
        menu.AddChild(heading);

        // Transient line that announces a host hand-off (the previous host left the room).
        _hostNotice = new Label
        {
            Name = "HostNotice",
            Text = string.Empty,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        menu.AddChild(_hostNotice);

        // Each seat is an icon (human vs AI) + the name, so it's clear at a glance who is a real
        // player and who is a computer-controlled tank.
        for (var seat = 0; seat < _seatNames.Length; seat++)
        {
            var row = new HBoxContainer { Name = $"SeatRow{seat}", Alignment = BoxContainer.AlignmentMode.Center };
            row.AddThemeConstantOverride("separation", 8);

            _seatIcons[seat] = new SeatIcon { Name = $"SeatIcon{seat}" };
            row.AddChild(_seatIcons[seat]);

            // The name Label keeps the "Seat{seat}" node name so it stays the seat's addressable label.
            _seatNames[seat] = new Label
            {
                Name = $"Seat{seat}",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.AddChild(_seatNames[seat]);
            menu.AddChild(row);
        }

        _modeLabel = new Label { Name = "ModeLabel", HorizontalAlignment = HorizontalAlignment.Center };
        menu.AddChild(_modeLabel);
        _mapLabel = new Label { Name = "MapLabel", HorizontalAlignment = HorizontalAlignment.Center };
        menu.AddChild(_mapLabel);

        _countdown = new Label
        {
            Name = "Countdown",
            Text = string.Empty,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        menu.AddChild(_countdown);

        // Guests ready up; the host readies by starting, so the host never sees this toggle.
        _ready = new Button
        {
            Name = "Ready",
            Text = "room.ready",
            ToggleMode = true,
            Visible = false,
            CustomMinimumSize = new Vector2(0f, 48f),
        };
        _ready.Toggled += on => _controller.SetReady(on);
        menu.AddChild(_ready);

        _start = Button("Start", "room.start");
        _start.Visible = false; // revealed once the server seats this client as host
        _start.Pressed += () => _controller.Start();
        menu.AddChild(_start);

        var leave = Button("Leave", "room.leave");
        leave.Pressed += OnLeavePressed;
        menu.AddChild(leave);

        AddChild(menu);
        MenuStyle.AttachHoverRecursive(this);

        // Rematch re-entry: the arena returned here on the same socket, so no second welcome will
        // ever arrive — adopt the carried slot and the waiting or countdown view the arena captured.
        if (NetworkSession.LocalSlot is byte carried)
        {
            _controller.Adopt(carried,
                NetworkSession.StartedLobby is { Phase: LobbyPhase.Waiting or LobbyPhase.Countdown } carriedView
                    ? carriedView
                    : null);
        }

        Render();
    }

    public override void _Process(double delta) => _transport?.Poll();

    // The socket outlives this scene: left subscribed, the next lobby push runs OnLobbyChanged on a
    // freed node, and that throw also stops the arena from ever hearing "started".
    public override void _ExitTree() => _controller?.Dispose();

    private void OnLobbyChanged()
    {
        // First seating: tell the room our battle name, and — if we created it — apply the mode and
        // map picked in the create panel. Sent once; the server remains the authority thereafter.
        if (!_introduced && _controller.LocalSlot is not null && _controller.State is not null)
        {
            _introduced = true;
            _controller.SetName(GameSetup.PlayerName.Length > 0 ? GameSetup.PlayerName : "Player");
            if (_controller.IsHost)
            {
                if (NetworkSession.PendingMode is { } mode)
                {
                    _controller.SetMode(mode);
                }

                if (NetworkSession.PendingMap is { } map && map.Length > 0)
                {
                    _controller.SetMap(map);
                }

                NetworkSession.PendingMode = null;
                NetworkSession.PendingMap = null;
            }
        }

        if (_controller.State is { } view)
        {
            if (_lastHostSlot is { } prev && prev != view.HostSlot && !_controller.HasStarted)
            {
                AnnounceHostChange(view);
            }

            _lastHostSlot = view.HostSlot;
        }

        // Hand off as soon as the room enters loading (not started): each client leaves the room here
        // to build its arena, reports "loaded" when ready, and the arena holds on a loading banner
        // until the server sees every seated player loaded and flips to started. (HasStarted is kept
        // for a client that somehow only observes the started push — e.g. a dropped loading frame.)
        if (_controller.IsLoading || _controller.HasStarted)
        {
            // Snapshot the final roster (slots, names, teams, mode, map, seed) and our own slot for the
            // play scene — the welcome that carried the slot is a one-shot that already fired here.
            NetworkSession.StartedLobby = _controller.State;
            NetworkSession.LocalSlot = _controller.LocalSlot;
            Go(NetArenaScenePath);
            return;
        }

        Render();
    }

    /// <summary>Renders the seats and status from the latest lobby view. Public so a test can force
    /// a render without frame timing.</summary>
    public void Render()
    {
        var view = _controller.State;
        for (var seat = 0; seat < _seatNames.Length; seat++)
        {
            var player = PlayerAt(view, seat);
            var label = _seatNames[seat];
            if (player is { } p)
            {
                // Host wears the star; a readied-up guest wears a tick.
                var badge = p.Slot == view!.HostSlot ? " ★" : p.Ready ? " ✓" : string.Empty;
                label.Text = p.Name + badge;
                label.AddThemeColorOverride("font_color", JoinedWhite);
                _seatIcons[seat].IsHuman = true;
            }
            else
            {
                label.Text = _placeholders.Count > seat ? _placeholders[seat] : string.Empty;
                label.AddThemeColorOverride("font_color", PlaceholderGray);
                _seatIcons[seat].IsHuman = false;
            }
        }

        _modeLabel.Text = view?.Mode == NetMode.Team ? "browser.mode_team" : "browser.mode_ffa";
        _mapLabel.Text = LobbyBrowserScene.MapLabel(view?.Map ?? "");
        _countdown.Text = view?.Phase == LobbyPhase.Countdown
            ? string.Format(CultureInfo.InvariantCulture, Tr("room.starting"), view.Countdown)
            : string.Empty;

        var waiting = view?.Phase == LobbyPhase.Waiting;
        var local = _controller.LocalPlayer;
        _ready.Visible = local is { } lp && lp.Slot != view!.HostSlot && waiting;
        if (local is { } l)
        {
            _ready.SetPressedNoSignal(l.Ready); // reflect the server's truth without echoing a command
        }

        _start.Visible = _controller.IsHost && waiting;
        _start.Disabled = !AllGuestsReady(view); // the host can't launch until every guest is ready
    }

    // Empty seats are AI-filled at launch, so they never gate the start; only seated guests must ready
    // up (the host's readiness is pressing Start). A lone host has no guests, so this is vacuously true.
    private static bool AllGuestsReady(LobbyView? view)
    {
        if (view is null)
        {
            return false;
        }

        foreach (var player in view.Players)
        {
            if (player.Slot != view.HostSlot && !player.Ready)
            {
                return false;
            }
        }

        return true;
    }

    private void AnnounceHostChange(LobbyView view)
    {
        var host = PlayerAt(view, view.HostSlot);
        if (host is not { } h)
        {
            return;
        }

        _hostNotice.Text = string.Format(CultureInfo.InvariantCulture, Tr("room.host_left"), h.Name);
        var timer = GetTree().CreateTimer(4.0); // clear the banner after a few seconds
        timer.Timeout += () =>
        {
            if (IsInstanceValid(_hostNotice))
            {
                _hostNotice.Text = string.Empty;
            }
        };
    }

    private static LobbyPlayer? PlayerAt(LobbyView? view, int seat)
    {
        if (view is null)
        {
            return null;
        }

        foreach (var player in view.Players)
        {
            if (player.Slot == seat)
            {
                return player;
            }
        }

        return null;
    }

    private void OnLeavePressed()
    {
        NetworkSession.Reset(); // drops the transport; the room's close handler frees the seat
        Go(BrowserScenePath);
    }

    // Guarded so the GoDotTest click-path (which adds the room as a child, not the active scene)
    // can assert the wiring without the runner swapping its whole scene out from under it.
    private void Go(string scenePath)
    {
        if (GetTree().CurrentScene == this)
        {
            GetTree().ChangeSceneToFile(scenePath);
        }
    }

    // ≥44 px tall (a11y touch-target baseline) — the arcade is played on phones and iPads.
    private static Button Button(string name, string textKey) =>
        new() { Name = name, Text = textKey, CustomMinimumSize = new Vector2(0f, 48f) };
}
