using System;
using System.Collections.Generic;
using TankGame.Domain.Net;
using TankGame.GameLogic;
using Xunit;

namespace TankGame.Tests.GameLogic;

public class LobbyControllerTests
{
    // A fake transport that lets a test raise the server pushes and records the commands sent up.
    private sealed class FakeTransport : IMatchTransport
    {
        public List<byte[]> Sent { get; } = new();
        public event Action<byte>? WelcomeReceived;
        public event Action<SnapshotFrame>? SnapshotReceived;
        public event Action<LobbyView>? LobbyStateReceived;

        public void SendInput(InputFrame input) { }
        public void Poll() { }
        public void SendLobby(byte[] command) => Sent.Add(command);

        public void RaiseWelcome(byte slot) => WelcomeReceived?.Invoke(slot);
        public void RaiseLobby(LobbyView view) => LobbyStateReceived?.Invoke(view);
        public void RaiseSnapshot(SnapshotFrame frame) => SnapshotReceived?.Invoke(frame);
    }

    private static LobbyView View(LobbyPhase phase, int hostSlot, params LobbyPlayer[] players) =>
        new(GameMode.Ffa, phase, hostSlot, phase == LobbyPhase.Countdown ? 3 : 0, players);

    [Fact]
    public void Dispose_StopsReactingToTheSocketThatOutlivesTheRoom()
    {
        var transport = new FakeTransport();
        var controller = new LobbyController(transport);
        var changes = 0;
        controller.Changed += () => changes++;

        controller.Dispose();
        transport.RaiseLobby(View(LobbyPhase.Started, 0, new LobbyPlayer(0, "Ada", 0, false)));

        Assert.Equal(0, changes);
        Assert.Null(controller.State);
    }

    [Fact]
    public void Welcome_RecordsTheLocalSlotAndNotifies()
    {
        var transport = new FakeTransport();
        var controller = new LobbyController(transport);
        var changes = 0;
        controller.Changed += () => changes++;

        transport.RaiseWelcome(2);

        Assert.Equal((byte)2, controller.LocalSlot);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void LobbyPush_MirrorsStateAndResolvesTheLocalPlayer()
    {
        var transport = new FakeTransport();
        var controller = new LobbyController(transport);
        transport.RaiseWelcome(1);

        transport.RaiseLobby(View(LobbyPhase.Waiting, hostSlot: 0,
            new LobbyPlayer(0, "Ada", 0, false),
            new LobbyPlayer(1, "Bea", 1, true)));

        Assert.NotNull(controller.State);
        Assert.Equal(new LobbyPlayer(1, "Bea", 1, true), controller.LocalPlayer);
        Assert.False(controller.IsHost); // local slot 1, host slot 0
    }

    [Fact]
    public void IsHost_WhenLocalSlotMatchesTheHostSlot()
    {
        var transport = new FakeTransport();
        var controller = new LobbyController(transport);
        transport.RaiseWelcome(0);
        transport.RaiseLobby(View(LobbyPhase.Waiting, hostSlot: 0, new LobbyPlayer(0, "Ada", 0, false)));

        Assert.True(controller.IsHost);
    }

    [Fact]
    public void SetReady_SendsTheTaggedReadyCommand()
    {
        var transport = new FakeTransport();
        var controller = new LobbyController(transport);

        controller.SetReady(true);

        Assert.Single(transport.Sent);
        Assert.Equal(LobbyProtocol.EncodeSetReady(true), transport.Sent[0]);
    }

    [Fact]
    public void SetMap_SendsTheTaggedMapCommand()
    {
        var transport = new FakeTransport();
        var controller = new LobbyController(transport);

        controller.SetMap("CliffsAndValleys");

        Assert.Equal(LobbyProtocol.EncodeSetMap("CliffsAndValleys"), Assert.Single(transport.Sent));
    }

    [Fact]
    public void Start_SendsTheStartCommand()
    {
        var transport = new FakeTransport();
        var controller = new LobbyController(transport);

        controller.Start();

        Assert.Equal(LobbyProtocol.EncodeStart(), Assert.Single(transport.Sent));
    }

    // Rematch re-entry: the welcome is a one-shot fired on connect, so a room scene rebuilt after a
    // match must adopt the carried slot (and the waiting view captured at the hand-off) instead.
    [Fact]
    public void Adopt_SeedsTheSlotAndState_WithoutAWelcome()
    {
        var transport = new FakeTransport();
        var controller = new LobbyController(transport);
        var changes = 0;
        controller.Changed += () => changes++;
        var view = View(LobbyPhase.Waiting, hostSlot: 1, new LobbyPlayer(1, "Bea", 1, false));

        controller.Adopt(1, view);

        Assert.Equal((byte)1, controller.LocalSlot);
        Assert.Same(view, controller.State);
        Assert.True(controller.IsHost);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Adopt_WithoutAView_KeepsTheExistingState()
    {
        var transport = new FakeTransport();
        var controller = new LobbyController(transport);
        var pushed = View(LobbyPhase.Waiting, hostSlot: 0, new LobbyPlayer(0, "Ada", 0, false));
        transport.RaiseLobby(pushed);

        controller.Adopt(0, null);

        Assert.Same(pushed, controller.State);
        Assert.Equal((byte)0, controller.LocalSlot);
    }

    [Fact]
    public void HasStarted_TracksThePhase()
    {
        var transport = new FakeTransport();
        var controller = new LobbyController(transport);

        transport.RaiseLobby(View(LobbyPhase.Countdown, 0, new LobbyPlayer(0, "Ada", 0, true)));
        Assert.True(controller.IsCountingDown);
        Assert.False(controller.HasStarted);

        transport.RaiseLobby(View(LobbyPhase.Started, 0, new LobbyPlayer(0, "Ada", 0, true)));
        Assert.True(controller.HasStarted);
    }
}
