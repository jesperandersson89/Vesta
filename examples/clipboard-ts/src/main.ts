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

import { createInterface } from "node:readline";
import clipboardy from "clipboardy";
import WebSocket from "ws";
import {
    VestaConnection,
    createEvent,
    loadOrCreateIdentity,
    type EventMessage,
    type EventsBatchMessage,
    type VestaSocket,
} from "vesta-client";

// ── Configuration ────────────────────────────────────────────────────────────
const DEFAULT_SERVER = "ws://localhost:5150/ws";
const DEFAULT_ROOM = "main";
const POLL_INTERVAL_MS = 300;

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
            b.timestamp.localeCompare(a.timestamp),
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
    localClipboard: string,
): void {
    clearScreen();

    const status = connected
        ? `${GREEN}●${RESET} Connected to ${serverUrl}`
        : `${YELLOW}○${RESET} Disconnected — retrying…`;

    console.log(
        `${BOLD}Vesta Shared Clipboard${RESET}  ${DIM}[${channel}]${RESET}`,
    );
    console.log(status);
    console.log(`${DIM}${"─".repeat(60)}${RESET}`);
    console.log();

    const entries = state.getAll();
    if (entries.length === 0) {
        console.log(
            `${DIM}  No clipboard entries yet. Copy something!${RESET}`,
        );
    } else {
        console.log(`${BOLD}  Recent clips:${RESET}`);
        console.log();
        for (const entry of entries.slice(0, 8)) {
            const isSelf = entry.clientId === selfClientId;
            const who = isSelf
                ? `${CYAN}${entry.username} (you)${RESET}`
                : `${entry.username}`;
            const preview =
                entry.text.length > 50
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
    console.log(
        `${DIM}  Local clipboard: ${localClipboard.length > 40 ? localClipboard.slice(0, 40) + "…" : localClipboard}${RESET}`,
    );
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

    const identity = await loadOrCreateIdentity(
        `clipboard-${room}-${username}`,
    );
    const clientId = identity.clientId;
    const state = new ClipboardState();
    let lastClipboard = "";

    try {
        lastClipboard = await clipboardy.read();
    } catch {
        // Clipboard might be empty or inaccessible
    }

    // Apply initial clipboard as our own entry
    state.apply({
        clientId,
        eventType: "app.clipboard.update",
        timestamp: new Date().toISOString(),
        payload: { text: lastClipboard, username },
    });

    function redraw(): void {
        renderUI(
            state,
            clientId,
            connection.isConnected,
            serverUrl,
            channel,
            lastClipboard,
        );
    }

    // ── Vesta connection ─────────────────────────────────────────────────────
    const connection = new VestaConnection({
        serverUrl,
        clientId,
        publicKey: identity.publicKeyB64,
        channels: [channel],
        createSocket: (url) => new WebSocket(url) as unknown as VestaSocket,
    });

    connection.on("connected", () => {
        if (lastClipboard) {
            connection.publish(
                createEvent(
                    channel,
                    identity,
                    "app.clipboard.update",
                    { text: lastClipboard, username },
                    { replace: true },
                ),
            );
        }
        redraw();
    });

    connection.on("disconnected", () => redraw());

    connection.on("event", (msg: EventMessage) => {
        if (msg.event.clientId === clientId) return;
        if (state.apply(msg.event as any)) {
            const latest = state.getLatest();
            if (latest && latest.clientId !== clientId) {
                lastClipboard = latest.text;
                clipboardy.writeSync(latest.text);
            }
            redraw();
        }
    });

    connection.on("eventsBatch", (msg: EventsBatchMessage) => {
        let changed = false;
        for (const se of msg.events) {
            if (se.event.clientId === clientId) continue;
            if (state.apply(se.event as any)) changed = true;
        }
        if (changed) {
            const latest = state.getLatest();
            if (latest && latest.clientId !== clientId) {
                lastClipboard = latest.text;
                clipboardy.writeSync(latest.text);
            }
            redraw();
        }
    });

    connection.connect();
    redraw();

    // ── Clipboard polling ────────────────────────────────────────────────────
    setInterval(async () => {
        try {
            const current = await clipboardy.read();
            if (current && current !== lastClipboard) {
                lastClipboard = current;
                state.apply({
                    clientId,
                    eventType: "app.clipboard.update",
                    timestamp: new Date().toISOString(),
                    payload: { text: current, username },
                });
                if (connection.isConnected) {
                    connection.publish(
                        createEvent(
                            channel,
                            identity,
                            "app.clipboard.update",
                            { text: current, username },
                            { replace: true },
                        ),
                    );
                }
                redraw();
            }
        } catch {
            // Clipboard read can fail transiently
        }
    }, POLL_INTERVAL_MS);

    process.on("SIGINT", () => {
        console.log("\n  Goodbye!");
        connection.dispose();
        process.exit(0);
    });
}

function promptUsername(): Promise<string | null> {
    return new Promise((resolve) => {
        const rl = createInterface({
            input: process.stdin,
            output: process.stdout,
        });
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
