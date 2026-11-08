using System;
using System.Collections.Generic;
using TankGame.Domain.Net;
using Xunit;

namespace TankGame.Tests.Domain;

public class ProtocolCodecTests
{
    [Fact]
    public void InputFrame_RoundTrips()
    {
        // Slot rides the frame so the host can attribute a relayed input among up to 4 players.
        var original = new InputFrame(Seq: 42, MoveX: 0.25f, MoveY: -0.5f, Aim: 1.5f,
            Buttons: InputFrame.FireBit, Slot: 3);

        var decoded = ProtocolCodec.DecodeInput(ProtocolCodec.EncodeInput(original));

        Assert.Equal(original, decoded);
        Assert.True(decoded.Fire);
        Assert.Equal(3, decoded.Slot);
    }

    [Fact]
    public void SnapshotFrame_RoundTrips_WithTanksAndWallDeltas()
    {
        var original = new SnapshotFrame(
            Tick: 100,
            Acks: new List<InputAck> { new(1, 41) },
            Tanks: new List<TankState>
            {
                new(Slot: 0, X: 64f, Y: 128f, Rotation: 0f, TurretRotation: 0.5f, Hp: 3, Team: 0, Shield: 4, Layer: 1),
                new(Slot: 1, X: 200f, Y: 96f, Rotation: 3.14f, TurretRotation: -1f, Hp: 2, Team: 1, Shield: 0, Layer: 0),
            },
            WallDeltas: new List<WallDelta>
            {
                new(CellX: 5, CellY: 6, Material: 1, Hp: 2),
                new(CellX: 12, CellY: 0, Material: 0, Hp: 0),
            },
            Projectiles: new List<ProjectileState>());

        var decoded = ProtocolCodec.DecodeSnapshot(ProtocolCodec.EncodeSnapshot(original));

        Assert.Equal(original.Tick, decoded.Tick);
        Assert.Equal(original.AckSeq, decoded.AckSeq);
        Assert.Equal(original.Tanks, decoded.Tanks);
        Assert.Equal(original.WallDeltas, decoded.WallDeltas);
    }

    [Fact]
    public void EmptySnapshot_RoundTrips()
    {
        var original = new SnapshotFrame(7, 7, new List<TankState>(), new List<WallDelta>());

        var decoded = ProtocolCodec.DecodeSnapshot(ProtocolCodec.EncodeSnapshot(original));

        Assert.Equal(7u, decoded.Tick);
        Assert.Empty(decoded.Tanks);
        Assert.Empty(decoded.WallDeltas);
        Assert.Empty(decoded.Projectiles);
    }

    [Fact]
    public void SnapshotFrame_RoundTrips_WithProjectiles()
    {
        var original = new SnapshotFrame(
            Tick: 9,
            Acks: new List<InputAck> { new(1, 3) },
            Tanks: new List<TankState>(),
            WallDeltas: new List<WallDelta>(),
            Projectiles: new List<ProjectileState>
            {
                new(X: 320f, Y: 64f, Rotation: 1.25f, Style: 0, Layer: 0),
                new(X: 96.5f, Y: 200f, Rotation: -2.5f, Style: 1, Layer: 2),
            });

        var decoded = ProtocolCodec.DecodeSnapshot(ProtocolCodec.EncodeSnapshot(original));

        Assert.Equal(original.Projectiles, decoded.Projectiles);
    }

    [Fact]
    public void Snapshot_CarriesOneAckPerGuestSlot_AndAckForFindsEachOwn()
    {
        // The snapshot is a broadcast: with 4 players every predicting guest must find its OWN
        // reconciliation anchor in it, not share a single 2-player-era ack.
        var original = new SnapshotFrame(
            Tick: 5,
            Acks: new List<InputAck> { new(1, 10), new(2, 20), new(3, 30) },
            Tanks: new List<TankState>(),
            WallDeltas: new List<WallDelta>(),
            Projectiles: new List<ProjectileState>());

        var decoded = ProtocolCodec.DecodeSnapshot(ProtocolCodec.EncodeSnapshot(original));

        Assert.Equal(original.Acks, decoded.Acks);
        Assert.Equal(20u, decoded.AckFor(2));
        Assert.Equal(0u, decoded.AckFor(0)); // no anchor for the host — nothing to replay
    }

    // Cross-language parity anchor: the TypeScript codec MUST produce these exact bytes for the
    // same frame (server/worker/src/protocol/codec.test.ts asserts the identical vector). All
    // float values are exact in IEEE-754 single precision so the bytes are unambiguous.
    [Fact]
    public void InputFrame_EncodesToTheCanonicalByteVector()
    {
        var frame = new InputFrame(Seq: 1, MoveX: 1f, MoveY: -1f, Aim: 0.5f, Buttons: 1, Slot: 2);

        var bytes = ProtocolCodec.EncodeInput(frame);

        Assert.Equal(new byte[]
        {
            0x01, 0x00, 0x00, 0x00, // seq = 1
            0x00, 0x00, 0x80, 0x3F, // moveX = 1.0
            0x00, 0x00, 0x80, 0xBF, // moveY = -1.0
            0x00, 0x00, 0x00, 0x3F, // aim = 0.5
            0x01,                   // buttons = fire
            0x02,                   // slot = 2 — LAST so the relay can stamp it without decoding
        }, bytes);
    }

    // ADR-0019 step 3: inputs gained a kind tag. The relay forwards guest bytes to the HOST CLIENT,
    // whose one socket also carries the welcome — so every message must self-identify, inputs included.
    [Fact]
    public void InputMessage_EncodesToTheTaggedCanonicalByteVector()
    {
        var frame = new InputFrame(Seq: 1, MoveX: 1f, MoveY: -1f, Aim: 0.5f, Buttons: 1, Slot: 2);

        var bytes = ProtocolCodec.EncodeInputMessage(frame);

        Assert.Equal(new byte[]
        {
            0x03,                   // MsgInput
            0x01, 0x00, 0x00, 0x00, // seq = 1
            0x00, 0x00, 0x80, 0x3F, // moveX = 1.0
            0x00, 0x00, 0x80, 0xBF, // moveY = -1.0
            0x00, 0x00, 0x00, 0x3F, // aim = 0.5
            0x01,                   // buttons = fire
            0x02,                   // slot = 2 (the relay overwrites this byte with the sender's)
        }, bytes);
        Assert.Equal(frame, ProtocolCodec.DecodeInput(bytes.AsSpan(1)));
    }

    [Fact]
    public void Welcome_EncodesToTheCanonicalByteVector()
    {
        // Parity anchor — codec.test.ts asserts the identical [MSG_WELCOME, slot] bytes.
        Assert.Equal(new byte[] { 0x01, 0x01 }, ProtocolCodec.EncodeWelcome(1));
        Assert.Equal(new byte[] { 0x01, 0x00 }, ProtocolCodec.EncodeWelcome(0));
        Assert.Equal((byte)1, ProtocolCodec.DecodeWelcome(ProtocolCodec.EncodeWelcome(1)));
    }

    [Fact]
    public void Snapshot_EncodesToTheCanonicalByteVector()
    {
        var frame = new SnapshotFrame(
            Tick: 2,
            Acks: new List<InputAck> { new(Slot: 1, Seq: 1) },
            Tanks: new List<TankState> { new(Slot: 0, X: 64f, Y: 128f, Rotation: 0f, TurretRotation: 0.5f, Hp: 3, Team: 1, Shield: 0, Layer: 0, Lives: 5) },
            WallDeltas: new List<WallDelta> { new(CellX: 5, CellY: 6, Material: 1, Hp: 2) },
            Projectiles: new List<ProjectileState>());

        var bytes = ProtocolCodec.EncodeSnapshot(frame);

        Assert.Equal(new byte[]
        {
            0x02, 0x00, 0x00, 0x00, // tick = 2
            0x01,                   // ackCount = 1 (one anchor per guest slot)
            0x01,                   // ack slot = 1
            0x01, 0x00, 0x00, 0x00, // ack seq = 1
            0x01,                   // tankCount = 1
            0x00,                   // slot = 0
            0x00, 0x00, 0x80, 0x42, // x = 64.0
            0x00, 0x00, 0x00, 0x43, // y = 128.0
            0x00, 0x00, 0x00, 0x00, // rotation = 0.0
            0x00, 0x00, 0x00, 0x3F, // turret = 0.5
            0x03,                   // hp = 3
            0x01,                   // team = 1
            0x00,                   // shield = 0
            0x00,                   // layer = 0
            0x01, 0x00,             // wallCount = 1
            0x05, 0x00,             // cellX = 5
            0x06, 0x00,             // cellY = 6
            0x01,                   // material = 1 (brick)
            0x02,                   // hp = 2
            0x00, 0x00,             // projectileCount = 0
            0x00,                   // powerupCount = 0
            0x01,                   // livesCount = 1
            0x05,                   // lives = 5
        }, bytes);
    }

    [Fact]
    public void SnapshotFrame_RoundTrips_WithPowerups()
    {
        var original = new SnapshotFrame(
            Tick: 12,
            Acks: new List<InputAck> { new(1, 3) },
            Tanks: new List<TankState>(),
            WallDeltas: new List<WallDelta>(),
            Projectiles: new List<ProjectileState>())
        {
            Powerups = new List<PowerupState>
            {
                new(Id: 3, Kind: 8, CellX: 5, CellY: 9, Available: 1),
                new(Id: 4, Kind: 0, CellX: 20, CellY: 1, Available: 0),
            },
        };

        var decoded = ProtocolCodec.DecodeSnapshot(ProtocolCodec.EncodeSnapshot(original));

        Assert.Equal(original.Powerups, decoded.Powerups);
    }

    [Fact]
    public void SnapshotFrame_RoundTrips_TankLives()
    {
        var original = new SnapshotFrame(
            Tick: 3,
            Acks: new List<InputAck>(),
            Tanks: new List<TankState>
            {
                new(0, 1f, 2f, 0f, 0f, 0, 0, 0, 0, Lives: 4), // down, waiting to respawn
                new(1, 3f, 4f, 0f, 0f, 8, 1, 0, 0, Lives: 6),
            },
            WallDeltas: new List<WallDelta>(),
            Projectiles: new List<ProjectileState>());

        var decoded = ProtocolCodec.DecodeSnapshot(ProtocolCodec.EncodeSnapshot(original));

        Assert.Equal(original.Tanks, decoded.Tanks);
    }

    [Fact]
    public void SnapshotWithoutPowerupSection_DecodesWithEmptyPowerups()
    {
        // Pre-powerup snapshot bytes (the old layout ends at the projectile section) still decode —
        // the powerup section is a tolerated trailing extension, so the wire format is not broken.
        var legacy = new byte[]
        {
            0x07, 0x00, 0x00, 0x00, // tick = 7
            0x00,                   // ackCount = 0
            0x00,                   // tankCount = 0
            0x00, 0x00,             // wallCount = 0
            0x00, 0x00,             // projectileCount = 0
        };

        var decoded = ProtocolCodec.DecodeSnapshot(legacy);

        Assert.Equal(7u, decoded.Tick);
        Assert.Empty(decoded.Powerups);
    }

    // Cross-language parity anchor: the TS codec asserts the identical bytes for this powerup entry.
    [Fact]
    public void SnapshotWithAPowerup_EncodesToTheCanonicalByteVector()
    {
        var frame = new SnapshotFrame(
            Tick: 2,
            Acks: new List<InputAck>(),
            Tanks: new List<TankState>(),
            WallDeltas: new List<WallDelta>(),
            Projectiles: new List<ProjectileState>())
        {
            Powerups = new List<PowerupState> { new(Id: 3, Kind: 8, CellX: 5, CellY: 9, Available: 1) },
        };

        Assert.Equal(new byte[]
        {
            0x02, 0x00, 0x00, 0x00, // tick = 2
            0x00,                   // ackCount = 0
            0x00,                   // tankCount = 0
            0x00, 0x00,             // wallCount = 0
            0x00, 0x00,             // projectileCount = 0
            0x01,                   // powerupCount = 1
            0x03, 0x00,             // id = 3
            0x08,                   // kind = 8 (Telephone)
            0x05, 0x00,             // cellX = 5
            0x09, 0x00,             // cellY = 9
            0x01,                   // available = 1
            0x00,                   // livesCount = 0
        }, ProtocolCodec.EncodeSnapshot(frame));
    }
}
