import type { VestaEvent } from "../types.js";
import { EventReducer } from "./reducer.js";

/** A single update to an {@link LwwMap}: either set a key, or remove (tombstone) it. */
export type LwwMapUpdate<K, V> =
    | { readonly kind: "set"; readonly key: K; readonly value: V }
    | { readonly kind: "remove"; readonly key: K };

/** Helpers for building {@link LwwMapUpdate} values. */
export const LwwMapUpdate = {
    set<K, V>(key: K, value: V): LwwMapUpdate<K, V> {
        return { kind: "set", key, value };
    },
    remove<K, V>(key: K): LwwMapUpdate<K, V> {
        return { kind: "remove", key };
    },
};

/**
 * Key-value map where each key independently follows last-writer-wins by event timestamp.
 *
 * The projector inspects each event and returns either an {@link LwwMapUpdate} or
 * null/undefined when the event does not affect the map. A Set with a timestamp
 * older than the current entry is ignored; a Remove tombstones the key; a later
 * Set whose timestamp is older than the tombstone is also ignored (no zombies).
 */
export class LwwMap<K, V> extends EventReducer<ReadonlyMap<K, V>> {
    private readonly _entries = new Map<K, Entry<V>>();
    private readonly _project: (
        event: VestaEvent,
    ) => LwwMapUpdate<K, V> | null | undefined;

    constructor(
        project: (event: VestaEvent) => LwwMapUpdate<K, V> | null | undefined,
    ) {
        super();
        this._project = project;
    }

    get state(): ReadonlyMap<K, V> {
        const snapshot = new Map<K, V>();
        for (const [key, entry] of this._entries) {
            if (!entry.tombstoned) {
                snapshot.set(key, entry.value as V);
            }
        }
        return snapshot;
    }

    /** Try to read the live (non-tombstoned) value for a key. */
    tryGet(key: K): { found: true; value: V } | { found: false } {
        const entry = this._entries.get(key);
        if (entry && !entry.tombstoned) {
            return { found: true, value: entry.value as V };
        }
        return { found: false };
    }

    protected reduce(event: VestaEvent): void {
        const update = this._project(event);
        if (update === null || update === undefined) {
            return;
        }

        const existing = this._entries.get(update.key);
        if (existing && event.timestamp <= existing.timestamp) {
            return; // stale — keep existing
        }

        if (update.kind === "remove") {
            this._entries.set(update.key, {
                value: undefined,
                timestamp: event.timestamp,
                tombstoned: true,
            });
        } else {
            this._entries.set(update.key, {
                value: update.value,
                timestamp: event.timestamp,
                tombstoned: false,
            });
        }
    }
}

interface Entry<V> {
    readonly value: V | undefined;
    readonly timestamp: string;
    readonly tombstoned: boolean;
}
