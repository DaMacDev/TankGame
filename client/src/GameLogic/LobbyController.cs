using System;
using System.Linq;
using TankGame.Domain.Net;

namespace TankGame.GameLogic;

/// <summary>The client's lobby brain (multiplayer milestone): it sits over an
/// <see cref="IMatchTransport"/>, mirrors the server's authoritative <see cref="LobbyView"/> pushes,
/// remembers which slot the welcome assigned this client, and turns UI intents into tagged lobby
/// commands. The lobby UI binds to <see cref="Changed"/> and reads <see cref="State"/> /
/// <see cref="LocalPlayer"/> / <see cref="IsHost"/>; it never parses the wire itself. Pure C# (no
/// Godot) so the whole flow tests against a fake transport.</summary>
public sealed class LobbyController : IDisposable
{
    private readonly IMatchTransport _transport;

    public LobbyController(IMatchTransport transport)
    {
        _transport = transport;
        transport.WelcomeReceived += OnWelcome;
        transport.LobbyStateReceived += OnLobbyState;
    }

    /// <summary>Stops listening to the transport. The socket outlives the room scene that owns this
    /// controller, so a leftover subscription would keep pushing lobby states into a freed scene.</summary>
    public void Dispose()
    {
        _transport.WelcomeReceived -= OnWelcome;
        _transport.LobbyStateReceived -= OnLobbyState;
    }

    /// <summary>The latest authoritative lobby snapshot, or null before the first push.</summary>
    public LobbyView? State { get; private set; }

    /// <summary>The slot the server assigned this client, or null before the welcome.</summary>
    public byte? LocalSlot { get; private set; }

    /// <summary>Raised whenever the welcome or a lobby push changes what the UI should show.</summary>
    public event Action? Changed;

    /// <summary>This client's own seat in the current lobby, or null if not yet seated.</summary>
    public LobbyPlayer? LocalPlayer =>
        State is { } s && LocalSlot is { } slot
            ? s.Players.Cast<LobbyPlayer?>().FirstOrDefault(p => p!.Value.Slot == slot)
            : null;

    /// <summary>Whether this client is the lobby host (only the host may change the mode).</summary>
    public bool IsHost => State is { } s && LocalSlot == s.HostSlot;

    /// <summary>Whether the match is counting down to launch.</summary>
    public bool IsCountingDown => State?.Phase == LobbyPhase.Countdown;

    /// <summary>True while the room waits for every seated player's arena to finish building — the UI's
    /// cue to build its arena and then call <see cref="SendLoaded"/>.</summary>
    public bool IsLoading => State?.Phase == LobbyPhase.Loading;

    /// <summary>The match seed the server stamped at launch, or null before the first push — feeds the
    /// spawn shuffle and AI seeding so every client agrees.</summary>
    public int? Seed => State?.Seed;

    /// <summary>True once the server has flipped the lobby to "started" — the UI's cue to hand off to
    /// the networked arena with the final roster + mode.</summary>
    public bool HasStarted => State?.Phase == LobbyPhase.Started;

    public void SetName(string name) => _transport.SendLobby(LobbyProtocol.EncodeSetName(name));

    public void SetReady(bool ready) => _transport.SendLobby(LobbyProtocol.EncodeSetReady(ready));

    public void SetTeam(int team) => _transport.SendLobby(LobbyProtocol.EncodeSetTeam(team));

    public void SetMode(GameMode mode) => _transport.SendLobby(LobbyProtocol.EncodeSetMode(mode));

    public void SetMap(string map) => _transport.SendLobby(LobbyProtocol.EncodeSetMap(map));

    public void Start() => _transport.SendLobby(LobbyProtocol.EncodeStart());

    /// <summary>Reports this client's arena is built during the loading phase.</summary>
    public void SendLoaded() => _transport.SendLobby(LobbyProtocol.EncodeLoaded());

    /// <summary>Rematch re-entry: the welcome is a one-shot fired on connect, so a room scene rebuilt
    /// on the same socket after a match adopts the carried slot — and, when given, the waiting view
    /// the arena captured — instead of waiting for a second welcome that never comes.</summary>
    public void Adopt(byte slot, LobbyView? state)
    {
        LocalSlot = slot;
        State = state ?? State;
        Changed?.Invoke();
    }

    private void OnWelcome(byte slot)
    {
        LocalSlot = slot;
        Changed?.Invoke();
    }

    private void OnLobbyState(LobbyView view)
    {
        State = view;
        Changed?.Invoke();
    }
}
