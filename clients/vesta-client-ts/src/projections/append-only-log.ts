import type { VestaEvent } from "../types.js";
import { EventReducer } from "./reducer.js";

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
}
