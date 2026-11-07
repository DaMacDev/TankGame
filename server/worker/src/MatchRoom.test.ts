import { SELF } from "cloudflare:test";
import { describe, it, expect } from "vitest";
import {
  encodeInputMessage,
  encodeSnapshotMessage,
  encodeLobbyCommand,
  decodeInput,
  decodeSnapshot,
  decodeLobbyJson,
  MSG_WELCOME,
  MSG_SNAPSHOT,
  MSG_INPUT,
  MSG_LOBBY_STATE,
  type InputFrame,
  type SnapshotFrame,
} from "./protocol/codec";
import { MAX_PLAYERS, type LobbyState } from "./lobbyState";

// A joined connection that buffers every inbound message from accept time (synchronously, before any
// await) so arrival order is kept and nothing is missed. Messages are either the binary game frames
// or JSON lobby-state pushes (MSG_LOBBY_STATE); helpers below pick out the kind a test wants.
interface Conn {
  ws: WebSocket;
  next(): Promise<Uint8Array>;
}

async function joinRoom(code: string, name?: string): Promise<Conn> {
  const suffix = name ? `?name=${encodeURIComponent(name)}` : "";
  const response = await SELF.fetch(`http://room.test/room/${code}${suffix}`, {
    headers: { Upgrade: "websocket" },
  });
  expect(response.status).toBe(101);
  const ws = response.webSocket;
  expect(ws).toBeTruthy();

  const buffered: Uint8Array[] = [];
  const waiters: ((message: Uint8Array) => void)[] = [];
  ws!.binaryType = "arraybuffer"; // the 2026 compat date defaults a client socket to Blob
  ws!.accept();
  ws!.addEventListener("message", (event: MessageEvent) => {
    const message = new Uint8Array(event.data as ArrayBuffer);
    const waiter = waiters.shift();
    if (waiter) {
      waiter(message);
    } else {
      buffered.push(message);
    }
  });

  return {
    ws: ws!,
    next: () =>
      buffered.length > 0
        ? Promise.resolve(buffered.shift()!)
        : new Promise<Uint8Array>((resolve) => waiters.push(resolve)),
  };
}

// The first server→client message is always the welcome (the slot assignment).
async function welcomeOf(conn: Conn): Promise<number> {
  const message = await conn.next();
  expect(message[0]).toBe(MSG_WELCOME);
  return message[1];
}

// The next non-lobby (binary game) frame — skips any lobby-state pushes that interleave.
async function nextGame(conn: Conn): Promise<Uint8Array> {
  for (;;) {
    const message = await conn.next();
    if (message[0] !== MSG_LOBBY_STATE) {
      return message;
    }
  }
}

// The next lobby-state push, decoded.
async function nextLobby(conn: Conn): Promise<LobbyState> {
  for (;;) {
    const message = await conn.next();
    if (message[0] === MSG_LOBBY_STATE) {
      return decodeLobbyJson<LobbyState>(message);
    }
  }
}

describe("MatchRoom (relay)", () => {
  it("welcomes the host as slot 0 and the guest as slot 1", async () => {
    const host = await joinRoom("WEL001");
    expect(await welcomeOf(host)).toBe(0);

    const guest = await joinRoom("WEL001");
    expect(await welcomeOf(guest)).toBe(1);

    host.ws.close();
    guest.ws.close();
  });

  it("forwards a guest InputFrame to the host (slot 0)", async () => {
    const host = await joinRoom("REL001");
    const guest = await joinRoom("REL001");
    expect(await welcomeOf(host)).toBe(0);
    expect(await welcomeOf(guest)).toBe(1);

    // The guest lies about its slot (0 = the host's): the relay must stamp the sender's REAL slot
    // over the frame's last byte, so the host attributes the input correctly and nobody can act
    // as anyone else.
    const frame: InputFrame = { seq: 7, moveX: 1, moveY: 0, aim: 0.5, buttons: 1, slot: 0 };
    guest.ws.send(encodeInputMessage(frame));

    const received = await nextGame(host);
    expect(received[0]).toBe(MSG_INPUT);
    expect(decodeInput(received.subarray(1))).toEqual({ ...frame, slot: 1 });

    host.ws.close();
    guest.ws.close();
  });

  it("forwards a host SnapshotFrame to the guests", async () => {
    const host = await joinRoom("REL002");
    const guest = await joinRoom("REL002");
    expect(await welcomeOf(host)).toBe(0);
    expect(await welcomeOf(guest)).toBe(1);

    const snapshot: SnapshotFrame = {
      tick: 3,
      acks: [{ slot: 1, seq: 2 }],
      tanks: [{ slot: 0, x: 64, y: 128, rotation: 0, turretRotation: 0.5, hp: 8, team: 0, shield: 0, layer: 0 }],
      wallDeltas: [],
      projectiles: [],

      powerups: [],
    };
    host.ws.send(encodeSnapshotMessage(snapshot));

    const received = await nextGame(guest);
    expect(received[0]).toBe(MSG_SNAPSHOT);
    expect(decodeSnapshot(received.subarray(1))).toEqual(snapshot);

    host.ws.close();
    guest.ws.close();
  });

  it("does not relay a guest's bytes back to that guest (host-only relay)", async () => {
    const host = await joinRoom("REL003");
    const guest = await joinRoom("REL003");
    expect(await welcomeOf(host)).toBe(0);
    expect(await welcomeOf(guest)).toBe(1);

    guest.ws.send(encodeInputMessage({ seq: 1, moveX: 1, moveY: 0, aim: 0, buttons: 0, slot: 1 }));

    // The host receives the input; the guest must NOT receive its own frame echoed back. Race the
    // guest's next game frame against the host's: the host's relayed input must arrive first.
    const sentinel = Symbol("no-echo");
    const winner = await Promise.race([
      nextGame(host).then(() => "host"),
      nextGame(guest).then(() => "echo"),
      new Promise<typeof sentinel>((resolve) => setTimeout(() => resolve(sentinel), 100)),
    ]);
    expect(winner).toBe("host");

    host.ws.close();
    guest.ws.close();
  });

  it("drops anything but inputs from a guest, so a forged lobby state never reaches the host", async () => {
    const host = await joinRoom("REL005");
    const guest = await joinRoom("REL005");
    expect(await welcomeOf(host)).toBe(0);
    expect(await welcomeOf(guest)).toBe(1);

    guest.ws.send(new Uint8Array([MSG_LOBBY_STATE, 0x7b, 0x7d]));
    guest.ws.send(encodeInputMessage({ seq: 1, moveX: 1, moveY: 0, aim: 0, buttons: 0, slot: 1 }));

    const received = await nextGame(host);
    expect(received[0]).toBe(MSG_INPUT); // the forged frame was not delivered ahead of it

    host.ws.close();
    guest.ws.close();
  });

  it("ignores alarm-only and unknown commands sent over a socket", async () => {
    const host = await joinRoom("REL006");
    expect(await welcomeOf(host)).toBe(0);
    await nextLobby(host);

    host.ws.send(encodeLobbyCommand({ type: "bogus" } as never));
    host.ws.send(encodeLobbyCommand({ type: "tick" } as never));
    host.ws.send(encodeLobbyCommand({ type: "loadingTimeout" } as never));
    host.ws.send(encodeLobbyCommand({ type: "start" }));

    // The room survived, and the next push is the real start with a full countdown.
    const state = await nextLobby(host);
    expect(state.phase).toBe("countdown");
    expect(state.countdown).toBe(3);

    host.ws.close();
  });

  it("rejects a non-WebSocket request to a room", async () => {
    const response = await SELF.fetch("http://room.test/room/REL004");
    expect(response.status).toBe(426);
  });

  it("refuses one more player than MAX_PLAYERS in a full room", async () => {
    const conns: Conn[] = [];
    for (let i = 0; i < MAX_PLAYERS; i++) {
      conns.push(await joinRoom("FULL01"));
    }

    const overflow = await SELF.fetch("http://room.test/room/FULL01", {
      headers: { Upgrade: "websocket" },
    });
    expect(overflow.status).toBe(503);

    for (const conn of conns) {
      conn.ws.close();
    }
  });
});

describe("MatchRoom (lobby)", () => {
  it("broadcasts the lobby as players join, names them, and any player can start a countdown", async () => {
    const host = await joinRoom("LOB001", "Ada");
    expect(await welcomeOf(host)).toBe(0);
    let state = await nextLobby(host); // after the host joined
    expect(state.players).toEqual([{ slot: 0, name: "Ada", team: 0, ready: false, loaded: false }]);
    expect(state.hostSlot).toBe(0);
    expect(state.phase).toBe("waiting");

    const guest = await joinRoom("LOB001", "Bea");
    expect(await welcomeOf(guest)).toBe(1);
    state = await nextLobby(host); // the host is told the guest joined
    expect(state.players.map((p) => p.slot)).toEqual([0, 1]);
    expect(state.players[1].name).toBe("Bea");

    // The guest must ready up before the room can launch (the host's readiness is pressing Start).
    guest.ws.send(encodeLobbyCommand({ type: "setReady", ready: true }));
    state = await nextLobby(host);
    expect(state.players[1].ready).toBe(true);

    // The command's slot is stamped by the server from the sender, so the guest's start is its own.
    guest.ws.send(encodeLobbyCommand({ type: "start" }));
    state = await nextLobby(host);
    expect(state.phase).toBe("countdown");
    expect(state.countdown).toBe(3);

    host.ws.close();
    guest.ws.close();
  });

  it("appears in the lobby browser once joined, and withdraws when emptied", async () => {
    const host = await joinRoom("BRWS01", "Ada");
    expect(await welcomeOf(host)).toBe(0); // fetch awaits the join+publish before returning 101

    const listed = await SELF.fetch("http://room.test/lobbies");
    const open = (await listed.json()) as { code: string; mode: string; players: number }[];
    expect(open.find((l) => l.code === "BRWS01")).toMatchObject({ mode: "ffa", players: 1 });

    host.ws.close();
  });

  it("frees a slot when a player leaves so the next joiner reuses it", async () => {
    const a = await joinRoom("LOB002");
    const b = await joinRoom("LOB002");
    expect(await welcomeOf(a)).toBe(0);
    expect(await welcomeOf(b)).toBe(1);

    b.ws.close(); // slot 1 frees
    const c = await joinRoom("LOB002");
    expect(await welcomeOf(c)).toBe(1); // reuses the freed slot rather than jumping to 2

    a.ws.close();
    c.ws.close();
  });
});
