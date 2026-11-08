// Wire protocol — the TypeScript mirror of the C# `TankGame.Domain.Net.ProtocolCodec`.
// Little-endian, fixed-layout binary; the two languages MUST produce identical bytes (guarded by
// the shared byte-vector test in codec.test.ts and ProtocolCodecTests.cs). See ADR-0005.

export interface InputFrame {
  seq: number;
  moveX: number;
  moveY: number;
  aim: number;
  buttons: number;
  // The sender's slot — with up to 4 players the host must attribute each relayed frame. The relay
  // overwrites this byte with the sender socket's real slot, so a client can only act as itself.
  slot: number;
}

export interface TankState {
  slot: number;
  x: number;
  y: number;
  rotation: number;
  turretRotation: number;
  hp: number;
  team: number;
  // Over-shield points (a guest must see a shielded remote tank, ADR-0019 step 4) and the elevation
  // layer the tank stands on (ADR-0018), so a mirrored tank renders at the right height.
  shield: number;
  layer: number;
  // Lives left including the current one (respawns). Rides a trailing section after the pickups;
  // 0 when decoding a snapshot from before it existed.
  lives: number;
}

export interface WallDelta {
  cellX: number;
  cellY: number;
  material: number;
  hp: number;
}

// One live shot's state, so a guest can see shots in flight (ADR-0019 step 4). `rotation` is the
// travel heading (atan2(dir.x, dir.y)); `style` is 0 bullet / 1 missile; `layer` is the elevation
// layer the shot rides (ADR-0018).
export interface ProjectileState {
  x: number;
  y: number;
  rotation: number;
  style: number;
  layer: number;
}

// The last input seq the host applied for one guest slot — the snapshot is a broadcast, so every
// predicting guest finds its own reconciliation anchor in the list.
export interface InputAck {
  slot: number;
  seq: number;
}

// One live pickup's state (the host's boost-director drops): `id` is a small host-assigned handle
// stable for the pickup's lifetime, `kind` the PowerupKind ordinal, the cell locates it, and
// `available` is 0 while dormant. Mirrors TankGame.Domain.Net.PowerupState.
export interface PowerupState {
  id: number;
  kind: number;
  cellX: number;
  cellY: number;
  available: number;
}

export interface SnapshotFrame {
  tick: number;
  acks: InputAck[];
  tanks: TankState[];
  wallDeltas: WallDelta[];
  projectiles: ProjectileState[];
  // Appended LAST on the wire as a tolerated trailing section — pre-pickup bytes decode to [].
  powerups: PowerupState[];
}

// seq(4) + move(4+4) + aim(4) + buttons(1) + slot(1). The slot is deliberately the LAST byte of the
// message so the relay can overwrite it without decoding the frame.
export const INPUT_FRAME_SIZE = 18;
export const INPUT_ACK_SIZE = 5;
export const TANK_STATE_SIZE = 21;
export const WALL_DELTA_SIZE = 6;
export const PROJECTILE_STATE_SIZE = 14;
// id(2) + kind(1) + cell(2+2) + available(1).
export const POWERUP_STATE_SIZE = 8;
export const FIRE_BIT = 1 << 0;

// Every message is tagged with a leading kind byte: a Welcome (sent once on connect, carrying the
// player's assigned slot), a Snapshot, or an Input. Inputs are tagged too (ADR-0019 step 3) — the
// relay forwards guest bytes to the HOST CLIENT, whose one socket also carries the welcome, so each
// message must self-identify. The C# client mirrors these tags (TankGame.Domain.Net.ProtocolCodec)
// and the byte vectors in codec.test.ts are cross-language parity anchors.
export const MSG_WELCOME = 1;
export const MSG_SNAPSHOT = 2;
export const MSG_INPUT = 3;

// Lobby control rides the same socket as the game relay but is JSON, not fixed-layout binary: the
// pre-game flow is low-rate and shape-churning, so a readable JSON payload behind a tag byte is the
// right trade (the fixed binary frames above stay byte-parity-locked with C#). The DO consumes a
// MSG_LOBBY_CMD itself (never relays it) and answers every member with MSG_LOBBY_STATE.
export const MSG_LOBBY_STATE = 0x10; // server → all members: the current LobbyState as JSON
export const MSG_LOBBY_CMD = 0x11; // client → server: a LobbyCommand as JSON (server stamps the slot)

const LOBBY_TEXT = { encoder: new TextEncoder(), decoder: new TextDecoder() };

function encodeTaggedJson(tag: number, value: unknown): Uint8Array {
  const json = LOBBY_TEXT.encoder.encode(JSON.stringify(value));
  const message = new Uint8Array(json.length + 1);
  message[0] = tag;
  message.set(json, 1);
  return message;
}

/** `[MSG_LOBBY_STATE, ...utf8(JSON)]` — the lobby snapshot the DO pushes to every member. */
export function encodeLobbyState(state: unknown): Uint8Array {
  return encodeTaggedJson(MSG_LOBBY_STATE, state);
}

/** `[MSG_LOBBY_CMD, ...utf8(JSON)]` — a control command a client puts on the socket. */
export function encodeLobbyCommand(command: unknown): Uint8Array {
  return encodeTaggedJson(MSG_LOBBY_CMD, command);
}

/** Parse a tagged lobby message's JSON payload (drops the leading tag byte). */
export function decodeLobbyJson<T>(message: Uint8Array): T {
  return JSON.parse(LOBBY_TEXT.decoder.decode(message.subarray(1))) as T;
}

/** A welcome message: `[MSG_WELCOME, slot]`. Tells a freshly-joined client which slot it controls. */
export function encodeWelcome(slot: number): Uint8Array {
  return new Uint8Array([MSG_WELCOME, slot & 0xff]);
}

/** An input message: `[MSG_INPUT, ...encodeInput(frame)]` — what a guest puts on the socket. */
export function encodeInputMessage(frame: InputFrame): Uint8Array {
  const payload = encodeInput(frame);
  const message = new Uint8Array(payload.length + 1);
  message[0] = MSG_INPUT;
  message.set(payload, 1);
  return message;
}

/** A snapshot message: `[MSG_SNAPSHOT, ...encodeSnapshot(frame)]`. */
export function encodeSnapshotMessage(frame: SnapshotFrame): Uint8Array {
  const payload = encodeSnapshot(frame);
  const message = new Uint8Array(payload.length + 1);
  message[0] = MSG_SNAPSHOT;
  message.set(payload, 1);
  return message;
}

export function encodeInput(frame: InputFrame): Uint8Array {
  const buffer = new Uint8Array(INPUT_FRAME_SIZE);
  const view = new DataView(buffer.buffer);
  view.setUint32(0, frame.seq, true);
  view.setFloat32(4, frame.moveX, true);
  view.setFloat32(8, frame.moveY, true);
  view.setFloat32(12, frame.aim, true);
  view.setUint8(16, frame.buttons);
  view.setUint8(17, frame.slot);
  return buffer;
}

export function decodeInput(data: Uint8Array): InputFrame {
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  return {
    seq: view.getUint32(0, true),
    moveX: view.getFloat32(4, true),
    moveY: view.getFloat32(8, true),
    aim: view.getFloat32(12, true),
    buttons: view.getUint8(16),
    slot: view.getUint8(17),
  };
}

export function encodeSnapshot(frame: SnapshotFrame): Uint8Array {
  const size =
    4 +
    1 +
    frame.acks.length * INPUT_ACK_SIZE +
    1 +
    frame.tanks.length * TANK_STATE_SIZE +
    2 +
    frame.wallDeltas.length * WALL_DELTA_SIZE +
    2 +
    frame.projectiles.length * PROJECTILE_STATE_SIZE +
    1 +
    frame.powerups.length * POWERUP_STATE_SIZE +
    1 +
    frame.tanks.length;
  const buffer = new Uint8Array(size);
  const view = new DataView(buffer.buffer);
  let offset = 0;

  view.setUint32(offset, frame.tick, true);
  offset += 4;

  // One reconciliation ack per guest slot (count byte + pairs).
  view.setUint8(offset, frame.acks.length);
  offset += 1;
  for (const ack of frame.acks) {
    view.setUint8(offset, ack.slot);
    offset += 1;
    view.setUint32(offset, ack.seq, true);
    offset += 4;
  }

  view.setUint8(offset, frame.tanks.length);
  offset += 1;
  for (const tank of frame.tanks) {
    view.setUint8(offset, tank.slot);
    offset += 1;
    view.setFloat32(offset, tank.x, true);
    offset += 4;
    view.setFloat32(offset, tank.y, true);
    offset += 4;
    view.setFloat32(offset, tank.rotation, true);
    offset += 4;
    view.setFloat32(offset, tank.turretRotation, true);
    offset += 4;
    view.setUint8(offset, tank.hp);
    offset += 1;
    view.setUint8(offset, tank.team);
    offset += 1;
    view.setUint8(offset, tank.shield);
    offset += 1;
    view.setUint8(offset, tank.layer);
    offset += 1;
  }

  view.setUint16(offset, frame.wallDeltas.length, true);
  offset += 2;
  for (const wall of frame.wallDeltas) {
    view.setUint16(offset, wall.cellX, true);
    offset += 2;
    view.setUint16(offset, wall.cellY, true);
    offset += 2;
    view.setUint8(offset, wall.material);
    offset += 1;
    view.setUint8(offset, wall.hp);
    offset += 1;
  }

  view.setUint16(offset, frame.projectiles.length, true);
  offset += 2;
  for (const shot of frame.projectiles) {
    view.setFloat32(offset, shot.x, true);
    offset += 4;
    view.setFloat32(offset, shot.y, true);
    offset += 4;
    view.setFloat32(offset, shot.rotation, true);
    offset += 4;
    view.setUint8(offset, shot.style);
    offset += 1;
    view.setUint8(offset, shot.layer);
    offset += 1;
  }

  // The pickup section is appended LAST (count byte + entries) so a pre-pickup decoder simply never
  // reads it — the extension does not break the wire format.
  view.setUint8(offset, frame.powerups.length);
  offset += 1;
  for (const pickup of frame.powerups) {
    view.setUint16(offset, pickup.id, true);
    offset += 2;
    view.setUint8(offset, pickup.kind);
    offset += 1;
    view.setUint16(offset, pickup.cellX, true);
    offset += 2;
    view.setUint16(offset, pickup.cellY, true);
    offset += 2;
    view.setUint8(offset, pickup.available);
    offset += 1;
  }

  // Lives, one byte per tank in tank order, appended after the pickups for the same reason.
  view.setUint8(offset, frame.tanks.length);
  offset += 1;
  for (const tank of frame.tanks) {
    view.setUint8(offset, tank.lives);
    offset += 1;
  }

  return buffer;
}

export function decodeSnapshot(data: Uint8Array): SnapshotFrame {
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  let offset = 0;

  const tick = view.getUint32(offset, true);
  offset += 4;

  const ackCount = view.getUint8(offset);
  offset += 1;
  const acks: InputAck[] = [];
  for (let i = 0; i < ackCount; i++) {
    const slot = view.getUint8(offset);
    offset += 1;
    const seq = view.getUint32(offset, true);
    offset += 4;
    acks.push({ slot, seq });
  }

  const tankCount = view.getUint8(offset);
  offset += 1;
  const tanks: TankState[] = [];
  for (let i = 0; i < tankCount; i++) {
    const slot = view.getUint8(offset);
    offset += 1;
    const x = view.getFloat32(offset, true);
    offset += 4;
    const y = view.getFloat32(offset, true);
    offset += 4;
    const rotation = view.getFloat32(offset, true);
    offset += 4;
    const turretRotation = view.getFloat32(offset, true);
    offset += 4;
    const hp = view.getUint8(offset);
    offset += 1;
    const team = view.getUint8(offset);
    offset += 1;
    const shield = view.getUint8(offset);
    offset += 1;
    const layer = view.getUint8(offset);
    offset += 1;
    tanks.push({ slot, x, y, rotation, turretRotation, hp, team, shield, layer, lives: 0 });
  }

  const wallCount = view.getUint16(offset, true);
  offset += 2;
  const wallDeltas: WallDelta[] = [];
  for (let i = 0; i < wallCount; i++) {
    const cellX = view.getUint16(offset, true);
    offset += 2;
    const cellY = view.getUint16(offset, true);
    offset += 2;
    const material = view.getUint8(offset);
    offset += 1;
    const hp = view.getUint8(offset);
    offset += 1;
    wallDeltas.push({ cellX, cellY, material, hp });
  }

  const projectileCount = view.getUint16(offset, true);
  offset += 2;
  const projectiles: ProjectileState[] = [];
  for (let i = 0; i < projectileCount; i++) {
    const x = view.getFloat32(offset, true);
    offset += 4;
    const y = view.getFloat32(offset, true);
    offset += 4;
    const rotation = view.getFloat32(offset, true);
    offset += 4;
    const style = view.getUint8(offset);
    offset += 1;
    const layer = view.getUint8(offset);
    offset += 1;
    projectiles.push({ x, y, rotation, style, layer });
  }

  // Tolerated trailing extension: bytes encoded before the pickup section existed end here.
  const powerups: PowerupState[] = [];
  if (offset < data.byteLength) {
    const powerupCount = view.getUint8(offset);
    offset += 1;
    for (let i = 0; i < powerupCount; i++) {
      const id = view.getUint16(offset, true);
      offset += 2;
      const kind = view.getUint8(offset);
      offset += 1;
      const cellX = view.getUint16(offset, true);
      offset += 2;
      const cellY = view.getUint16(offset, true);
      offset += 2;
      const available = view.getUint8(offset);
      offset += 1;
      powerups.push({ id, kind, cellX, cellY, available });
    }
  }

  // Tolerated trailing extension: a snapshot from before respawns leaves lives at 0.
  if (offset < data.byteLength) {
    const livesCount = view.getUint8(offset);
    offset += 1;
    for (let i = 0; i < livesCount && i < tanks.length; i++) {
      tanks[i].lives = view.getUint8(offset + i);
    }
  }

  return { tick, acks, tanks, wallDeltas, projectiles, powerups };
}
