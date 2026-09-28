/**
 * Persistent store for `ProjectionSnapshot`s so projections can resume from the last known
 * sequence on cold start instead of replaying the entire channel log. Mirrors the C#
 * `VestaClient.Storage.IProjectionStore`.
 *
 * Snapshots are keyed by `(channelId, projectionId)` — a single channel may have multiple
 * projections (e.g. chat history + presence map), each persisted independently.
 */

import type { EventReducer, ProjectionSnapshot } from "./projections/index.js";

export interface ProjectionStore {
    /** Save (or overwrite) the snapshot for a given channel + projection. */
    save(channelId: string, projectionId: string, snapshot: ProjectionSnapshot): Promise<void>;

    /** Load the snapshot for a given channel + projection, or `null` if none has been saved. */
    load(channelId: string, projectionId: string): Promise<ProjectionSnapshot | null>;

    /** Delete the snapshot for a given channel + projection. No-op if not present. */
    delete(channelId: string, projectionId: string): Promise<void>;
}

function keyFor(channelId: string, projectionId: string): string {
    return `${channelId}\u0000${projectionId}`;
}

/** Capture a reducer's current state and persist it. */
export async function saveProjection(
    store: ProjectionStore,
    channelId: string,
    projectionId: string,
    reducer: EventReducer<unknown>,
): Promise<void> {
    await store.save(channelId, projectionId, reducer.snapshot());
}

/** Load a snapshot (if any) and restore it into the reducer. Returns true if a snapshot was found. */
export async function restoreProjection(
    store: ProjectionStore,
    channelId: string,
    projectionId: string,
    reducer: EventReducer<unknown>,
): Promise<boolean> {
    const snapshot = await store.load(channelId, projectionId);
    if (!snapshot) return false;
    reducer.restore(snapshot);
    return true;
}

/** In-memory `ProjectionStore`. Persists nothing across page reloads or process restarts. */
export class InMemoryProjectionStore implements ProjectionStore {
    private readonly snapshots = new Map<string, ProjectionSnapshot>();

    async save(channelId: string, projectionId: string, snapshot: ProjectionSnapshot): Promise<void> {
        this.snapshots.set(keyFor(channelId, projectionId), snapshot);
    }

    async load(channelId: string, projectionId: string): Promise<ProjectionSnapshot | null> {
        return this.snapshots.get(keyFor(channelId, projectionId)) ?? null;
    }

    async delete(channelId: string, projectionId: string): Promise<void> {
        this.snapshots.delete(keyFor(channelId, projectionId));
    }
}

/** A `localStorage`-backed `ProjectionStore` for browser apps. `keyPrefix` namespaces the keys. */
export class LocalStorageProjectionStore implements ProjectionStore {
    constructor(private readonly keyPrefix: string) {}

    private storageKey(channelId: string, projectionId: string): string {
        return `${this.keyPrefix}:${channelId}:${projectionId}`;
    }

    async save(channelId: string, projectionId: string, snapshot: ProjectionSnapshot): Promise<void> {
        globalThis.localStorage?.setItem(
            this.storageKey(channelId, projectionId),
            JSON.stringify(snapshot),
        );
    }

    async load(channelId: string, projectionId: string): Promise<ProjectionSnapshot | null> {
        const raw = globalThis.localStorage?.getItem(this.storageKey(channelId, projectionId));
        if (!raw) return null;
        try {
            return JSON.parse(raw) as ProjectionSnapshot;
        } catch {
            return null;
        }
    }

    async delete(channelId: string, projectionId: string): Promise<void> {
        globalThis.localStorage?.removeItem(this.storageKey(channelId, projectionId));
    }
}
