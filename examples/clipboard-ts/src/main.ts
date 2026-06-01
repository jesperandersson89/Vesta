/**
 * Vesta Shared Clipboard
 * ──────────────────────
 * A CLI tool that syncs your system clipboard across machines via Vesta.
 *
 * Event schema:
 *   Channel:   clipboard/{room}
 *   Event:     app.clipboard.update
 *   Payload:   { "text": "...", "username": "..." }
 *   Replace:   true (server keeps only the latest per user)
 *
 * Run:  npx tsx src/main.ts [ws://host:port/ws] [room-name]
 */

import { randomUUID } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { createInterface } from "node:readline";
import clipboardy from "clipboardy";
import WebSocket from "ws";

// ── Configuration ────────────────────────────────────────────────────────────
const DEFAULT_SERVER = "ws://localhost:5150/ws";
const DEFAULT_ROOM = "main";
const POLL_INTERVAL_MS = 300;
const RECONNECT_INITIAL_MS = 1000;
const RECONNECT_MAX_MS = 30000;

const VESTA_DIR = join(homedir(), ".vesta");
mkdirSync(VESTA_DIR, { recursive: true });

// ── Identity ─────────────────────────────────────────────────────────────────
interface Identity {
  clientId: string;
  username: string;
}

function loadOrCreateIdentity(username: string, room: string): Identity {
  const path = join(VESTA_DIR, `clipboard-${room}-${username}-identity.json`);
  if (existsSync(path)) {
    const data = JSON.parse(readFileSync(path, "utf-8"));
    return { clientId: data.clientId, username };
  }
  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
  let clientId = "";
  for (let i = 0; i < 22; i++) {
    clientId += chars[Math.floor(Math.random() * chars.length)];
  }
  writeFileSync(path, JSON.stringify({ clientId, username }, null, 2));
  return { clientId, username };
}

// ── Protocol helpers ─────────────────────────────────────────────────────────
function makeHello(clientId: string, channel: string): string {
  return JSON.stringify({
    type: "HELLO",
    clientId,
    channels: [channel],
    lastSequences: { [channel]: 0 },
  });
}

function makePublish(
  clientId: string,
  channel: string,
  text: string,
  username: string
): string {
  return JSON.stringify({
    type: "PUBLISH",
    channelId: channel,
    event: {
      id: randomUUID(),
      channelId: channel,
      timestamp: new Date().toISOString(),
      clientId,
      eventType: "app.clipboard.update",
      payload: { text, username },
      replace: true,
    },
  });
}

// ── State ────────────────────────────────────────────────────────────────────
interface ClipboardEntry {
  clientId: string;
  username: string;
  text: string;
  timestamp: string;
}

class ClipboardState {
  private entries = new Map<string, ClipboardEntry>();

  apply(event: any): boolean {
    if (event.eventType !== "app.clipboard.update") return false;
    const clientId: string = event.clientId ?? "";
    const ts: string = event.timestamp ?? "";
    const payload = event.payload ?? {};
    const text: string = payload.text ?? "";
    const username: string = payload.username ?? clientId.slice(0, 8);

    const existing = this.entries.get(clientId);
    if (existing && existing.timestamp >= ts) return false;

    this.entries.set(clientId, { clientId, username, text, timestamp: ts });
    return true;
  }

  getLatest(): ClipboardEntry | undefined {
    let latest: ClipboardEntry | undefined;
    for (const entry of this.entries.values()) {
      if (!latest || entry.timestamp > latest.timestamp) {
        latest = entry;
      }
    }
    return latest;
  }

  getAll(): ClipboardEntry[] {
    return [...this.entries.values()].sort((a, b) =>
      b.timestamp.localeCompare(a.timestamp)
    );
  }
}

// ── Display ──────────────────────────────────────────────────────────────────
const RESET = "\x1b[0m";
const BOLD = "\x1b[1m";
const DIM = "\x1b[2m";
const CYAN = "\x1b[36m";
const GREEN = "\x1b[32m";
const YELLOW = "\x1b[33m";

function clearScreen(): void {
  process.stdout.write("\x1b[2J\x1b[H");
}

function renderUI(
  state: ClipboardState,
  selfClientId: string,
  connected: boolean,
  serverUrl: string,
  channel: string,
  localClipboard: string
): void {
  clearScreen();

  const status = connected
    ? `${GREEN}●${RESET} Connected to ${serverUrl}`
    : `${YELLOW}○${RESET} Disconnected — retrying…`;

  console.log(`${BOLD}Vesta Shared Clipboard${RESET}  ${DIM}[${channel}]${RESET}`);
  console.log(status);
  console.log(`${DIM}${"─".repeat(60)}${RESET}`);
  console.log();

  const entries = state.getAll();
  if (entries.length === 0) {
    console.log(`${DIM}  No clipboard entries yet. Copy something!${RESET}`);
  } else {
    console.log(`${BOLD}  Recent clips:${RESET}`);
    console.log();
    for (const entry of entries.slice(0, 8)) {
      const isSelf = entry.clientId === selfClientId;
      const who = isSelf
        ? `${CYAN}${entry.username} (you)${RESET}`
        : `${entry.username}`;
      const preview = entry.text.length > 50
        ? entry.text.slice(0, 50) + "…"
        : entry.text;
      const displayText = preview.replace(/\n/g, "↵");
      const time = new Date(entry.timestamp).toLocaleTimeString();
      console.log(`  ${who} ${DIM}${time}${RESET}`);
      console.log(`    ${displayText}`);
      console.log();
    }
  }

  console.log(`${DIM}${"─".repeat(60)}${RESET}`);
  console.log(`${DIM}  Local clipboard: ${localClipboard.length > 40 ? localClipboard.slice(0, 40) + "…" : localClipboard}${RESET}`);
  console.log(`${DIM}  Watching for changes… (Ctrl+C to exit)${RESET}`);
}

// ── Main ─────────────────────────────────────────────────────────────────────
async function main(): Promise<void> {
  const args = process.argv.slice(2);
  const serverUrl = args[0] ?? DEFAULT_SERVER;
  const room = args[1] ?? DEFAULT_ROOM;
  const channel = `clipboard/${room}`;

  // Prompt for username
  const maybeUsername = await promptUsername();
  if (!maybeUsername) {
    process.exit(0);
  }
  const username: string = maybeUsername;

  const identity = loadOrCreateIdentity(username, room);
  const state = new ClipboardState();
  let connected = false;
  let ws: WebSocket | null = null;
  let lastClipboard = "";

  try {
    lastClipboard = await clipboardy.read();
  } catch {
    // Clipboard might be empty or inaccessible
  }

  // Apply initial clipboard as our own entry
  state.apply({
    clientId: identity.clientId,
    eventType: "app.clipboard.update",
    timestamp: new Date().toISOString(),
    payload: { text: lastClipboard, username },
  });

  function redraw(): void {
    renderUI(state, identity.clientId, connected, serverUrl, channel, lastClipboard);
  }

  // ── WebSocket connection with auto-reconnect ─────────────────────────────
  let backoff = RECONNECT_INITIAL_MS;

  function connect(): void {
    ws = new WebSocket(serverUrl);

    ws.on("open", () => {
      ws!.send(makeHello(identity.clientId, channel));
    });

    ws.on("message", (data) => {
      const msg = JSON.parse(data.toString());

      if (msg.type === "WELCOME") {
        connected = true;
        backoff = RECONNECT_INITIAL_MS;

        // Publish our current clipboard on connect
        if (lastClipboard) {
          ws!.send(makePublish(identity.clientId, channel, lastClipboard, username));
        }
        redraw();
        return;
      }

      const events = eventsFromMessage(msg);
      let changed = false;
      for (const evt of events) {
        if (evt.clientId === identity.clientId) continue; // Skip own echoes
        if (state.apply(evt)) {
          changed = true;
          // Write the latest entry to our clipboard
          const latest = state.getLatest();
          if (latest && latest.clientId !== identity.clientId) {
            lastClipboard = latest.text;
            clipboardy.writeSync(latest.text);
          }
        }
      }
      if (changed) redraw();
    });

    ws.on("close", () => {
      connected = false;
      ws = null;
      redraw();
      setTimeout(connect, backoff);
      backoff = Math.min(backoff * 2, RECONNECT_MAX_MS);
    });

    ws.on("error", () => {
      // Will trigger 'close'
    });
  }

  function eventsFromMessage(msg: any): any[] {
    if (msg.type === "EVENT") return [msg.event];
    if (msg.type === "EVENTS_BATCH") {
      return (msg.events ?? []).map((se: any) => se.event);
    }
    return [];
  }

  // ── Clipboard polling ────────────────────────────────────────────────────
  setInterval(async () => {
    try {
      const current = await clipboardy.read();
      if (current && current !== lastClipboard) {
        lastClipboard = current;

        // Apply locally
        state.apply({
          clientId: identity.clientId,
          eventType: "app.clipboard.update",
          timestamp: new Date().toISOString(),
          payload: { text: current, username },
        });

        // Publish to server
        if (connected && ws?.readyState === WebSocket.OPEN) {
          ws.send(makePublish(identity.clientId, channel, current, username));
        }

        redraw();
      }
    } catch {
      // Clipboard read can fail transiently
    }
  }, POLL_INTERVAL_MS);

  connect();
  redraw();

  // Keep process alive
  process.on("SIGINT", () => {
    console.log("\n  Goodbye!");
    process.exit(0);
  });
}

function promptUsername(): Promise<string | null> {
  return new Promise((resolve) => {
    const rl = createInterface({ input: process.stdin, output: process.stdout });
    rl.question("Enter your display name: ", (answer) => {
      rl.close();
      resolve(answer.trim() || null);
    });
  });
}

main().catch((err) => {
  console.error("Fatal error:", err);
  process.exit(1);
});
