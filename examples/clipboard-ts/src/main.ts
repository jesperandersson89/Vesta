/**
 * Vesta Shared Clipboard
 * ──────────────────────
 * A CLI tool that syncs your system clipboard across machines via Vesta.
 *
 * Event schema:
 *   Channel:   {appId}/{room}   (appId defaults to "clipboard")
 *   Event:     app.clipboard.update
 *   Payload:   { "text": "...", "username": "..." }
 *   Replace:   true (server keeps only the latest per user)
 *
 * Run:  npx tsx src/main.ts [ws://host:port/ws] [room-name]
 * Env:  VESTA_RELAY_URL, VESTA_APP_ID, VESTA_IDENTITY_FILE (for Atrium-managed relays)
 */

import { createInterface } from "node:readline";
import clipboardy from "clipboardy";
import WebSocket from "ws";
import {
    LwwMap,
    LwwMapUpdate,
    VestaConnection,
    VestaIdentity,
    createEvent,
    loadOrCreateIdentity,
    type EventMessage,
    type EventsBatchMessage,
    type SequencedEvent,
    type VestaEvent,
    type VestaSocket,
} from "vesta-client";

// ── Configuration ────────────────────────────────────────────────────────────
const DEFAULT_SERVER = "ws://localhost:5150/ws";
const DEFAULT_ROOM = "main";// App namespace = the first channel segment. Set VESTA_APP_ID to the app id you
// provisioned in Atrium so every channel is scoped under it. Defaults to "clipboard".
const DEFAULT_APP_ID = "clipboard";const POLL_INTERVAL_MS = 300;

// ── State ────────────────────────────────────────────────────────────────────
interface ClipboardEntry {
    clientId: string;
    username: string;
    text: string;
    timestamp: string;
}

/** Projector: each `app.clipboard.update` sets that author's latest paste. */
function clipboardProjector(
    event: VestaEvent,
): LwwMapUpdate<string, ClipboardEntry> | null {
    if (event.eventType !== "app.clipboard.update") return null;
    const clientId = event.clientId ?? "";
    if (!clientId) return null;
    const payload = (event.payload ?? {}) as {
        text?: string;
        username?: string;
    };
    const entry: ClipboardEntry = {
        clientId,
        username: payload.username ?? clientId.slice(0, 8),
        text: payload.text ?? "",
        timestamp: event.timestamp ?? "",
    };
    return LwwMapUpdate.set(clientId, entry);
}

function getLatestEntry(
    state: ReadonlyMap<string, ClipboardEntry>,
): ClipboardEntry | undefined {
    let latest: ClipboardEntry | undefined;
    for (const entry of state.values()) {
        if (!latest || entry.timestamp > latest.timestamp) latest = entry;
    }
    return latest;
}

function getAllEntries(
    state: ReadonlyMap<string, ClipboardEntry>,
): ClipboardEntry[] {
    return [...state.values()].sort((a, b) =>
        b.timestamp.localeCompare(a.timestamp),
    );
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
    state: ReadonlyMap<string, ClipboardEntry>,
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

    const entries = getAllEntries(state);
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
/**
 * Load an identity from VESTA_IDENTITY_FILE (e.g. the `{appId}.identity.json`
 * downloaded from Atrium) when set, otherwise fall back to a local persistent
 * identity stored under ~/.vesta.
 */
async function resolveIdentity(prefix: string): Promise<VestaIdentity> {
    const file = process.env.VESTA_IDENTITY_FILE;
    if (file) {
        const { readFileSync } = await import("node:fs");
        return VestaIdentity.fromJSON(JSON.parse(readFileSync(file, "utf-8")));
    }
    return loadOrCreateIdentity(prefix);
}

async function main(): Promise<void> {
    const args = process.argv.slice(2);
    const serverUrl = process.env.VESTA_RELAY_URL ?? args[0] ?? DEFAULT_SERVER;
    const room = args[1] ?? DEFAULT_ROOM;
    const appId = process.env.VESTA_APP_ID ?? DEFAULT_APP_ID;
    const channel = `${appId}/${room}`;

    // Prompt for username
    const maybeUsername = await promptUsername();
    if (!maybeUsername) {
        process.exit(0);
    }
    const username: string = maybeUsername;

    // VESTA_IDENTITY_FILE lets you load an identity downloaded from Atrium
    // (the app-owner key) instead of a per-room/user key generated locally.
    const identity = await resolveIdentity(`clipboard-${room}-${username}`);
    const clientId = identity.clientId;
    const state = new LwwMap<string, ClipboardEntry>(clipboardProjector);
    let lastClipboard = "";

    try {
        lastClipboard = await clipboardy.read();
    } catch {
        // Clipboard might be empty or inaccessible
    }

    // Seed the projection with our own initial clipboard so the UI shows us
    // immediately, without waiting for the server echo.
    const initialSelf = createEvent(
        channel,
        identity,
        "app.clipboard.update",
        { text: lastClipboard, username },
        { replace: true },
    );
    state.applyLocal(initialSelf);

    function redraw(): void {
        renderUI(
            state.state,
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
        state.apply({
            event: msg.event,
            sequence: msg.sequence,
            receivedAt: msg.receivedAt,
        });
        const latest = getLatestEntry(state.state);
        if (latest && latest.clientId !== clientId) {
            lastClipboard = latest.text;
            clipboardy.writeSync(latest.text);
        }
        redraw();
    });

    connection.on("eventsBatch", (msg: EventsBatchMessage) => {
        let changed = false;
        for (const se of msg.events) {
            if (se.event.clientId === clientId) continue;
            state.apply(se);
            changed = true;
        }
        if (changed) {
            const latest = getLatestEntry(state.state);
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
                const selfEvent = createEvent(
                    channel,
                    identity,
                    "app.clipboard.update",
                    { text: current, username },
                    { replace: true },
                );
                state.applyLocal(selfEvent);
                if (connection.isConnected) {
                    connection.publish(selfEvent);
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
