/**
 * Vesta Chess — browser entry point.
 *
 * Channels:
 *   - `chess/lobby` (public): presence + invitations
 *       - app.chess.presence  { username }        (volatile, ttlSeconds=60)
 *       - app.chess.invite    { matchId, toClientId, fromUsername, toUsername }
 *       - app.chess.invite-accepted { matchId, toClientId }
 *       - app.chess.invite-declined { matchId, toClientId }
 *
 *   - `chess/match/{matchId}` (private, both players invited at create):
 *       - app.chess.match-started { whiteClientId, blackClientId, whiteName, blackName }
 *       - app.chess.move          { from, to, promotion, fen }
 *       - app.chess.resign        { byClientId }
 */

import { Chess, type Square } from "chess.js";
import {
  createEvent,
  VestaConnection,
  type EventMessage,
  type EventsBatchMessage,
  type SequencedEvent,
  type VestaSocket,
  type WelcomeMessage,
} from "vesta-client";

import { BoardView } from "./board.js";
import { loadOrCreateBrowserIdentity, loadUsername, saveUsername } from "./identity.js";

const LOBBY = "chess/lobby";
const PRESENCE_TTL_SEC = 60;
const PRESENCE_REPUBLISH_MS = 25_000;

// ─── DOM refs ──────────────────────────────────────────────────────────────

const $ = <T extends HTMLElement = HTMLElement>(id: string): T =>
  document.getElementById(id) as T;

const connStatusEl = $("conn-status");
const meEl = $("me");
const lobbyView = $("lobby-view");
const matchView = $("match-view");
const playersListEl = $<HTMLUListElement>("players-list");
const invitesListEl = $<HTMLUListElement>("invites-list");
const activeListEl = $<HTMLUListElement>("active-list");
const matchTitleEl = $("match-title");
const matchTurnEl = $("match-turn");
const matchMessageEl = $("match-message");
const boardEl = $("board");
const leaveBtn = $<HTMLButtonElement>("leave-match");
const resignBtn = $<HTMLButtonElement>("resign-btn");
const serverInput = $<HTMLInputElement>("server-url");
const usernameInput = $<HTMLInputElement>("username");
const connectBtn = $<HTMLButtonElement>("connect-btn");

// ─── State ─────────────────────────────────────────────────────────────────

const identity = loadOrCreateBrowserIdentity();
meEl.textContent = `${identity.clientId.slice(0, 10)}…`;
usernameInput.value = loadUsername() || `player-${identity.clientId.slice(0, 6)}`;

interface PresentPlayer {
  clientId: string;
  username: string;
  lastSeen: number;
}

interface Invitation {
  matchId: string;
  fromClientId: string;
  fromUsername: string;
}

interface ActiveMatch {
  matchId: string;
  opponentClientId: string;
  opponentName: string;
  myColor: "w" | "b";
  chess: Chess;
  board?: BoardView;
  /** True until the second player has acknowledged via match-started. */
  isStarted: boolean;
  resignedBy?: string;
}

const presence = new Map<string, PresentPlayer>();
const invitations = new Map<string, Invitation>();
const activeMatches = new Map<string, ActiveMatch>();
let currentMatchId: string | null = null;
let connection: VestaConnection | null = null;
let username: string = usernameInput.value;
let presenceTimer: number | null = null;

// ─── Wire helpers ──────────────────────────────────────────────────────────

function matchChannel(matchId: string): string {
  return `chess/match/${matchId}`;
}

function publishLobby(type: string, payload: object, opts?: { volatile?: boolean; ttlSeconds?: number }): void {
  if (!connection?.isConnected) return;
  const options: { metadata?: Record<string, unknown> } & Record<string, unknown> = {};
  if (opts?.volatile || opts?.ttlSeconds) {
    options.metadata = { ttlSeconds: opts.ttlSeconds ?? PRESENCE_TTL_SEC };
  }
  const event = createEvent(LOBBY, identity, type, payload, options);
  if (opts?.volatile) {
    (event as { volatile?: boolean }).volatile = true;
  }
  connection.publish(event);
}

function publishMatch(matchId: string, type: string, payload: object): void {
  if (!connection?.isConnected) return;
  const event = createEvent(matchChannel(matchId), identity, type, payload);
  connection.publish(event);
}

// ─── Presence ──────────────────────────────────────────────────────────────

function broadcastPresence(): void {
  publishLobby("app.chess.presence", { username }, { volatile: true, ttlSeconds: PRESENCE_TTL_SEC });
}

function pruneStalePresence(): void {
  const now = Date.now();
  let changed = false;
  for (const [id, p] of presence) {
    if (now - p.lastSeen > PRESENCE_TTL_SEC * 1000) {
      presence.delete(id);
      changed = true;
    }
  }
  if (changed) renderLobby();
}

// ─── Rendering ─────────────────────────────────────────────────────────────

function renderLobby(): void {
  playersListEl.innerHTML = "";
  const players = [...presence.values()]
    .filter((p) => p.clientId !== identity.clientId)
    .sort((a, b) => a.username.localeCompare(b.username));

  if (players.length === 0) {
    const li = document.createElement("li");
    li.innerHTML = `<span class="meta">No other players online.</span>`;
    playersListEl.appendChild(li);
  }

  for (const p of players) {
    const li = document.createElement("li");
    li.innerHTML = `
      <span><strong>${escape(p.username)}</strong> <span class="meta">${p.clientId.slice(0, 8)}…</span></span>
      <span class="actions"><button data-action="invite" data-client="${p.clientId}" data-name="${escape(p.username)}">Invite</button></span>
    `;
    playersListEl.appendChild(li);
  }

  // Invitations
  invitesListEl.innerHTML = "";
  if (invitations.size === 0) {
    const li = document.createElement("li");
    li.innerHTML = `<span class="meta">No pending invitations.</span>`;
    invitesListEl.appendChild(li);
  }
  for (const inv of invitations.values()) {
    const li = document.createElement("li");
    li.innerHTML = `
      <span><strong>${escape(inv.fromUsername)}</strong> invited you <span class="meta">(${inv.matchId.slice(0, 8)}…)</span></span>
      <span class="actions">
        <button data-action="accept" data-match="${inv.matchId}">Accept</button>
        <button class="decline" data-action="decline" data-match="${inv.matchId}">Decline</button>
      </span>
    `;
    invitesListEl.appendChild(li);
  }

  // Active matches
  activeListEl.innerHTML = "";
  if (activeMatches.size === 0) {
    const li = document.createElement("li");
    li.innerHTML = `<span class="meta">No active matches.</span>`;
    activeListEl.appendChild(li);
  }
  for (const m of activeMatches.values()) {
    const li = document.createElement("li");
    const status = m.resignedBy
      ? "Resigned"
      : m.isStarted
        ? (m.chess.isGameOver() ? "Finished" : `Your color: ${m.myColor === "w" ? "White" : "Black"}`)
        : "Waiting for opponent…";
    li.innerHTML = `
      <span><strong>vs ${escape(m.opponentName)}</strong> <span class="meta">${status}</span></span>
      <span class="actions"><button data-action="open" data-match="${m.matchId}">Open</button></span>
    `;
    activeListEl.appendChild(li);
  }
}

function renderMatch(): void {
  if (!currentMatchId) return;
  const m = activeMatches.get(currentMatchId);
  if (!m) return;

  matchTitleEl.textContent = `vs ${m.opponentName} — ${m.myColor === "w" ? "White" : "Black"}`;

  if (!m.board) {
    m.board = new BoardView(boardEl, {
      orientation: m.myColor,
      onMove: (from, to, promotion) => onLocalMove(m, from, to, promotion),
      isMyTurn: () => m.isStarted && !m.chess.isGameOver() && m.chess.turn() === m.myColor && !m.resignedBy,
    });
  }
  m.board.setFen(m.chess.fen());

  if (m.resignedBy) {
    matchTurnEl.textContent = "";
    matchMessageEl.textContent = m.resignedBy === identity.clientId ? "You resigned." : `${m.opponentName} resigned. You win.`;
    resignBtn.disabled = true;
  } else if (m.chess.isGameOver()) {
    matchTurnEl.textContent = "";
    matchMessageEl.textContent = m.board.statusText;
    resignBtn.disabled = true;
  } else {
    matchTurnEl.textContent = m.board.statusText;
    matchMessageEl.textContent = m.isStarted ? "" : "Waiting for opponent to join…";
    resignBtn.disabled = !m.isStarted;
  }
}

function escape(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" })[c]!);
}

function showView(view: "lobby" | "match"): void {
  lobbyView.classList.toggle("hidden", view !== "lobby");
  matchView.classList.toggle("hidden", view !== "match");
}

// ─── Local actions ─────────────────────────────────────────────────────────

function onLocalMove(m: ActiveMatch, from: Square, to: Square, promotion?: "q" | "r" | "b" | "n"): boolean {
  if (m.chess.turn() !== m.myColor) return false;
  // Trial-apply on a clone to compute resulting FEN; the BoardView will apply for real on success.
  const probe = new Chess(m.chess.fen());
  const result = probe.move({ from, to, promotion: promotion ?? "q" });
  if (!result) return false;

  // Mirror into our authoritative state and broadcast.
  m.chess.move({ from, to, promotion: promotion ?? "q" });
  publishMatch(m.matchId, "app.chess.move", { from, to, promotion: promotion ?? "q", fen: m.chess.fen() });

  // Re-render so turn indicator updates.
  setTimeout(() => renderMatch(), 0);
  return true;
}

function invitePlayer(toClientId: string, toName: string): void {
  if (!connection?.isConnected) return;

  const matchId = crypto.randomUUID();
  const channel = matchChannel(matchId);

  // Pre-create the private channel with the invitee included as a member.
  connection.createChannel(channel, { visibility: "private", members: [toClientId] });

  // Track as active (waiting for acceptance). Inviter is White by default.
  activeMatches.set(matchId, {
    matchId,
    opponentClientId: toClientId,
    opponentName: toName,
    myColor: "w",
    chess: new Chess(),
    isStarted: false,
  });

  // Announce the invite on the lobby.
  publishLobby("app.chess.invite", {
    matchId,
    toClientId,
    fromUsername: username,
    toUsername: toName,
  });

  renderLobby();
}

function acceptInvite(matchId: string): void {
  const inv = invitations.get(matchId);
  if (!inv || !connection) return;
  invitations.delete(matchId);

  // Subscribe to the (already-created) private match channel.
  connection.subscribe(matchChannel(matchId));

  // Track as active. Invitee is Black.
  activeMatches.set(matchId, {
    matchId,
    opponentClientId: inv.fromClientId,
    opponentName: inv.fromUsername,
    myColor: "b",
    chess: new Chess(),
    isStarted: true, // From our side, we're ready; inviter will mark started on receiving this.
  });

  publishLobby("app.chess.invite-accepted", { matchId, toClientId: inv.fromClientId });
  // Send match-started on the private channel so the inviter knows the game is live.
  publishMatch(matchId, "app.chess.match-started", {
    whiteClientId: inv.fromClientId,
    blackClientId: identity.clientId,
    whiteName: inv.fromUsername,
    blackName: username,
  });

  openMatch(matchId);
}

function declineInvite(matchId: string): void {
  const inv = invitations.get(matchId);
  if (!inv) return;
  invitations.delete(matchId);
  publishLobby("app.chess.invite-declined", { matchId, toClientId: inv.fromClientId });
  renderLobby();
}

function openMatch(matchId: string): void {
  currentMatchId = matchId;
  const m = activeMatches.get(matchId);
  if (m) m.board = undefined; // force re-create in fresh DOM
  showView("match");
  renderMatch();
}

function leaveMatch(): void {
  currentMatchId = null;
  showView("lobby");
  renderLobby();
}

function resign(): void {
  if (!currentMatchId) return;
  const m = activeMatches.get(currentMatchId);
  if (!m || m.resignedBy) return;
  m.resignedBy = identity.clientId;
  publishMatch(m.matchId, "app.chess.resign", { byClientId: identity.clientId });
  renderMatch();
}

// ─── Inbound events ────────────────────────────────────────────────────────

function handleLobbyEvent(ev: SequencedEvent["event"] | EventMessage["event"]): void {
  const payload = ev.payload as Record<string, unknown>;
  const fromClientId = ev.clientId ?? "";

  switch (ev.eventType) {
    case "app.chess.presence": {
      const uname = String(payload.username ?? "anon");
      presence.set(fromClientId, { clientId: fromClientId, username: uname, lastSeen: Date.now() });
      renderLobby();
      break;
    }
    case "app.chess.invite": {
      const matchId = String(payload.matchId);
      const toClientId = String(payload.toClientId);
      if (toClientId !== identity.clientId) return;
      if (invitations.has(matchId) || activeMatches.has(matchId)) return;
      invitations.set(matchId, {
        matchId,
        fromClientId,
        fromUsername: String(payload.fromUsername ?? "?"),
      });
      renderLobby();
      break;
    }
    case "app.chess.invite-accepted": {
      const matchId = String(payload.matchId);
      const toClientId = String(payload.toClientId);
      // Inviter sees acceptance for their own invitation.
      if (toClientId !== identity.clientId) return;
      const m = activeMatches.get(matchId);
      if (m && !m.isStarted) {
        m.isStarted = true;
        renderLobby();
        // Send match-started from the inviter side too (white).
        publishMatch(matchId, "app.chess.match-started", {
          whiteClientId: identity.clientId,
          blackClientId: m.opponentClientId,
          whiteName: username,
          blackName: m.opponentName,
        });
      }
      break;
    }
    case "app.chess.invite-declined": {
      const matchId = String(payload.matchId);
      const toClientId = String(payload.toClientId);
      if (toClientId !== identity.clientId) return;
      activeMatches.delete(matchId);
      renderLobby();
      break;
    }
  }
}

function handleMatchEvent(channelId: string, ev: SequencedEvent["event"] | EventMessage["event"]): void {
  const matchId = channelId.slice("chess/match/".length);
  const m = activeMatches.get(matchId);
  const payload = ev.payload as Record<string, unknown>;

  switch (ev.eventType) {
    case "app.chess.match-started": {
      // First-time observation (in case we only learned about this match by being added to the channel).
      if (!m) {
        const myColor: "w" | "b" = payload.whiteClientId === identity.clientId ? "w" : "b";
        const opponentClientId = myColor === "w" ? String(payload.blackClientId) : String(payload.whiteClientId);
        const opponentName = myColor === "w" ? String(payload.blackName) : String(payload.whiteName);
        activeMatches.set(matchId, {
          matchId,
          opponentClientId,
          opponentName,
          myColor,
          chess: new Chess(),
          isStarted: true,
        });
      } else {
        m.isStarted = true;
      }
      if (currentMatchId === matchId) renderMatch();
      renderLobby();
      break;
    }
    case "app.chess.move": {
      if (!m) return;
      if (ev.clientId === identity.clientId) return; // own move already applied
      const from = String(payload.from) as Square;
      const to = String(payload.to) as Square;
      const promotion = payload.promotion as "q" | "r" | "b" | "n" | undefined;
      const ok = m.chess.move({ from, to, promotion: promotion ?? "q" });
      if (!ok && payload.fen) {
        // Fall back to authoritative FEN if our local model diverged.
        m.chess.load(String(payload.fen));
      }
      if (currentMatchId === matchId) {
        m.board?.applyMove(from, to, promotion);
        renderMatch();
      }
      break;
    }
    case "app.chess.resign": {
      if (!m) return;
      m.resignedBy = String(payload.byClientId);
      if (currentMatchId === matchId) renderMatch();
      renderLobby();
      break;
    }
  }
}

// ─── Connection setup ──────────────────────────────────────────────────────

async function connect(): Promise<void> {
  if (connection) {
    await connection.disconnect();
    connection = null;
  }
  username = usernameInput.value.trim() || `player-${identity.clientId.slice(0, 6)}`;
  saveUsername(username);

  connection = new VestaConnection({
    serverUrl: serverInput.value,
    clientId: identity.clientId,
    publicKey: identity.publicKeyB64,
    channels: [LOBBY],
    createSocket: (url) => new WebSocket(url) as unknown as VestaSocket,
    autoReconnect: true,
  });

  connection.on("connected", (welcome: WelcomeMessage) => {
    connStatusEl.textContent = "online";
    connStatusEl.classList.replace("offline", "online");
    void welcome;
    broadcastPresence();
    if (presenceTimer) window.clearInterval(presenceTimer);
    presenceTimer = window.setInterval(() => {
      broadcastPresence();
      pruneStalePresence();
    }, PRESENCE_REPUBLISH_MS);
  });

  connection.on("disconnected", () => {
    connStatusEl.textContent = "offline";
    connStatusEl.classList.replace("online", "offline");
  });

  connection.on("event", (msg: EventMessage) => {
    if (msg.channelId === LOBBY) handleLobbyEvent(msg.event);
    else if (msg.channelId.startsWith("chess/match/")) handleMatchEvent(msg.channelId, msg.event);
  });

  connection.on("eventsBatch", (msg: EventsBatchMessage) => {
    for (const se of msg.events) {
      if (msg.channelId === LOBBY) handleLobbyEvent(se.event);
      else if (msg.channelId.startsWith("chess/match/")) handleMatchEvent(msg.channelId, se.event);
    }
  });

  await connection.connect();
}

// ─── UI wiring ─────────────────────────────────────────────────────────────

connectBtn.addEventListener("click", () => {
  connect().catch((err) => {
    console.error(err);
    matchMessageEl.textContent = `Connection failed: ${err.message ?? err}`;
  });
});

leaveBtn.addEventListener("click", leaveMatch);
resignBtn.addEventListener("click", resign);

playersListEl.addEventListener("click", (e) => {
  const btn = (e.target as HTMLElement).closest("button[data-action]") as HTMLButtonElement | null;
  if (!btn) return;
  if (btn.dataset.action === "invite") {
    invitePlayer(btn.dataset.client!, btn.dataset.name ?? "?");
  }
});

invitesListEl.addEventListener("click", (e) => {
  const btn = (e.target as HTMLElement).closest("button[data-action]") as HTMLButtonElement | null;
  if (!btn) return;
  if (btn.dataset.action === "accept") acceptInvite(btn.dataset.match!);
  else if (btn.dataset.action === "decline") declineInvite(btn.dataset.match!);
});

activeListEl.addEventListener("click", (e) => {
  const btn = (e.target as HTMLElement).closest("button[data-action]") as HTMLButtonElement | null;
  if (!btn) return;
  if (btn.dataset.action === "open") openMatch(btn.dataset.match!);
});

// Initial render
renderLobby();
showView("lobby");
