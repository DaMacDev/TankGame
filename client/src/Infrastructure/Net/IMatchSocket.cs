using System.Collections.Generic;

namespace TankGame.Infrastructure.Net;

/// <summary>The byte-level seam beneath <see cref="WebSocketTransport"/>: send one binary frame, and
/// pump-and-drain inbound binary messages. A Godot <c>WebSocketPeer</c> satisfies it in the running
/// game (<see cref="GodotWebSocket"/>); a fake satisfies it in tests, so the transport's encode/decode
/// logic is unit-tested without a live socket.</summary>
public interface IMatchSocket
{
    /// <summary>Sends one binary message. Fire-and-forget.</summary>
    void Send(byte[] data);

    /// <summary>Pumps socket I/O and returns the binary messages received since the previous call,
    /// in arrival order (empty if none).</summary>
    IReadOnlyList<byte[]> Poll();

    /// <summary>Closes the connection.</summary>
    void Close();
}
