// MatchRoom — one lobby's room, as a Durable Object (one DO per lobby code). It plays two roles over
// the same socket (ADR-0019 + multiplayer milestone, web-export.md §5):
//   1. PRE-GAME LOBBY — it owns a LobbyState (up to 8 slots, FFA/Team, ready, host). Clients send
//      JSON `MSG_LOBBY_CMD`s; the DO stamps the sender's slot, runs the pure reducer, persists, and
//      pushes `MSG_LOBBY_STATE` to every member. Any player can start → a per-second countdown
//      (driven by the DO alarm) → phase "loading" (the loaded handshake) → phase "started".
//   2. IN-GAME RELAY — once started it is a PURE byte relay: the host (slot 0 / the lobby host)
//      broadcasts SnapshotFrames to guests; a guest sends InputFrames to the host. It never simulates.
// State survives hibernation: the per-socket slot via the socket attachment, the LobbyState via DO
// storage. The lobby-code routes and the request-budget alarm live elsewhere.

import {
  encodeWelcome,
  encodeLobbyState,
  decodeLobbyJson,
  MSG_LOBBY_CMD,
  MSG_INPUT,
  INPUT_FRAME_SIZE,
} from "./protocol/codec";
import {
  emptyLobby,
  reduce,
  MAX_PLAYERS,
  LOADING_TIMEOUT_SECONDS,
  type LobbyState,
  type LobbyCommand,
} from "./lobbyState";
import { publishToDirectory } from "./lobbyDirectory";

const LOBBY_KEY = "lobby";
const CODE_KEY = "code";
const TICK_MS = 1000;

interface Attachment {
  slot: number;
}

interface RoomEnv {
  LOBBY_KV: KVNamespace;
  LOBBY_DIRECTORY: DurableObjectNamespace;
}

export class MatchRoom implements DurableObject {
  private readonly state: DurableObjectState;
  private readonly env: RoomEnv;
  private lobby: LobbyState = emptyLobby();
  private code = ""; // this room's lobby code, learned from the first /room/:code request

  constructor(state: DurableObjectState, env: RoomEnv) {
    this.state = state;
    this.env = env;
    // Restore the lobby + code before any request/alarm runs (storage survives hibernation/eviction).
    this.state.blockConcurrencyWhile(async () => {
      this.lobby = (await this.state.storage.get<LobbyState>(LOBBY_KEY)) ?? emptyLobby();
      this.code = (await this.state.storage.get<string>(CODE_KEY)) ?? "";
    });
  }

  async fetch(request: Request): Promise<Response> {
    if (request.headers.get("Upgrade") !== "websocket") {
      return new Response("expected a websocket upgrade", { status: 426 });
    }
    if (this.lobby.phase !== "waiting") {
      // Only a waiting room is joinable: once it is counting down, loading, or started, the roster is
      // locked so no one arrives mid-launch.
      return new Response("match already launching", { status: 409 });
    }
    if (this.state.getWebSockets().length >= MAX_PLAYERS) {
      return new Response("room full", { status: 503 });
    }

    const url = new URL(request.url);
    if (this.code === "") {
      this.code = url.pathname.slice("/room/".length); // learn our code from the first request
      await this.state.storage.put(CODE_KEY, this.code);
    }

    const slot = this.assignSlot();
    const name = url.searchParams.get("name") ?? "Player";

    const pair = new WebSocketPair();
    const [client, server] = [pair[0], pair[1]];
    this.state.acceptWebSocket(server); // hibernation API
    server.serializeAttachment({ slot } satisfies Attachment); // survives hibernation
    server.send(encodeWelcome(slot)); // first message: tell the client which slot it drives

    await this.apply({ type: "join", slot, name }); // adds the player + broadcasts the new lobby

    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(sender: WebSocket, message: ArrayBuffer | string): Promise<void> {
    if (typeof message === "string") {
      return; // the protocol is binary; ignore anything else
    }

    const bytes = new Uint8Array(message);
    if (bytes[0] === MSG_LOBBY_CMD) {
      // A lobby command is consumed here, never relayed. The sender's real slot is stamped over
      // whatever the client sent, so a client can only ever act as itself.
      const command = decodeLobbyJson<LobbyCommand>(bytes);
      await this.apply({ ...command, slot: slotOf(sender) } as LobbyCommand);
      return;
    }

    // Same authority rule for game inputs: the sender's real slot is stamped over the frame's
    // last byte (placed there so no decode is needed), so the host attributes it correctly.
    if (bytes[0] === MSG_INPUT && bytes.length >= 1 + INPUT_FRAME_SIZE) {
      bytes[INPUT_FRAME_SIZE] = slotOf(sender);
    }

    for (const target of this.relayTargets(slotOf(sender))) {
      try {
        target.send(message);
      } catch {
        // target socket closing mid-relay — its close handler will clean it up
      }
    }
  }

  async webSocketClose(ws: WebSocket, code: number, _reason: string, _wasClean: boolean): Promise<void> {
    try {
      ws.close(code);
    } catch {
      // already closing
    }
    await this.apply({ type: "leave", slot: slotOf(ws) });
  }

  // The DO alarm drives the start countdown one second at a time, so every client ticks in lockstep
  // from the broadcast state. It reschedules itself until the countdown reaches "started".
  async alarm(): Promise<void> {
    await this.apply(this.lobby.phase === "loading" ? { type: "loadingTimeout" } : { type: "tick" });
  }

  // Run one command through the pure reducer, persist, push the new state to all members, and keep
  // the countdown alarm scheduled exactly while the lobby is counting down.
  private async apply(command: LobbyCommand): Promise<void> {
    const previousPhase = this.lobby.phase;
    this.lobby = reduce(this.lobby, command);
    await this.state.storage.put(LOBBY_KEY, this.lobby);
    this.broadcast();

    // Keep this room's entry in the lobby browser in step with its joinability.
    if (this.code !== "") {
      const directory = this.env.LOBBY_DIRECTORY.get(this.env.LOBBY_DIRECTORY.idFromName("global"));
      await publishToDirectory(directory, this.lobby, this.code);
    }

    if (this.lobby.phase === "countdown") {
      await this.state.storage.setAlarm(Date.now() + TICK_MS);
    } else if (this.lobby.phase === "loading" && previousPhase !== "loading") {
      await this.state.storage.setAlarm(Date.now() + LOADING_TIMEOUT_SECONDS * 1000);
    }
  }

  private broadcast(): void {
    const message = encodeLobbyState(this.lobby);
    for (const ws of this.state.getWebSockets()) {
      try {
        ws.send(message);
      } catch {
        // a socket closing mid-broadcast — its close handler cleans it up
      }
    }
  }

  // The lowest slot 0..MAX-1 not currently held by a live socket, so a mid-lobby leave frees its slot
  // for the next joiner instead of colliding (the 2-player relay never churned; an 8-slot lobby does).
  private assignSlot(): number {
    const taken = new Set(this.state.getWebSockets().map(slotOf));
    for (let slot = 0; slot < MAX_PLAYERS; slot++) {
      if (!taken.has(slot)) {
        return slot;
      }
    }
    return MAX_PLAYERS - 1; // unreachable: fetch already rejected a full room
  }

  // Where in-game bytes from a sender go: the host (the lobby's hostSlot) broadcasts to every guest;
  // a guest sends to the host only. The sender is always excluded.
  private relayTargets(senderSlot: number): WebSocket[] {
    const peers = this.state.getWebSockets().filter((ws) => slotOf(ws) !== senderSlot);
    if (senderSlot === this.lobby.hostSlot) {
      return peers; // host → every guest
    }
    return peers.filter((ws) => slotOf(ws) === this.lobby.hostSlot); // guest → host only
  }
}

function slotOf(ws: WebSocket): number {
  return (ws.deserializeAttachment() as Attachment | null)?.slot ?? 0;
}
