using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TankGame.Domain.Net;

/// <summary>Which way the match is scored.</summary>
public enum GameMode
{
    /// <summary>Free-for-all — every tank is its own team.</summary>
    Ffa,

    /// <summary>Two teams.</summary>
    Team,
}

/// <summary>The lobby's lifecycle stage (mirrors the worker's LobbyPhase).</summary>
public enum LobbyPhase
{
    Waiting,
    Countdown,

    /// <summary>Countdown finished; the room waits for every seated player to report its arena is built
    /// (the "loaded" handshake) before the match starts.</summary>
    Loading,

    Started,
}

/// <summary>One seat in the lobby. <paramref name="Loaded"/> is the loading-phase handshake flag —
/// trailing default so existing positional call sites keep compiling.</summary>
public readonly record struct LobbyPlayer(int Slot, string Name, int Team, bool Ready, bool Loaded = false);

/// <summary>The client's view of a lobby — the C# mirror of the worker's <c>LobbyState</c> JSON,
/// pushed over the socket as a <see cref="LobbyProtocol.MsgLobbyState"/> message. Read-only: the
/// server is the authority, the client renders this and sends commands.</summary>
/// <param name="Map">The map id the host picked for the room; "" means random, resolved by the
/// host client at launch. Defaults to "" so a push from a server without the field still parses.</param>
public sealed record LobbyView(
    GameMode Mode,
    LobbyPhase Phase,
    int HostSlot,
    int Countdown,
    IReadOnlyList<LobbyPlayer> Players,
    string Map = "",
    int Seed = 0);

/// <summary>The lobby control protocol — the C# mirror of the worker's JSON lobby channel
/// (<c>codec.ts</c> / <c>lobbyState.ts</c>). The server pushes <see cref="MsgLobbyState"/> snapshots
/// and accepts <see cref="MsgLobbyCmd"/> commands (it stamps the sender's slot, so commands carry no
/// slot). JSON, not the fixed binary game protocol, because the lobby is low-rate and shape-churning.
/// Pure — no Godot — so the parse/encode round-trips are unit-tested without a runtime.</summary>
public static class LobbyProtocol
{
    /// <summary>Seats per room — mirrors the worker's <c>MAX_PLAYERS</c> (owner ask: 8, raised from 4).
    /// The room UI, placeholder cast, and spawn table all size off this.</summary>
    public const int MaxPlayers = 8;

    /// <summary>Leading kind byte of a server→client lobby-state push.</summary>
    public const byte MsgLobbyState = 0x10;

    /// <summary>Leading kind byte of a client→server lobby command.</summary>
    public const byte MsgLobbyCmd = 0x11;

    /// <summary>Parses a <see cref="MsgLobbyState"/> message (tag byte then UTF-8 JSON) into a view.</summary>
    public static LobbyView ParseState(ReadOnlySpan<byte> message)
    {
        using var doc = JsonDocument.Parse(message[1..].ToArray());
        var root = doc.RootElement;

        var players = new List<LobbyPlayer>();
        foreach (var p in root.GetProperty("players").EnumerateArray())
        {
            players.Add(new LobbyPlayer(
                p.GetProperty("slot").GetInt32(),
                p.GetProperty("name").GetString() ?? "Player",
                p.GetProperty("team").GetInt32(),
                p.GetProperty("ready").GetBoolean(),
                p.TryGetProperty("loaded", out var loaded) && loaded.GetBoolean()));
        }

        return new LobbyView(
            ParseMode(root.GetProperty("mode").GetString()),
            ParsePhase(root.GetProperty("phase").GetString()),
            root.GetProperty("hostSlot").GetInt32(),
            root.GetProperty("countdown").GetInt32(),
            players,
            root.TryGetProperty("map", out var map) ? map.GetString() ?? "" : "",
            root.TryGetProperty("seed", out var seed) ? seed.GetInt32() : 0);
    }

    public static byte[] EncodeSetName(string name) => Tag(w =>
    {
        w.WriteString("type", "setName");
        w.WriteString("name", name);
    });

    public static byte[] EncodeSetReady(bool ready) => Tag(w =>
    {
        w.WriteString("type", "setReady");
        w.WriteBoolean("ready", ready);
    });

    public static byte[] EncodeSetTeam(int team) => Tag(w =>
    {
        w.WriteString("type", "setTeam");
        w.WriteNumber("team", team);
    });

    public static byte[] EncodeSetMode(GameMode mode) => Tag(w =>
    {
        w.WriteString("type", "setMode");
        w.WriteString("mode", ModeString(mode));
    });

    public static byte[] EncodeSetMap(string map) => Tag(w =>
    {
        w.WriteString("type", "setMap");
        w.WriteString("map", map);
    });

    public static byte[] EncodeStart() => Tag(w => w.WriteString("type", "start"));

    /// <summary>The loading-phase handshake: this client reports its arena is built and ready.</summary>
    public static byte[] EncodeLoaded() => Tag(w => w.WriteString("type", "loaded"));

    /// <summary>Play again: any player sends a finished match straight into a new countdown with the
    /// same seats, mode and map and a fresh seed. Refused until the host reports the round over.</summary>
    public static byte[] EncodeRematch() => Tag(w => w.WriteString("type", "rematch"));

    /// <summary>Host-only: the host's world decided the round, which unlocks Play again server-side.</summary>
    public static byte[] EncodeRoundOver() => Tag(w => w.WriteString("type", "roundOver"));

    // Hand-write the command JSON with a Utf8JsonWriter (a forward-only writer, no reflection) rather
    // than JsonSerializer over an anonymous type: the trimmed WebAssembly runtime disables
    // reflection-based serialization (throws JsonSerializerIsReflectionDisabled), which killed EVERY
    // lobby command on the web build — SetName, SetMode/Map, and Start — so a web host's room controls
    // (including "Start") silently did nothing. The reader side (ParseState) already uses JsonDocument.
    private static byte[] Tag(Action<Utf8JsonWriter> writeBody)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(MsgLobbyCmd);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writeBody(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static GameMode ParseMode(string? mode) => mode == "team" ? GameMode.Team : GameMode.Ffa;

    private static string ModeString(GameMode mode) => mode == GameMode.Team ? "team" : "ffa";

    private static LobbyPhase ParsePhase(string? phase) => phase switch
    {
        "countdown" => LobbyPhase.Countdown,
        "loading" => LobbyPhase.Loading,
        "started" => LobbyPhase.Started,
        _ => LobbyPhase.Waiting,
    };
}
