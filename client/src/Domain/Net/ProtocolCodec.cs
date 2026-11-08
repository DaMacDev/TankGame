using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace TankGame.Domain.Net;

/// <summary>The wire format: little-endian, fixed-layout binary for <see cref="InputFrame"/> and
/// <see cref="SnapshotFrame"/>. Pure C#, no Godot, no dependencies. The TypeScript server encodes
/// and decodes the identical bytes (<c>server/worker/src/protocol/codec.ts</c>); a shared
/// byte-vector test in each language guards against drift. Hand-rolled rather than a serializer
/// library so the format is explicit and trivially portable (ADR-0005).</summary>
public static class ProtocolCodec
{
    /// <summary>Encoded size of an <see cref="InputFrame"/>: seq(4) + move(4+4) + aim(4) + buttons(1)
    /// + slot(1). The slot is deliberately the LAST byte of the message so the relay can overwrite it
    /// with the sender's real slot without decoding the frame.</summary>
    public const int InputFrameSize = 18;

    /// <summary>Encoded size of one <see cref="InputAck"/>: slot(1) + seq(4).</summary>
    public const int InputAckSize = 5;

    /// <summary>Encoded size of one <see cref="TankState"/>: slot(1) + 4 floats(16) + hp(1) + team(1)
    /// + shield(1) + layer(1).</summary>
    public const int TankStateSize = 21;

    /// <summary>Encoded size of one <see cref="WallDelta"/>: cell(2+2) + material(1) + hp(1).</summary>
    public const int WallDeltaSize = 6;

    /// <summary>Encoded size of one <see cref="ProjectileState"/>: 3 floats(12) + style(1) + layer(1).</summary>
    public const int ProjectileStateSize = 14;

    /// <summary>Encoded size of one <see cref="PowerupState"/>: id(2) + kind(1) + cell(2+2) + available(1).</summary>
    public const int PowerupStateSize = 8;

    /// <summary>Leading kind byte of a server→client welcome message (slot assignment).</summary>
    public const byte MsgWelcome = 1;

    /// <summary>Leading kind byte of a server→client snapshot message.</summary>
    public const byte MsgSnapshot = 2;

    /// <summary>Leading kind byte of an input message (ADR-0019 step 3). Inputs are tagged like every
    /// other message because the relay forwards guest bytes to the HOST CLIENT, whose one socket also
    /// carries the welcome — untagged 17-byte inputs would be ambiguous against the other kinds.</summary>
    public const byte MsgInput = 3;

    /// <summary>A welcome message: <c>[MsgWelcome, slot]</c>. The server sends it once on connect to
    /// tell the client which slot it controls. Mirrors <c>encodeWelcome</c> in the TS codec — the
    /// byte vector is a cross-language parity anchor.</summary>
    public static byte[] EncodeWelcome(byte slot) => new[] { MsgWelcome, slot };

    /// <summary>Reads the slot from a welcome message (the byte after the kind tag).</summary>
    public static byte DecodeWelcome(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            throw new ArgumentException($"welcome message needs 2 bytes, got {data.Length}");
        }

        return data[1];
    }

    /// <summary>An input message: <c>[MsgInput | EncodeInput(frame)]</c> — what a guest puts on the
    /// socket; the host strips the tag and decodes the payload with <see cref="DecodeInput"/>.</summary>
    public static byte[] EncodeInputMessage(InputFrame frame)
    {
        var payload = EncodeInput(frame);
        var message = new byte[payload.Length + 1];
        message[0] = MsgInput;
        payload.CopyTo(message, 1);
        return message;
    }

    public static byte[] EncodeInput(InputFrame frame)
    {
        var buffer = new byte[InputFrameSize];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], frame.Seq);
        BinaryPrimitives.WriteSingleLittleEndian(span[4..], frame.MoveX);
        BinaryPrimitives.WriteSingleLittleEndian(span[8..], frame.MoveY);
        BinaryPrimitives.WriteSingleLittleEndian(span[12..], frame.Aim);
        span[16] = frame.Buttons;
        span[17] = frame.Slot;
        return buffer;
    }

    public static InputFrame DecodeInput(ReadOnlySpan<byte> data)
    {
        if (data.Length < InputFrameSize)
        {
            throw new ArgumentException($"input frame needs {InputFrameSize} bytes, got {data.Length}");
        }

        return new InputFrame(
            BinaryPrimitives.ReadUInt32LittleEndian(data[0..]),
            BinaryPrimitives.ReadSingleLittleEndian(data[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(data[8..]),
            BinaryPrimitives.ReadSingleLittleEndian(data[12..]),
            data[16],
            data[17]);
    }

    public static byte[] EncodeSnapshot(SnapshotFrame frame)
    {
        var size = 4 + 1 + (frame.Acks.Count * InputAckSize) + 1 + (frame.Tanks.Count * TankStateSize)
            + 2 + (frame.WallDeltas.Count * WallDeltaSize) + 2 + (frame.Projectiles.Count * ProjectileStateSize)
            + 1 + (frame.Powerups.Count * PowerupStateSize) + 1 + frame.Tanks.Count;
        var buffer = new byte[size];
        var span = buffer.AsSpan();
        var offset = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], frame.Tick);
        offset += 4;

        // One reconciliation ack per guest slot (count byte + pairs) — the snapshot is a broadcast,
        // so every predicting guest must find its own anchor in it.
        span[offset++] = (byte)frame.Acks.Count;
        foreach (var ack in frame.Acks)
        {
            span[offset++] = ack.Slot;
            BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], ack.Seq);
            offset += 4;
        }

        span[offset++] = (byte)frame.Tanks.Count;
        foreach (var tank in frame.Tanks)
        {
            span[offset++] = tank.Slot;
            BinaryPrimitives.WriteSingleLittleEndian(span[offset..], tank.X);
            offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(span[offset..], tank.Y);
            offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(span[offset..], tank.Rotation);
            offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(span[offset..], tank.TurretRotation);
            offset += 4;
            span[offset++] = tank.Hp;
            span[offset++] = tank.Team;
            span[offset++] = tank.Shield;
            span[offset++] = tank.Layer;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], (ushort)frame.WallDeltas.Count);
        offset += 2;
        foreach (var wall in frame.WallDeltas)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], wall.CellX);
            offset += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], wall.CellY);
            offset += 2;
            span[offset++] = wall.Material;
            span[offset++] = wall.Hp;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], (ushort)frame.Projectiles.Count);
        offset += 2;
        foreach (var shot in frame.Projectiles)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span[offset..], shot.X);
            offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(span[offset..], shot.Y);
            offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(span[offset..], shot.Rotation);
            offset += 4;
            span[offset++] = shot.Style;
            span[offset++] = shot.Layer;
        }

        // The pickup section is appended LAST (count byte + entries) so a pre-pickup decoder simply
        // never reads it — the extension does not break the wire format.
        span[offset++] = (byte)frame.Powerups.Count;
        foreach (var pickup in frame.Powerups)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], pickup.Id);
            offset += 2;
            span[offset++] = pickup.Kind;
            BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], pickup.CellX);
            offset += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], pickup.CellY);
            offset += 2;
            span[offset++] = pickup.Available;
        }

        // Lives, one byte per tank in tank order, appended after the pickups for the same reason.
        span[offset++] = (byte)frame.Tanks.Count;
        foreach (var tank in frame.Tanks)
        {
            span[offset++] = tank.Lives;
        }

        return buffer;
    }

    public static SnapshotFrame DecodeSnapshot(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        var tick = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;

        var ackCount = data[offset++];
        var acks = new List<InputAck>(ackCount);
        for (var i = 0; i < ackCount; i++)
        {
            var ackSlot = data[offset++];
            acks.Add(new InputAck(ackSlot, BinaryPrimitives.ReadUInt32LittleEndian(data[offset..])));
            offset += 4;
        }

        var tankCount = data[offset++];
        var tanks = new List<TankState>(tankCount);
        for (var i = 0; i < tankCount; i++)
        {
            var slot = data[offset++];
            var x = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
            offset += 4;
            var y = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
            offset += 4;
            var rotation = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
            offset += 4;
            var turret = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
            offset += 4;
            var hp = data[offset++];
            var team = data[offset++];
            var shield = data[offset++];
            var layer = data[offset++];
            tanks.Add(new TankState(slot, x, y, rotation, turret, hp, team, shield, layer));
        }

        var wallCount = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        var walls = new List<WallDelta>(wallCount);
        for (var i = 0; i < wallCount; i++)
        {
            var cellX = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            offset += 2;
            var cellY = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            offset += 2;
            var material = data[offset++];
            var hp = data[offset++];
            walls.Add(new WallDelta(cellX, cellY, material, hp));
        }

        var projectileCount = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        var projectiles = new List<ProjectileState>(projectileCount);
        for (var i = 0; i < projectileCount; i++)
        {
            var x = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
            offset += 4;
            var y = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
            offset += 4;
            var rotation = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
            offset += 4;
            var style = data[offset++];
            var layer = data[offset++];
            projectiles.Add(new ProjectileState(x, y, rotation, style, layer));
        }

        // Tolerated trailing extension: bytes encoded before the pickup section existed end here.
        var powerups = new List<PowerupState>();
        if (offset < data.Length)
        {
            var powerupCount = data[offset++];
            for (var i = 0; i < powerupCount; i++)
            {
                var id = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
                offset += 2;
                var kind = data[offset++];
                var cellX = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
                offset += 2;
                var cellY = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
                offset += 2;
                var available = data[offset++];
                powerups.Add(new PowerupState(id, kind, cellX, cellY, available));
            }
        }

        // Tolerated trailing extension: a snapshot from before respawns leaves Lives at 0 (hp decides).
        if (offset < data.Length)
        {
            var livesCount = data[offset++];
            for (var i = 0; i < livesCount && i < tanks.Count; i++)
            {
                tanks[i] = tanks[i] with { Lives = data[offset + i] };
            }

            offset += livesCount;
        }

        return new SnapshotFrame(tick, acks, tanks, walls, projectiles) { Powerups = powerups };
    }
}
