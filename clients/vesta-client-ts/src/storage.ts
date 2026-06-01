/**
 * Client-side local storage abstraction.
 *
 * Mirrors the C# `VestaClient.Storage.IClientEventStore` 1:1: caches sequenced
 * events received from the server and manages an outbox for events created
 * while disconnected.
 *
 * Ships an `InMemoryClientEventStore` default implementation that is suitable
 * for tests, SSR, and short-lived sessions. Persistent stores (IndexedDB for
 * the browser, sqlite / fs for Node) can implement the same interface — see
 * `docs/projections.md` and `clients/vesta-client-ts/README.md`.
 */

import type { SequencedEvent, VestaEvent } from "./types.js";

export type OutboxStatus = "pending" | "sent" | "confirmed";

/** An entry in the client outbox — an event created offline pending sync. */
export interface OutboxEntry {
    event: VestaEvent;
    /** ISO-8601 timestamp of when the entry was enqueued. */
    createdAt: string;
    status: OutboxStatus;
}

export interface ClientEventStore {
    // ── Event cache ──────────────────────────────────────────────────────────

    /**
     * Store a sequenced event received from the server.
     * Must be idempotent — duplicate (channelId, sequence) pairs are ignored.
     */
    storeEvent(sequenced: SequencedEvent): Promise<void>;

    /** Batch variant of {@link storeEvent}. */
    storeEvents(events: SequencedEvent[]): Promise<void>;

    /**
     * Read cached events for a channel starting at `fromSequence` (inclusive),
     * up to `limit` entries, ordered ascending.
     */
    getEvents(
        channelId: string,
        fromSequence: number,
        limit?: number,
    ): Promise<SequencedEvent[]>;

    /** Latest cached sequence for a channel, or 0 if none. */
    getLatestSequence(channelId: string): Promise<number>;

    /** Map of channelId → latest cached sequence, for reconnect catch-up. */
    getChannelPositions(): Promise<Record<string, number>>;

    // ── Outbox ───────────────────────────────────────────────────────────────

    /** Append an event to the outbox (created offline, pending sync). */
    enqueueOutbox(event: VestaEvent): Promise<void>;

    /**
     * Outbox entries that have not yet been confirmed, ordered by `createdAt`.
     * Returns both `"pending"` (never sent) AND `"sent"` (sent but the process
     * died before the ACK landed) — server-side appends are idempotent on event
     * id, so re-sending a `"sent"` entry on reconnect is safe.
     */
    getPendingOutbox(): Promise<OutboxEntry[]>;

    /** Mark an outbox entry as sent (awaiting server confirmation). */
    markOutboxSent(eventId: string): Promise<void>;

    /**
     * Mark an outbox entry as confirmed (server ACK received).
     * Removes it from the outbox.
     */
    markOutboxConfirmed(eventId: string): Promise<void>;
}

// ── InMemoryClientEventStore ─────────────────────────────────────────────────

interface StoredOutboxEntry {
    event: VestaEvent;
    createdAt: string;
    status: OutboxStatus;
    seq: number; // insertion order tiebreaker
}

/**
 * In-memory `ClientEventStore`. Persists nothing across page reloads or process
 * restarts — but is sufficient to prove protocol-level correctness in tests
 * and is a safe default for transient sessions.
 */
export class InMemoryClientEventStore implements ClientEventStore {
    private readonly events = new Map<string, SequencedEvent[]>();
    private readonly outbox = new Map<string, StoredOutboxEntry>();
    private nextOutboxSeq = 0;

    async storeEvent(sequenced: SequencedEvent): Promise<void> {
        const list = this.events.get(sequenced.event.channelId) ?? [];
        if (list.some((e) => e.sequence === sequenced.sequence)) {
            return;
        }
        list.push(sequenced);
        list.sort((a, b) => a.sequence - b.sequence);
        this.events.set(sequenced.event.channelId, list);
    }

    async storeEvents(events: SequencedEvent[]): Promise<void> {
        for (const e of events) {
            await this.storeEvent(e);
        }
    }

    async getEvents(
        channelId: string,
        fromSequence: number,
        limit = 100,
    ): Promise<SequencedEvent[]> {
        const list = this.events.get(channelId) ?? [];
        return list.filter((e) => e.sequence >= fromSequence).slice(0, limit);
    }

    async getLatestSequence(channelId: string): Promise<number> {
        const list = this.events.get(channelId);
        if (!list || list.length === 0) return 0;
        return list[list.length - 1].sequence;
    }

    async getChannelPositions(): Promise<Record<string, number>> {
        const out: Record<string, number> = {};
        for (const [ch, list] of this.events) {
            if (list.length > 0) {
                out[ch] = list[list.length - 1].sequence;
            }
        }
        return out;
    }

    async enqueueOutbox(event: VestaEvent): Promise<void> {
        if (this.outbox.has(event.id)) return;
        this.outbox.set(event.id, {
            event,
            createdAt: new Date().toISOString(),
            status: "pending",
            seq: this.nextOutboxSeq++,
        });
    }

    async getPendingOutbox(): Promise<OutboxEntry[]> {
        const entries = [...this.outbox.values()]
            .filter((e) => e.status === "pending" || e.status === "sent")
            .sort((a, b) => a.seq - b.seq);
        return entries.map(({ event, createdAt, status }) => ({
            event,
            createdAt,
            status,
        }));
    }

    async markOutboxSent(eventId: string): Promise<void> {
        const entry = this.outbox.get(eventId);
        if (entry) entry.status = "sent";
    }

    async markOutboxConfirmed(eventId: string): Promise<void> {
        this.outbox.delete(eventId);
    }
}
