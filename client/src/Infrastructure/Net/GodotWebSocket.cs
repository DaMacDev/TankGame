using System;
using System.Collections.Generic;
using Godot;

namespace TankGame.Infrastructure.Net;

/// <summary><see cref="IMatchSocket"/> backed by Godot's <c>WebSocketPeer</c> — the live socket for
/// the game. Thin wiring over the engine peer: open the connection on construction, <c>PutPacket</c>
/// to send (the peer's default write mode is binary), and on <see cref="Poll"/> pump the peer and
/// drain any complete binary messages while the connection is open. This is untested wiring; the
/// framing it carries is covered by <see cref="WebSocketTransport"/> and <see cref="Domain.Net.ProtocolCodec"/>
/// tests.</summary>
public sealed class GodotWebSocket : IMatchSocket
{
    private readonly WebSocketPeer _peer = new();

    public GodotWebSocket(string url)
    {
        var error = _peer.ConnectToUrl(url);
        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"could not open a websocket to {url}: {error}");
        }
    }

    public void Send(byte[] data) => _peer.PutPacket(data);

    public void Close()
    {
        _peer.Close();
        _peer.Poll(); // nothing pumps this peer after a close; flush the close frame now
    }

    public IReadOnlyList<byte[]> Poll()
    {
        _peer.Poll();
        if (_peer.GetReadyState() != WebSocketPeer.State.Open)
        {
            return Array.Empty<byte[]>();
        }

        var messages = new List<byte[]>();
        while (_peer.GetAvailablePacketCount() > 0)
        {
            messages.Add(_peer.GetPacket());
        }
        return messages;
    }
}
