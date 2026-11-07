using System;
using System.Collections.Generic;
using TankGame.Domain.Net;
using TankGame.Infrastructure.Net;
using Xunit;

namespace TankGame.Tests.Infrastructure;

// Exercises WebSocketTransport's framing against a fake byte socket — no Godot, no live server.
// The encode/decode round-trip itself is guaranteed by ProtocolCodec's tests (and the cross-language
// byte-vector test); here we only prove the transport routes input out and snapshots in correctly.
public class WebSocketTransportTests
{
    private sealed class FakeSocket : IMatchSocket
    {
        public List<byte[]> Sent { get; } = new();
        public Queue<byte[]> Inbound { get; } = new();

        public bool Closed { get; private set; }

        public void Close() => Closed = true;

        public void Send(byte[] data) => Sent.Add(data);

        public IReadOnlyList<byte[]> Poll()
        {
            var batch = new List<byte[]>(Inbound);
            Inbound.Clear();
            return batch;
        }
    }

    [Fact]
    public void Close_ClosesTheSocket_SoTheServerFreesTheSeat()
    {
        var socket = new FakeSocket();

        new WebSocketTransport(socket).Close();

        Assert.True(socket.Closed);
    }

    [Fact]
    public void SendInput_EncodesTheTaggedFrameOntoTheSocket()
    {
        var socket = new FakeSocket();
        var transport = new WebSocketTransport(socket);
        var frame = new InputFrame(7, 0.5f, -0.5f, 1.25f, InputFrame.FireBit);

        transport.SendInput(frame);

        Assert.Single(socket.Sent);
        Assert.Equal(ProtocolCodec.EncodeInputMessage(frame), socket.Sent[0]);
    }

    // ── Host side (ADR-0019 step 3): the host broadcasts snapshots and receives relayed guest inputs ──

    [Fact]
    public void SendSnapshot_EncodesTheTaggedSnapshotOntoTheSocket()
    {
        var socket = new FakeSocket();
        var transport = new WebSocketTransport(socket);
        var frame = new SnapshotFrame(3, 2,
            new List<TankState> { new(0, 64f, 128f, 0f, 0.5f, 8, 0) }, new List<WallDelta>());

        transport.SendSnapshot(frame);

        Assert.Single(socket.Sent);
        Assert.Equal(SnapshotMessage(frame), socket.Sent[0]);
    }

    [Fact]
    public void Poll_DispatchesARelayedGuestInput_ToInputReceived()
    {
        var socket = new FakeSocket();
        var transport = new WebSocketTransport(socket);
        var frame = new InputFrame(9, 1f, 0f, 0.25f, 0);
        InputFrame? received = null;
        transport.InputReceived += f => received = f;
        socket.Inbound.Enqueue(ProtocolCodec.EncodeInputMessage(frame));

        transport.Poll();

        Assert.Equal(frame, received);
    }

    // Server→client messages carry a leading kind tag; the transport strips it and dispatches.
    private static byte[] SnapshotMessage(SnapshotFrame frame)
    {
        var payload = ProtocolCodec.EncodeSnapshot(frame);
        var message = new byte[payload.Length + 1];
        message[0] = ProtocolCodec.MsgSnapshot;
        Array.Copy(payload, 0, message, 1, payload.Length);
        return message;
    }

    [Fact]
    public void Poll_DecodesEachInboundSnapshotMessageAndRaisesSnapshotReceived()
    {
        var socket = new FakeSocket();
        var transport = new WebSocketTransport(socket);
        var received = new List<SnapshotFrame>();
        transport.SnapshotReceived += received.Add;

        var first = new SnapshotFrame(
            1, 7, new[] { new TankState(0, 10f, 20f, 0.1f, 0.2f, 100, 0) }, Array.Empty<WallDelta>());
        var second = new SnapshotFrame(
            2, 8, new[] { new TankState(1, 30f, 40f, 0.3f, 0.4f, 80, 1) },
            new[] { new WallDelta(3, 4, 1, 2) });
        socket.Inbound.Enqueue(SnapshotMessage(first));
        socket.Inbound.Enqueue(SnapshotMessage(second));

        transport.Poll();

        Assert.Equal(2, received.Count);
        Assert.Equal(1u, received[0].Tick);
        Assert.Equal(7u, received[0].AckSeq);
        Assert.Equal(2u, received[1].Tick);
        Assert.Equal(3, received[1].WallDeltas[0].CellX);
    }

    [Fact]
    public void Poll_RaisesWelcomeReceived_WithTheAssignedSlot()
    {
        var socket = new FakeSocket();
        var transport = new WebSocketTransport(socket);
        byte? welcomedSlot = null;
        transport.WelcomeReceived += slot => welcomedSlot = slot;
        socket.Inbound.Enqueue(ProtocolCodec.EncodeWelcome(1));

        transport.Poll();

        Assert.Equal((byte)1, welcomedSlot);
    }

    [Fact]
    public void SendLobby_PutsTheTaggedCommandOnTheSocket()
    {
        var socket = new FakeSocket();
        var transport = new WebSocketTransport(socket);
        var command = LobbyProtocol.EncodeSetReady(true);

        transport.SendLobby(command);

        Assert.Single(socket.Sent);
        Assert.Equal(command, socket.Sent[0]);
    }

    [Fact]
    public void Poll_DispatchesALobbyStatePush_ToLobbyStateReceived()
    {
        var socket = new FakeSocket();
        var transport = new WebSocketTransport(socket);
        LobbyView? received = null;
        transport.LobbyStateReceived += v => received = v;

        var json = "{\"mode\":\"team\",\"phase\":\"waiting\",\"hostSlot\":0,\"countdown\":0," +
            "\"players\":[{\"slot\":0,\"name\":\"Ada\",\"team\":0,\"ready\":true}]}";
        var payload = System.Text.Encoding.UTF8.GetBytes(json);
        var message = new byte[payload.Length + 1];
        message[0] = LobbyProtocol.MsgLobbyState;
        Array.Copy(payload, 0, message, 1, payload.Length);
        socket.Inbound.Enqueue(message);

        transport.Poll();

        Assert.NotNull(received);
        Assert.Equal(GameMode.Team, received!.Mode);
        Assert.Single(received.Players);
        Assert.True(received.Players[0].Ready);
    }

    [Fact]
    public void Poll_WithNoInboundMessages_RaisesNothing()
    {
        var socket = new FakeSocket();
        var transport = new WebSocketTransport(socket);
        var count = 0;
        transport.SnapshotReceived += _ => count++;

        transport.Poll();

        Assert.Equal(0, count);
    }
}
