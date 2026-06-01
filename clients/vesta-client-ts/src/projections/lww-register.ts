import type { VestaEvent } from "../types.js";
import { EventReducer } from "./reducer.js";

/**
 * Single-value last-writer-wins register.
 *
 * Each event is run through the supplied projector. If it produces a non-null
 * value and the event's timestamp is strictly newer than the timestamp of the
 * currently stored value, the register is updated. Ties on timestamp preserve
 * the existing value (deterministic across clients).
 *
 * Timestamps are compared lexicographically as ISO-8601 strings, which is
 * correct because all Vesta timestamps are normalized to UTC with millisecond
 * precision.
 */
export class LwwRegister<T> extends EventReducer<T | null> {
    private _value: T | null = null;
    private _valueTimestamp = ""; // empty sorts before any ISO-8601 string
    private readonly _project: (event: VestaEvent) => T | null | undefined;

    constructor(project: (event: VestaEvent) => T | null | undefined) {
        super();
        this._project = project;
    }

    get state(): T | null {
        return this._value;
    }

    /** ISO-8601 timestamp of the event that produced the current value, or `""` if empty. */
    get lastModified(): string {
        return this._valueTimestamp;
    }

    protected reduce(event: VestaEvent): void {
        const projected = this._project(event);
        if (projected === null || projected === undefined) {
            return;
        }

        if (event.timestamp > this._valueTimestamp) {
            this._value = projected;
            this._valueTimestamp = event.timestamp;
        }
    }
}
