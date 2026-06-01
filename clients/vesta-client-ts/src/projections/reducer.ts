import type { SequencedEvent, VestaEvent } from "../types.js";

/**
 * Base class for projections that fold a channel's event stream into typed state.
 *
 * Concrete reducers implement {@link reduce} to mutate their internal storage and
 * expose a snapshot via {@link state}. Applying a {@link SequencedEvent} advances
 * {@link lastSequence}; applying a bare {@link VestaEvent} via {@link applyLocal}
 * does not (used for optimistic local updates before the server has assigned a
 * sequence).
 *
 * JavaScript is single-threaded so no locking is required — but the deduplication
 * and timestamp-comparison rules match the C# `VestaCore.Projections.EventReducer`
 * exactly so a TS client and a C# client see the same state for the same stream.
 */
export abstract class EventReducer<TState> {
    private _lastSequence = 0;

    /** Highest server-assigned sequence number this reducer has observed. */
    get lastSequence(): number {
        return this._lastSequence;
    }

    /** Snapshot of the current projected state. */
    abstract get state(): TState;

    /** Apply a server-confirmed event. Advances {@link lastSequence}. */
    apply(sequenced: SequencedEvent): void {
        this.reduce(sequenced.event);
        if (sequenced.sequence > this._lastSequence) {
            this._lastSequence = sequenced.sequence;
        }
    }

    /** Apply a batch of server-confirmed events. */
    applyBatch(events: Iterable<SequencedEvent>): void {
        for (const sequenced of events) {
            this.apply(sequenced);
        }
    }

    /**
     * Apply a locally-authored event that has not yet been sequenced, for
     * optimistic UI updates. Does not advance {@link lastSequence}.
     */
    applyLocal(event: VestaEvent): void {
        this.reduce(event);
    }

    /** Implementations mutate their internal state in response to the event. */
    protected abstract reduce(event: VestaEvent): void;
}
