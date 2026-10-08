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
 * Needs Node.js 22+ (built-in WebSocket).
 *
 * If every relay is unreachable, the SDK opens its own relay picker (a loopback browser
 * page) automatically — no app UI needed. The choice is remembered under ~/.vesta/relays/.
 */

import { createInterface } from "node:readline";
import { homedir } from "node:os";
import { join } from "node:path";
import clipboardy from "clipboardy";
import {
    LwwMap,
    LwwMapUpdate,
    VestaConnection,
    createEvent,
    loadIdentityFile,
    loadOrCreateIdentity,
    restoreProjection,
    saveProjection,
    type VestaEvent,
} from "vesta-client";
import { FileClientEventStore, FileProjectionStore } from "vesta-client/node";

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
const RED = "\x1b[31m";

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
    limitNotice: string | null,
    relayProblem: string | null,
): void {
    clearScreen();

    const status = connected
        ? `${GREEN}●${RESET} Connected to ${serverUrl}`
        : `${YELLOW}○${RESET} Disconnected from ${serverUrl} — retrying…`;

    console.log(
        `${BOLD}Vesta Shared Clipboard${RESET}  ${DIM}[${channel}]${RESET}`,
    );
    console.log(status);
    if (!connected && relayProblem) {
        console.log(`${RED}  Can't reach relay: ${relayProblem}${RESET}`);
    }
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
    console.log(`${DIM}  Local clipboard: ${localClipboard.length > 40 ? localClipboard.slice(0, 40) + "…" : localClipboard}${RESET}`);
    console.log(`${DIM}  Watching for changes… (Ctrl+C to exit)${RESET}`);
    if (limitNotice) {
        console.log(`${RED}  [LIMITED] ${limitNotice}${RESET}`);
    }
}

// ── Main ─────────────────────────────────────────────────────────────────────
async function main(): Promise<void> {
    const args = process.argv.slice(2);
    const relayUrls = (process.env.VESTA_RELAY_URL ?? args[0] ?? DEFAULT_SERVER)
        .split(",")
        .map((s) => s.trim())
        .filter(Boolean);
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
    const identityFile = process.env.VESTA_IDENTITY_FILE;
    const identity = identityFile
        ? await loadIdentityFile(identityFile)
        : await loadOrCreateIdentity(`clipboard-${room}-${username}`);
    const clientId = identity.clientId;

    // The relay-independence trust anchor: VESTA_APP_OWNER_KEY if set, otherwise this client's own
    // key, so the demo is self-signing. Relay overrides and manifests persist under ~/.vesta/relays/.
    const appConfig = {
        appId,
        ownerPublicKey: process.env.VESTA_APP_OWNER_KEY ?? identity.publicKeyB64,
        defaultRelays: relayUrls,
    };
    // Local event cache + offline outbox, and a projection snapshot for fast cold start.
    const vestaDir = join(homedir(), ".vesta");
    const localStore = new FileClientEventStore(join(vestaDir, `clipboard-${room}-${username}-cache.json`));
    const projectionStore = new FileProjectionStore(join(vestaDir, `clipboard-${room}-${username}-snapshots.json`));

    const state = new LwwMap<string, ClipboardEntry>(clipboardProjector);
    await restoreProjection(projectionStore, channel, "clipboard", state);

    let lastClipboard = "";
    try {
        lastClipboard = await clipboardy.read();
    } catch {
        // Clipboard might be empty or inaccessible
    }

    // Seed the projection with our own current clipboard so the UI shows us
    // immediately, without waiting for the server echo. Its "now" timestamp wins
    // over anything restored from the snapshot, per LWW semantics.
    const initialSelf = createEvent(
        channel,
        identity,
        "app.clipboard.update",
        { text: lastClipboard, username },
        { replace: true },
    );
    state.applyLocal(initialSelf);

    let lastLimitNotice: string | null = null;
    let relayProblem: string | null = null;

    function redraw(): void {
        renderUI(
            state.state,
            clientId,
            connection.isConnected,
            connection.activeRelay,
            channel,
            lastClipboard,
            lastLimitNotice,
            relayProblem,
        );
    }

    // ── Vesta connection ─────────────────────────────────────────────────────
    const connection = new VestaConnection({ appConfig, identity, channels: [channel], localStore });

    connection.on("relayAttemptFailed", (attempt) => {
        relayProblem = `${attempt.relay} (${attempt.reason})`;
        redraw();
    });

    connection.on("connected", () => {
        relayProblem = null;
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

    connection.on("limited", (notice) => {
        lastLimitNotice = `${notice.code}: ${notice.message}`;
        redraw();
    });

    connection.on("event", (msg) => {
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

    connection.on("eventsBatch", (msg) => {
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

    if (process.stdin.isTTY) {
        process.stdin.setRawMode(true);
        process.stdin.resume();
        process.stdin.on("data", (chunk: Buffer) => {
            if (chunk.toString() === "\u0003") process.emit("SIGINT");
        });
    }

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
                connection.publish(selfEvent);
                redraw();
            }
        } catch {
            // Clipboard read can fail transiently
        }
    }, POLL_INTERVAL_MS);

    process.on("SIGINT", async () => {
        console.log("\n  Goodbye!");
        await saveProjection(projectionStore, channel, "clipboard", state);
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
