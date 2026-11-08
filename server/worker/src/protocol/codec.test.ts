import { describe, it, expect } from "vitest";
import {
  encodeInput,
  decodeInput,
  encodeSnapshot,
  decodeSnapshot,
  encodeWelcome,
  encodeSnapshotMessage,
  encodeInputMessage,
  MSG_WELCOME,
  MSG_SNAPSHOT,
  MSG_INPUT,
  FIRE_BIT,
  type SnapshotFrame,
} from "./codec";

describe("protocol codec", () => {
  it("round-trips an InputFrame", () => {
    const original = { seq: 42, moveX: 0.25, moveY: -0.5, aim: 1.5, buttons: FIRE_BIT, slot: 3 };
    expect(decodeInput(encodeInput(original))).toEqual(original);
  });

  it("round-trips a SnapshotFrame with tanks and wall deltas", () => {
    const original: SnapshotFrame = {
      tick: 100,
      acks: [{ slot: 1, seq: 41 }],
      tanks: [
        { slot: 0, x: 64, y: 128, rotation: 0, turretRotation: 0.5, hp: 3, team: 0, shield: 4, layer: 1, lives: 6 },
        { slot: 1, x: 200, y: 96, rotation: 0.25, turretRotation: -1, hp: 2, team: 1, shield: 0, layer: 0, lives: 0 },
      ],
      wallDeltas: [
        { cellX: 5, cellY: 6, material: 1, hp: 2 },
        { cellX: 12, cellY: 0, material: 0, hp: 0 },
      ],
      projectiles: [],

      powerups: [],
    };
    expect(decodeSnapshot(encodeSnapshot(original))).toEqual(original);
  });

  it("round-trips an empty snapshot", () => {
    const original: SnapshotFrame = { tick: 7, acks: [{ slot: 1, seq: 7 }], tanks: [], wallDeltas: [], projectiles: [], powerups: [] };
    expect(decodeSnapshot(encodeSnapshot(original))).toEqual(original);
  });

  it("round-trips a SnapshotFrame with projectiles", () => {
    const original: SnapshotFrame = {
      tick: 9,
      acks: [{ slot: 1, seq: 3 }, { slot: 2, seq: 5 }],
      tanks: [],
      wallDeltas: [],
      projectiles: [
        { x: 320, y: 64, rotation: 1.25, style: 0, layer: 0 },
        { x: 96.5, y: 200, rotation: -2.5, style: 1, layer: 2 },
      ],
      powerups: [],
    };
    expect(decodeSnapshot(encodeSnapshot(original))).toEqual(original);
  });

  it("round-trips a SnapshotFrame with powerups", () => {
    const original: SnapshotFrame = {
      tick: 12,
      acks: [{ slot: 1, seq: 3 }],
      tanks: [],
      wallDeltas: [],
      projectiles: [],
      powerups: [
        { id: 3, kind: 8, cellX: 5, cellY: 9, available: 1 },
        { id: 4, kind: 0, cellX: 20, cellY: 1, available: 0 },
      ],
    };
    expect(decodeSnapshot(encodeSnapshot(original))).toEqual(original);
  });

  it("decodes pre-powerup snapshot bytes with empty powerups", () => {
    // The powerup section is a tolerated trailing extension — the old layout still decodes.
    const legacy = new Uint8Array([
      0x07, 0x00, 0x00, 0x00, // tick = 7
      0x00, // ackCount = 0
      0x00, // tankCount = 0
      0x00, 0x00, // wallCount = 0
      0x00, 0x00, // projectileCount = 0
    ]);
    const decoded = decodeSnapshot(legacy);
    expect(decoded.tick).toBe(7);
    expect(decoded.powerups).toEqual([]);
  });

  // Cross-language parity anchor — these exact bytes are asserted byte-for-byte by the C# codec
  // (client/tests/Domain/ProtocolCodecTests.cs). If either side changes the layout, both fail.
  it("encodes a Welcome to the canonical byte vector", () => {
    // Parity anchor — ProtocolCodecTests.cs asserts the identical [MSG_WELCOME, slot] bytes.
    expect([...encodeWelcome(1)]).toEqual([MSG_WELCOME, 0x01]);
    expect([...encodeWelcome(0)]).toEqual([MSG_WELCOME, 0x00]);
  });

  it("tags a snapshot message with the snapshot kind byte", () => {
    const frame: SnapshotFrame = { tick: 7, acks: [{ slot: 1, seq: 7 }], tanks: [], wallDeltas: [], projectiles: [], powerups: [] };
    const message = encodeSnapshotMessage(frame);
    expect(message[0]).toBe(MSG_SNAPSHOT);
    expect(decodeSnapshot(message.subarray(1))).toEqual(frame);
  });

  // ADR-0019 step 3: inputs gained a kind tag — the relay forwards guest bytes to the host CLIENT,
  // whose one socket also carries the welcome, so every message must self-identify.
  it("encodes an input message to the tagged canonical byte vector", () => {
    const bytes = [...encodeInputMessage({ seq: 1, moveX: 1, moveY: -1, aim: 0.5, buttons: 1, slot: 2 })];
    expect(bytes).toEqual([
      MSG_INPUT, // 0x03
      0x01, 0x00, 0x00, 0x00, // seq = 1
      0x00, 0x00, 0x80, 0x3f, // moveX = 1.0
      0x00, 0x00, 0x80, 0xbf, // moveY = -1.0
      0x00, 0x00, 0x00, 0x3f, // aim = 0.5
      0x01, // buttons = fire
      0x02, // slot = 2 (the relay overwrites this byte with the sender's)
    ]);
  });

  it("encodes an InputFrame to the canonical byte vector", () => {
    const bytes = [...encodeInput({ seq: 1, moveX: 1, moveY: -1, aim: 0.5, buttons: 1, slot: 2 })];
    expect(bytes).toEqual([
      0x01, 0x00, 0x00, 0x00, // seq = 1
      0x00, 0x00, 0x80, 0x3f, // moveX = 1.0
      0x00, 0x00, 0x80, 0xbf, // moveY = -1.0
      0x00, 0x00, 0x00, 0x3f, // aim = 0.5
      0x01, // buttons = fire
      0x02, // slot = 2 — LAST so the relay can stamp it without decoding
    ]);
  });

  it("encodes a SnapshotFrame to the canonical byte vector", () => {
    const bytes = [
      ...encodeSnapshot({
        tick: 2,
        acks: [{ slot: 1, seq: 1 }],
        tanks: [{ slot: 0, x: 64, y: 128, rotation: 0, turretRotation: 0.5, hp: 3, team: 1, shield: 0, layer: 0, lives: 5 }],
        wallDeltas: [{ cellX: 5, cellY: 6, material: 1, hp: 2 }],
        projectiles: [],

        powerups: [],
      }),
    ];
    expect(bytes).toEqual([
      0x02, 0x00, 0x00, 0x00, // tick = 2
      0x01, // ackCount = 1 (one anchor per guest slot)
      0x01, // ack slot = 1
      0x01, 0x00, 0x00, 0x00, // ack seq = 1
      0x01, // tankCount = 1
      0x00, // slot = 0
      0x00, 0x00, 0x80, 0x42, // x = 64.0
      0x00, 0x00, 0x00, 0x43, // y = 128.0
      0x00, 0x00, 0x00, 0x00, // rotation = 0.0
      0x00, 0x00, 0x00, 0x3f, // turret = 0.5
      0x03, // hp = 3
      0x01, // team = 1
      0x00, // shield = 0
      0x00, // layer = 0
      0x01, 0x00, // wallCount = 1
      0x05, 0x00, // cellX = 5
      0x06, 0x00, // cellY = 6
      0x01, // material = 1 (brick)
      0x02, // hp = 2
      0x00, 0x00, // projectileCount = 0
      0x00, // powerupCount = 0
      0x01, // livesCount = 1
      0x05, // lives = 5
    ]);
  });

  // Parity anchor — ProtocolCodecTests.cs asserts the identical bytes for this powerup entry.
  it("encodes a SnapshotFrame with a powerup to the canonical byte vector", () => {
    const bytes = [
      ...encodeSnapshot({
        tick: 2,
        acks: [],
        tanks: [],
        wallDeltas: [],
        projectiles: [],
        powerups: [{ id: 3, kind: 8, cellX: 5, cellY: 9, available: 1 }],
      }),
    ];
    expect(bytes).toEqual([
      0x02, 0x00, 0x00, 0x00, // tick = 2
      0x00, // ackCount = 0
      0x00, // tankCount = 0
      0x00, 0x00, // wallCount = 0
      0x00, 0x00, // projectileCount = 0
      0x01, // powerupCount = 1
      0x03, 0x00, // id = 3
      0x08, // kind = 8 (Telephone)
      0x05, 0x00, // cellX = 5
      0x09, 0x00, // cellY = 9
      0x01, // available = 1
      0x00, // livesCount = 0
    ]);
  });
});
