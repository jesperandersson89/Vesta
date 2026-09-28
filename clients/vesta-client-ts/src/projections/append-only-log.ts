import type { VestaEvent } from "../types.js";
import { EventReducer, type ProjectionSnapshot } from "./reducer.js";

interface SnapshotPayload<T> {
    items: T[];
    seenIds: string[];
}

/**
 * Append-only ordered list reducer.
 *
 * Every event for which the supplied projector returns a non-null/undefined
 * value is appended to the log. Events are deduplicated by `event.id` so that
 * an event seen both via {@link EventReducer.applyLocal} and later via the
 * server's confirmed sequence does not appear twice.
 */
export class AppendOnlyLog<T> extends EventReducer<readonly T[]> {
    private readonly _items: T[] = [];
    private readonly _seenIds = new Set<string>();
    private readonly _project: (event: VestaEvent) => T | null | undefined;

    constructor(project: (event: VestaEvent) => T | null | undefined) {
        super();
        this._project = project;
    }

    get state(): readonly T[] {
        return this._items.slice();
    }

    get count(): number {
        return this._items.length;
    }

    protected reduce(event: VestaEvent): void {
        if (this._seenIds.has(event.id)) {
            return;
        }

        const projected = this._project(event);
        if (projected === null || projected === undefined) {
            return;
        }

        this._seenIds.add(event.id);
        this._items.push(projected);
    }

    override snapshot(): ProjectionSnapshot {
        const payload: SnapshotPayload<T> = {
            items: this._items.slice(),
            seenIds: [...this._seenIds],
        };
        return { lastSequence: this.lastSequence, stateJson: JSON.stringify(payload) };
    }

    protected override restoreState(stateJson: string): void {
        const payload = JSON.parse(stateJson) as SnapshotPayload<T>;
        this._items.length = 0;
        this._items.push(...payload.items);
        this._seenIds.clear();
        for (const id of payload.seenIds) this._seenIds.add(id);
    }
}
