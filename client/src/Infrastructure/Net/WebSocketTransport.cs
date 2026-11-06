using System;
using TankGame.Domain.Net;

namespace TankGame.Infrastructure.Net;

/// <summary>The client's WebSocket <see cref="IMatchTransport"/> (M3-T6): encodes each
/// <see cref="InputFrame"/> onto an <see cref="IMatchSocket"/> and decodes inbound
/// <see cref="SnapshotFrame"/>s back up as <see cref="SnapshotReceived"/>. All the wire framing is
/// delegated to <see cref="ProtocolCodec"/> (mirrored byte-for-byte by the server), so this class is
/// only the routing between frames and bytes — kept free of Godot so it unit-tests against a fake
/// socket. The concrete engine socket is injected, matching the local <c>IInputSource</c> seam.</summary>
public sealed class WebSocketTransport : IMatchTransport
{
    private readonly IMatchSocket _socket;

    public WebSocketTransport(IMatchSocket socket) => _socket = socket;

    public event Action<byte>? WelcomeReceived;
    public event Action<SnapshotFrame>? SnapshotReceived;
    public event Action<InputFrame>? InputReceived;
    public event Action<LobbyView>? LobbyStateReceived;

    public void SendInput(InputFrame input) => _socket.Send(ProtocolCodec.EncodeInputMessage(input));

    public void SendLobby(byte[] command) => _socket.Send(command);

    public void Close() => _socket.Close();

    public void SendSnapshot(SnapshotFrame snapshot)
    {
        var payload = ProtocolCodec.EncodeSnapshot(snapshot);
        var message = new byte[payload.Length + 1];
        message[0] = ProtocolCodec.MsgSnapshot;
        payload.CopyTo(message, 1);
        _socket.Send(message);
    }

    /// <summary>Pumps the socket and raises the matching event for every server message that arrived
    /// since the last call, dispatching on its leading kind byte (welcome vs snapshot). Drive it from
    /// the game loop (<c>_Process</c>); it is decoupled from <see cref="SendInput"/> so input can be
    /// sent at any time independent of snapshot cadence.</summary>
    public void Poll()
    {
        foreach (var message in _socket.Poll())
        {
            if (message.Length == 0)
            {
                continue;
            }

            switch (message[0])
            {
                case ProtocolCodec.MsgWelcome:
                    WelcomeReceived?.Invoke(ProtocolCodec.DecodeWelcome(message));
                    break;
                case ProtocolCodec.MsgSnapshot:
                    SnapshotReceived?.Invoke(ProtocolCodec.DecodeSnapshot(message.AsSpan(1)));
                    break;
                case ProtocolCodec.MsgInput:
                    InputReceived?.Invoke(ProtocolCodec.DecodeInput(message.AsSpan(1)));
                    break;
                case LobbyProtocol.MsgLobbyState:
                    LobbyStateReceived?.Invoke(LobbyProtocol.ParseState(message));
                    break;
            }
        }
    }
}
