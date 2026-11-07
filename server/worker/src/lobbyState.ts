// The pre-game lobby state machine (multiplayer milestone, web-export.md §5). One MatchRoom DO owns a
// LobbyState; clients send LobbyCommands over a JSON control channel and the DO broadcasts the new
// state. Up to 8 players pick a mode (free-for-all or two teams), ready up, and any player can start
// the match — a short countdown, then a loading handshake, then a hand-off to the binary game relay.
// This module is the pure,
// engine-free heart of that flow so it is exhaustively unit-testable (the DO is a thin shell over it).

// 8 seats per room (owner ask 2026-07-08, raised from 4): the client room UI, the spawn table
// and the relay cap all size themselves off this one constant.
export const MAX_PLAYERS = 8;
export const COUNTDOWN_SECONDS = 3;
// A phone can take a while to build the arena, but a player who never reports in (a tab sent to the
// background keeps its socket open) must not hold everyone else on the loading banner forever.
export const LOADING_TIMEOUT_SECONDS = 45;

// 1, not 2: pressing Start fills every empty seat with an AI tank (owner ask), so a lone player
// starting a room is a real match, not a stall.
export const MIN_PLAYERS_TO_START = 1;

export type GameMode = "ffa" | "team";
// countdown → loading → started: after the countdown the room waits for every seated player to report
// its arena is built (the "loaded" handshake) before the match truly starts, so no one is mid-load
// when the first snapshot arrives. Empty seats are AI-filled and never connect, so only seated players
// gate the loading→started transition.
export type LobbyPhase = "waiting" | "countdown" | "loading" | "started";

export interface Player {
  slot: number;
  name: string;
  team: number; // FFA: team === slot (everyone their own); Team: 0 or 1
  ready: boolean;
  loaded: boolean; // has this player's arena finished building? (the loading-phase handshake)
}

export interface LobbyState {
  mode: GameMode;
  phase: LobbyPhase;
  hostSlot: number;
  countdown: number; // seconds left while phase === "countdown"; 0 otherwise
  map: string; // map id the host picked; "" = random, resolved by the host client at launch
  seed: number; // match seed stamped at the waiting→countdown transition; feeds spawn shuffle + AI
  players: Player[]; // always sorted by slot
}

export type LobbyCommand =
  | { type: "join"; slot: number; name: string }
  | { type: "leave"; slot: number }
  | { type: "setName"; slot: number; name: string }
  | { type: "setReady"; slot: number; ready: boolean }
  | { type: "setTeam"; slot: number; team: number }
  | { type: "setMode"; slot: number; mode: GameMode }
  | { type: "setMap"; slot: number; map: string }
  | { type: "start"; slot: number }
  | { type: "rematch"; slot: number }
  | { type: "loaded"; slot: number }
  | { type: "loadingTimeout" }
  | { type: "tick" };

export function emptyLobby(mode: GameMode = "ffa", map = ""): LobbyState {
  return { mode, phase: "waiting", hostSlot: 0, countdown: 0, map, seed: 0, players: [] };
}

export function reduce(state: LobbyState, command: LobbyCommand): LobbyState {
  switch (command.type) {
    case "join":
      return join(state, command.slot, command.name);
    case "leave":
      return leave(state, command.slot);
    case "setName":
      return editPlayer(state, command.slot, (p) => ({ ...p, name: cleanName(command.name) }));
    case "setReady":
      return editPlayer(state, command.slot, (p) => ({ ...p, ready: command.ready }));
    case "setTeam":
      return state.mode === "team"
        ? editPlayer(state, command.slot, (p) => ({ ...p, team: clampTeam(command.team) }))
        : state;
    case "setMode":
      return setMode(state, command.slot, command.mode);
    case "setMap":
      return setMap(state, command.slot, command.map);
    case "start":
      return start(state, command.slot);
    case "rematch":
      return rematch(state, command.slot);
    case "loaded":
      return markLoaded(state, command.slot);
    case "loadingTimeout":
      return state.phase === "loading" ? { ...state, phase: "started" } : state;
    case "tick":
      return tick(state);
    default:
      return state; // the wire can carry anything; an unknown command must never lose the room state
  }
}

function join(state: LobbyState, slot: number, name: string): LobbyState {
  if (state.phase !== "waiting" || state.players.length >= MAX_PLAYERS || hasSlot(state, slot)) {
    return state;
  }

  const firstIn = state.players.length === 0;
  const team = state.mode === "ffa" ? slot : smallerTeam(state.players);
  const players = sortBySlot([...state.players, { slot, name: cleanName(name), team, ready: false, loaded: false }]);
  return { ...state, players, hostSlot: firstIn ? slot : state.hostSlot };
}

function leave(state: LobbyState, slot: number): LobbyState {
  if (!hasSlot(state, slot)) {
    return state;
  }

  const players = state.players.filter((p) => p.slot !== slot);
  if (players.length === 0) {
    return emptyLobby(state.mode); // the room empties back to a fresh waiting lobby
  }

  const hostSlot = slot === state.hostSlot ? players[0].slot : state.hostSlot;
  // A departure that drops below the start threshold aborts an in-progress launch (countdown or the
  // loading handshake), sending the room back to waiting with the load flags + seed cleared.
  const aborts =
    (state.phase === "countdown" || state.phase === "loading") && players.length < MIN_PLAYERS_TO_START;
  // If the player who left was the last one still loading, everyone remaining is loaded — start now
  // rather than hang forever waiting for a "loaded" command that will never arrive.
  const finishesLoading = !aborts && state.phase === "loading" && players.every((p) => p.loaded);
  return {
    ...state,
    players: aborts ? players.map((p) => ({ ...p, loaded: false })) : players,
    hostSlot,
    phase: aborts ? "waiting" : finishesLoading ? "started" : state.phase,
    countdown: aborts ? 0 : state.countdown,
    seed: aborts ? 0 : state.seed,
  };
}

function setMode(state: LobbyState, slot: number, mode: GameMode): LobbyState {
  if (slot !== state.hostSlot || state.phase !== "waiting") {
    return state; // only the host re-picks the mode, and only before the match starts
  }

  // Re-derive teams for the new mode: FFA gives everyone their own team (= slot); Team splits the
  // roster as evenly as possible by join order.
  const players = state.players.map((p, i) => ({ ...p, team: mode === "ffa" ? p.slot : i % 2 }));
  return { ...state, mode, players };
}

function setMap(state: LobbyState, slot: number, map: string): LobbyState {
  if (slot !== state.hostSlot || state.phase !== "waiting") {
    return state; // like the mode: only the host picks the map, and only before the match starts
  }
  return { ...state, map: map.trim().slice(0, 64) };
}

function start(state: LobbyState, slot: number): LobbyState {
  if (!hasSlot(state, slot) || state.phase !== "waiting" || state.players.length < MIN_PLAYERS_TO_START) {
    return state;
  }
  // Every guest must have readied up first; the host pressing Start is their own readiness, so a lone
  // host still launches instantly (owner ask). Empty seats are AI-filled at launch and never gate it.
  if (state.players.some((p) => p.slot !== state.hostSlot && !p.ready)) {
    return state;
  }
  // Stamp the match seed here so it broadcasts through countdown/loading/started to every client.
  const seed = (Math.random() * 0x80000000) | 0;
  return { ...state, phase: "countdown", countdown: COUNTDOWN_SECONDS, seed };
}

// Online rematch: the host resets a finished match back to a waiting lobby — same seats, mode and
// map, ready/loaded flags and seed cleared — so the room runs the normal start flow again. Guests
// that disconnected mid-match already left the roster via their close-handler's "leave".
function rematch(state: LobbyState, slot: number): LobbyState {
  if (state.phase !== "started" || slot !== state.hostSlot) {
    return state;
  }
  return {
    ...state,
    phase: "waiting",
    countdown: 0,
    seed: 0,
    players: state.players.map((p) => ({ ...p, ready: false, loaded: false })),
  };
}

// The loading handshake: a seated player reports its arena is built. Once EVERY seated player has
// loaded, the match truly starts. (Empty seats are AI-filled and never connect, so we gate on the
// seated players, not MAX_PLAYERS.)
function markLoaded(state: LobbyState, slot: number): LobbyState {
  if (state.phase !== "loading" || !hasSlot(state, slot)) {
    return state;
  }
  const players = state.players.map((p) => (p.slot === slot ? { ...p, loaded: true } : p));
  const allLoaded = players.every((p) => p.loaded);
  return { ...state, players, phase: allLoaded ? "started" : "loading" };
}

function tick(state: LobbyState): LobbyState {
  if (state.phase !== "countdown") {
    return state;
  }
  const countdown = state.countdown - 1;
  // Countdown ends in "loading", NOT "started": the room now waits for the loaded handshake.
  return countdown <= 0
    ? { ...state, phase: "loading", countdown: 0 }
    : { ...state, countdown };
}

function editPlayer(state: LobbyState, slot: number, edit: (p: Player) => Player): LobbyState {
  if (state.phase !== "waiting" || !hasSlot(state, slot)) {
    return state;
  }
  return { ...state, players: state.players.map((p) => (p.slot === slot ? edit(p) : p)) };
}

function hasSlot(state: LobbyState, slot: number): boolean {
  return state.players.some((p) => p.slot === slot);
}

function smallerTeam(players: Player[]): number {
  const onTeamZero = players.filter((p) => p.team === 0).length;
  const onTeamOne = players.filter((p) => p.team === 1).length;
  return onTeamZero <= onTeamOne ? 0 : 1;
}

function clampTeam(team: number): number {
  return team === 1 ? 1 : 0;
}

function cleanName(name: string): string {
  const trimmed = name.trim().slice(0, 16);
  return trimmed.length > 0 ? trimmed : "Player";
}

function sortBySlot(players: Player[]): Player[] {
  return [...players].sort((a, b) => a.slot - b.slot);
}
