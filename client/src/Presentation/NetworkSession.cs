using System;
using Godot;
using TankGame.Domain.Net;
using TankGame.Infrastructure.Net;
using NetMode = TankGame.Domain.Net.GameMode; // Presentation has its own local-play GameMode enum

namespace TankGame.Presentation;

/// <summary>The networked-match entry point behind the lobby screen (ADR-0019 step 2): the lobby
/// directory mints/validates the shareable codes, the transport carries the match. Both are built
/// through swappable factories so the Host/Join click paths are testable without HTTP or a socket;
/// the defaults talk to the deployed Worker.</summary>
public static class NetworkSession
{
    private const string WorkerHost = "tankgame-worker.vodkadav.workers.dev";

    /// <summary>Builds the lobby directory client. Swap in tests; the default hits the live Worker's
    /// lobby routes. Web gets the Godot-HttpRequest client — .NET's HttpClient NREs on the
    /// multi-threaded WASM runtime (see <see cref="GodotHttpLobbyClient"/>).</summary>
    public static Func<ILobbyClient> LobbyFactory { get; set; } =
        () => OS.HasFeature("web")
            ? new GodotHttpLobbyClient { BaseUrl = $"https://{WorkerHost}" }
            : new HttpLobbyClient($"https://{WorkerHost}");

    /// <summary>Builds the transport for a lobby code. Swap in tests to assert the click path without
    /// opening a socket; the default connects a live WebSocket to <c>/room/{code}</c>.</summary>
    public static Func<string, IMatchTransport> TransportFactory { get; set; } =
        code => new WebSocketTransport(new GodotWebSocket($"wss://{WorkerHost}/room/{code}"));

    /// <summary>The transport for the joined match, or null before a join. The networked play scene
    /// reads this.</summary>
    public static IMatchTransport? Active { get; private set; }

    /// <summary>The lobby code <see cref="Active"/> is connected to ("" before a join) — the room
    /// scene seeds its placeholder cast from it so every member sees the same names.</summary>
    public static string ActiveCode { get; private set; } = "";

    /// <summary>The mode the creator picked in the browser's create panel, applied by the room
    /// scene once the server seats it as host, then cleared. Null when joining someone else's room.</summary>
    public static NetMode? PendingMode { get; set; }

    /// <summary>The map id the creator picked ("" = random); same lifecycle as <see cref="PendingMode"/>.</summary>
    public static string? PendingMap { get; set; }

    /// <summary>The lobby's final roster, snapshotted by the room scene the moment the server said
    /// "started" — the networked play scene reads slots/names/teams/mode/map from it.</summary>
    public static LobbyView? StartedLobby { get; set; }

    /// <summary>This client's own slot, learned from the welcome during the lobby and carried across
    /// the scene change: the welcome is a one-shot on connect, so the play scene — which re-subscribes
    /// after the handoff — would otherwise never learn its slot and never initialize.</summary>
    public static byte? LocalSlot { get; set; }

    /// <summary>Joins <paramref name="code"/> by building (and, for the live factory, connecting) its
    /// transport, and records it as <see cref="Active"/>.</summary>
    public static IMatchTransport Join(string code)
    {
        ActiveCode = code;
        return Active = TransportFactory(code);
    }

    /// <summary>Closes and drops the active transport and every per-match leftover — leaving a match, or a
    /// test resetting the seam.</summary>
    public static void Reset()
    {
        Active?.Close();
        Active = null;
        ActiveCode = "";
        PendingMode = null;
        PendingMap = null;
        StartedLobby = null;
        LocalSlot = null;
    }
}
