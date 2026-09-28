/**
 * Node.js-only persistent stores — file-backed implementations of `ClientEventStore`,
 * `ProjectionStore`, `RelayOverrideStore`, and `ManifestStore`.
 *
 * Import from `vesta-client/node`, NOT the package root — this module statically imports
 * Node built-ins (`node:fs`, `node:os`, `node:path`) and will fail to bundle for the browser.
 * Browser apps should use the `LocalStorage*` stores from the package root instead.
 */

import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";

import type { ClientEventStore, OutboxEntry, OutboxStatus } from "./storage.js";
import type { SequencedEvent, VestaEvent } from "./types.js";
import type { ProjectionSnapshot } from "./projections/index.js";
import type { ProjectionStore } from "./projection-store.js";
import type { ManifestStore, RelayManifest, RelayOverrideStore } from "./relay.js";

function ensureDirFor(path: string): void {
    mkdirSync(dirname(path), { recursive: true });
}

function readJson<T>(path: string): T | null {
    if (!existsSync(path)) return null;
    try {
        return JSON.parse(readFileSync(path, "utf-8")) as T;
    } catch {
        return null;
    }
}

function writeJson(path: string, value: unknown): void {
    ensureDirFor(path);
    writeFileSync(path, JSON.stringify(value, null, 2));
}

/** Default directory Vesta's Node.js file-backed stores persist under: `~/.vesta`. */
export function defaultVestaDir(): string {
    return join(homedir(), ".vesta");
}

// ─── Relay stores ────────────────────────────────────────────────────────────

/** A JSON-file-backed relay override store — the Node.js counterpart to `LocalStorageRelayOverrideStore`. */
export class FileRelayOverrideStore implements RelayOverrideStore {
    constructor(private readonly path: string) {}

    getOverride(): string | null {
        return readJson<{ url: string }>(this.path)?.url ?? null;
    }

    setOverride(url: string): void {
        writeJson(this.path, { url });
    }

    clearOverride(): void {
        if (existsSync(this.path)) writeFileSync(this.path, JSON.stringify(null));
    }
}

/** A JSON-file-backed manifest cache — the Node.js counterpart to `LocalStorageManifestStore`. */
export class FileManifestStore implements ManifestStore {
    constructor(private readonly path: string) {}

    getCached(): RelayManifest | null {
        return readJson<RelayManifest>(this.path);
    }

    save(manifest: RelayManifest): void {
        writeJson(this.path, manifest);
    }
}

/** Sanitize an app id for use as a filename segment. */
function sanitizeForFileName(id: string): string {
    return id.replace(/[<>:"/\\|?*]/g, "_");
}

/** Build the default `FileRelayOverrideStore` + `FileManifestStore` pair for an app, under `~/.vesta/relays/`. */
export function defaultRelayStorePaths(appId: string): { overridePath: string; manifestPath: string } {
    const dir = join(defaultVestaDir(), "relays");
    const safeAppId = sanitizeForFileName(appId);
    return {
        overridePath: join(dir, `${safeAppId}.override.json`),
        manifestPath: join(dir, `${safeAppId}.manifest.json`),
    };
}

// ─── Projection store ────────────────────────────────────────────────────────

interface ProjectionFile {
    snapshots: Record<string, ProjectionSnapshot>;
}

function keyFor(channelId: string, projectionId: string): string {
    return `${channelId}\u0000${projectionId}`;
}

/** A single-JSON-file-backed `ProjectionStore` for Node.js CLI / server apps. */
export class FileProjectionStore implements ProjectionStore {
    constructor(private readonly path: string) {}

    private readAll(): ProjectionFile {
        return readJson<ProjectionFile>(this.path) ?? { snapshots: {} };
    }

    async save(channelId: string, projectionId: string, snapshot: ProjectionSnapshot): Promise<void> {
        const file = this.readAll();
        file.snapshots[keyFor(channelId, projectionId)] = snapshot;
        writeJson(this.path, file);
    }

    async load(channelId: string, projectionId: string): Promise<ProjectionSnapshot | null> {
        return this.readAll().snapshots[keyFor(channelId, projectionId)] ?? null;
    }

    async delete(channelId: string, projectionId: string): Promise<void> {
        const file = this.readAll();
        delete file.snapshots[keyFor(channelId, projectionId)];
        writeJson(this.path, file);
    }
}

// ─── Client event store ──────────────────────────────────────────────────────

interface ClientEventStoreFile {
    events: Record<string, SequencedEvent[]>;
    outbox: Record<string, { event: VestaEvent; createdAt: string; status: OutboxStatus; seq: number }>;
    nextOutboxSeq: number;
}

/**
 * A single-JSON-file-backed `ClientEventStore` for Node.js CLI apps — durable across restarts,
 * unlike `InMemoryClientEventStore`. Not designed for high write volume (rewrites the whole file
 * on every mutation); fine for demo-scale examples.
 */
export class FileClientEventStore implements ClientEventStore {
    constructor(private readonly path: string) {}

    private readAll(): ClientEventStoreFile {
        return readJson<ClientEventStoreFile>(this.path) ?? { events: {}, outbox: {}, nextOutboxSeq: 0 };
    }

    private writeAll(file: ClientEventStoreFile): void {
        writeJson(this.path, file);
    }

    async storeEvent(sequenced: SequencedEvent): Promise<void> {
        const file = this.readAll();
        const list = file.events[sequenced.event.channelId] ?? [];
        if (list.some((e) => e.sequence === sequenced.sequence)) return;
        list.push(sequenced);
        list.sort((a, b) => a.sequence - b.sequence);
        file.events[sequenced.event.channelId] = list;
        this.writeAll(file);
    }

    async storeEvents(events: SequencedEvent[]): Promise<void> {
        for (const e of events) {
            await this.storeEvent(e);
        }
    }

    async getEvents(channelId: string, fromSequence: number, limit = 100): Promise<SequencedEvent[]> {
        const list = this.readAll().events[channelId] ?? [];
        return list.filter((e) => e.sequence >= fromSequence).slice(0, limit);
    }

    async getLatestSequence(channelId: string): Promise<number> {
        const list = this.readAll().events[channelId];
        if (!list || list.length === 0) return 0;
        return list[list.length - 1].sequence;
    }

    async getChannelPositions(): Promise<Record<string, number>> {
        const out: Record<string, number> = {};
        for (const [ch, list] of Object.entries(this.readAll().events)) {
            if (list.length > 0) out[ch] = list[list.length - 1].sequence;
        }
        return out;
    }

    async enqueueOutbox(event: VestaEvent): Promise<void> {
        const file = this.readAll();
        if (file.outbox[event.id]) return;
        file.outbox[event.id] = {
            event,
            createdAt: new Date().toISOString(),
            status: "pending",
            seq: file.nextOutboxSeq++,
        };
        this.writeAll(file);
    }

    async getPendingOutbox(): Promise<OutboxEntry[]> {
        const file = this.readAll();
        return Object.values(file.outbox)
            .filter((e) => e.status === "pending" || e.status === "sent")
            .sort((a, b) => a.seq - b.seq)
            .map(({ event, createdAt, status }) => ({ event, createdAt, status }));
    }

    async markOutboxSent(eventId: string): Promise<void> {
        const file = this.readAll();
        const entry = file.outbox[eventId];
        if (entry) {
            entry.status = "sent";
            this.writeAll(file);
        }
    }

    async markOutboxConfirmed(eventId: string): Promise<void> {
        const file = this.readAll();
        if (file.outbox[eventId]) {
            delete file.outbox[eventId];
            this.writeAll(file);
        }
    }

    async markOutboxRejected(eventId: string, code: string): Promise<void> {
        const file = this.readAll();
        const entry = file.outbox[eventId];
        if (entry) {
            entry.status = "rejected";
            this.writeAll(file);
        }
        void code; // recorded for parity with the C#/Python stores; not surfaced by this minimal file format
    }
}
