using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Godot;
using TankGame.Domain.Net;
using TankGame.Infrastructure.Net;
using TankGame.Presentation;

namespace TankGame.Tests.Presentation;

/// <summary>One scripted player for <c>scripts/net-e2e.mjs</c>: a real game process that takes the
/// real path (HTTP lobby, WebSocket, room scene buttons, scene hand-off, net arena) against a local
/// worker and reports whether the match actually ran. Debug builds only.</summary>
public partial class NetE2EDriver : Node
{
    private const double TimeoutSeconds = 90d;
    private const double PlaySeconds = 10d;
    private const float MinTravel = 64f; // one tile: enough to prove input moved the tank

    private static readonly Key[] DriveKeys = { Key.W, Key.D, Key.S, Key.A };

    private readonly string _role;
    private readonly string _worker;
    private readonly string _codeFile;
    private readonly string _outFile;
    private readonly int _humans;
    private readonly string? _shotPrefix;
    private readonly string? _map;
    private readonly bool _hostQuits;
    private readonly Dictionary<string, object> _report = new();
    private readonly Dictionary<byte, System.Numerics.Vector2> _firstSeen = new();
    private readonly Dictionary<byte, float> _farthest = new();

    private double _elapsed;
    private double _playing = -1d;
    private LobbyView? _lobby;
    private bool _joining;
    private bool _finished;
    private int _driveKey = -1;
    private bool _ownViewSeen;
    private bool _arrowShot;
    private string? _createdCode;

    private NetE2EDriver(string role, string worker, string codeFile, string outFile, int humans, string? shotPrefix, string? map, bool hostQuits)
    {
        _shotPrefix = shotPrefix;
        _map = map;
        _hostQuits = hostQuits;
        _role = role;
        _worker = worker;
        _codeFile = codeFile;
        _outFile = outFile;
        _humans = humans;
    }

    /// <summary>Starts a driver when the command line asks for one.</summary>
    /// <param name="tree">The running scene tree; the driver joins its root so it outlives scene changes.</param>
    /// <returns>True when a driver took over this process; false for a normal boot.</returns>
    public static bool TryStart(SceneTree tree)
    {
        var args = new Dictionary<string, string>();
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            var split = arg.IndexOf('=', StringComparison.Ordinal);
            if (arg.StartsWith("--net-e2e", StringComparison.Ordinal) && split > 0)
            {
                args[arg[..split]] = arg[(split + 1)..];
            }
        }

        if (!args.TryGetValue("--net-e2e-role", out var role))
        {
            return false;
        }

        var driver = new NetE2EDriver(
            role, args["--net-e2e-worker"], args["--net-e2e-code-file"], args["--net-e2e-out"],
            int.Parse(args["--net-e2e-humans"], CultureInfo.InvariantCulture),
            args.GetValueOrDefault("--net-e2e-shot"), args.GetValueOrDefault("--net-e2e-map"),
            args.ContainsKey("--net-e2e-host-quits")) { Name = "NetE2EDriver" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, driver);
        return true;
    }

    public override void _Process(double delta)
    {
        if (_finished)
        {
            return;
        }

        _elapsed += delta;
        if (_elapsed > TimeoutSeconds)
        {
            Finish(false, $"timed out; lobby phase {_lobby?.Phase.ToString() ?? "none"}, played {_playing:F1}s, scene {GetTree().CurrentScene?.Name}");
            return;
        }

        if (NetworkSession.Active is null)
        {
            Connect();
        }
        else if (GetTree().CurrentScene is LobbyRoomScene room)
        {
            DriveRoom(room);
        }
        else if (GetTree().CurrentScene is NetArena3DScene arena)
        {
            DriveArena(arena, delta);
        }
    }

    private void Connect()
    {
        if (_joining)
        {
            return;
        }

        if (_role == "host")
        {
            _joining = true;
            _ = CreateAndJoin();
        }
        else if (File.Exists(_codeFile) && File.ReadAllText(_codeFile).Trim() is { Length: > 0 } code)
        {
            _joining = true;
            Join(code);
        }
    }

    private async System.Threading.Tasks.Task CreateAndJoin()
    {
        var code = await new HttpLobbyClient(_worker).CreateLobbyAsync();
        if (code is null)
        {
            Finish(false, "could not create a lobby");
            return;
        }

        _createdCode = code;
        NetworkSession.PendingMap = _map;
        Join(code);
    }

    private void Join(string code)
    {
        var socketBase = _worker.Replace("http", "ws", StringComparison.Ordinal);
        NetworkSession.TransportFactory = room => new WebSocketTransport(new GodotWebSocket($"{socketBase}/room/{room}"));
        GameSetup.PlayerName = _role;
        var transport = NetworkSession.Join(code);
        transport.LobbyStateReceived += view => _lobby = view;
        GetTree().ChangeSceneToFile(LobbyBrowserScene.RoomScenePath);
    }

    // Presses the same buttons a player does: guests ready up, the host starts once all are seated.
    private void DriveRoom(LobbyRoomScene room)
    {
        if (_createdCode is not null && _lobby is not null)
        {
            File.WriteAllText(_codeFile, _createdCode); // seated first, so guests can't take slot 0
            _createdCode = null;
        }

        if (_lobby is not { Phase: LobbyPhase.Waiting } lobby || lobby.Players.Count < _humans)
        {
            return;
        }

        if (room.FindChild("Ready", true, false) is Button { Visible: true, ButtonPressed: false } ready)
        {
            ready.ButtonPressed = true;
        }

        if (room.FindChild("Start", true, false) is Button { Visible: true, Disabled: false } start)
        {
            start.EmitSignal(BaseButton.SignalName.Pressed);
        }
    }

    private void DriveArena(NetArena3DScene arena, double delta)
    {
        if (_playing < 0d)
        {
            if (arena.LocalSlot is null || _lobby?.Phase != LobbyPhase.Started)
            {
                return;
            }

            _playing = 0d; // the server flipped to started: from here the match must be running
        }

        _playing += delta;
        var key = (int)(_playing / 1.5d) % DriveKeys.Length;
        if (key != _driveKey)
        {
            if (_driveKey >= 0)
            {
                PressKey(DriveKeys[_driveKey], false);
            }

            _driveKey = key;
            PressKey(DriveKeys[key], true);
        }

        foreach (var (slot, tank) in arena.Tanks)
        {
            if (!_firstSeen.ContainsKey(slot))
            {
                if (tank.Hp > 0)
                {
                    _firstSeen[slot] = tank.Position;
                }

                continue;
            }

            var travel = System.Numerics.Vector2.Distance(_firstSeen[slot], tank.Position);
            _farthest[slot] = Math.Max(_farthest.GetValueOrDefault(slot), travel);
        }

        // Sampled while alive: five bots may legitimately kill this tank before the check ends.
        if (arena.LocalSlot is byte own && arena.Tanks.TryGetValue(own, out var mine) && mine.Hp > 0)
        {
            _ownViewSeen |= arena.FindChild($"Tank3DView{own}", true, false) is Node3D { Visible: true };
        }

        if (!_arrowShot && arena.FindChild("EnemyArrow0", true, false) is Node2D { Visible: true })
        {
            _arrowShot = true;
            SaveScreenshot("arrow");
        }

        // The host's report makes it quit. In that scenario a guest passes only once it has been told,
        // or once the round was already decided (its victory card has its own Leave).
        var hasWayOut = arena.HostLeft || arena.FindChild("GameOverLayer", true, false) is not null;
        if (_playing >= PlaySeconds && (!_hostQuits || _role == "host" || hasWayOut))
        {
            SaveScreenshot("end");
            Judge(arena);
        }
    }

    // Windowed runs only (a headless viewport has no pixels): lets a person look at the real HUD.
    private void SaveScreenshot(string moment)
    {
        if (_shotPrefix is not null)
        {
            GetViewport().GetTexture().GetImage().SavePng($"{_shotPrefix}-{moment}.png");
        }
    }

    private void Judge(NetArena3DScene arena)
    {
        var problems = new List<string>();
        var seats = _lobby?.Players ?? (IReadOnlyList<LobbyPlayer>)Array.Empty<LobbyPlayer>();
        if (_firstSeen.Count < LobbyProtocol.MaxPlayers)
        {
            problems.Add($"saw {_firstSeen.Count}/{LobbyProtocol.MaxPlayers} live tanks");
        }

        foreach (var seat in seats)
        {
            if (_farthest.GetValueOrDefault((byte)seat.Slot) < MinTravel)
            {
                problems.Add($"player tank {seat.Slot} did not move ({_farthest.GetValueOrDefault((byte)seat.Slot):F0})");
            }
        }

        if (!_ownViewSeen)
        {
            problems.Add("own tank view was never visible");
        }

        _report["slot"] = arena.LocalSlot ?? 255;
        _report["tanksSeen"] = _firstSeen.Count;
        Finish(problems.Count == 0, string.Join("; ", problems));
    }

    private static void PressKey(Key key, bool pressed) =>
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Pressed = pressed });

    private void Finish(bool passed, string detail)
    {
        _finished = true;
        _report["role"] = _role;
        _report["passed"] = passed;
        _report["detail"] = detail;
        File.WriteAllText(_outFile, JsonSerializer.Serialize(_report));
        GetTree().Quit(passed ? 0 : 1);
    }
}
