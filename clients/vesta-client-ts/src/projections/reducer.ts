import type { SequencedEvent, VestaEvent } from "../types.js";

/**
 * A captured snapshot of a projection's state at a specific server-assigned sequence.
 * Matches the C# `VestaCore.Projections.ProjectionSnapshot` record shape so persisted
 * snapshots are interchangeable across SDKs where the projected state is JSON-compatible.
 */
export interface ProjectionSnapshot {
    lastSequence: number;
    stateJson: string;
}

/** Thrown by the default {@link EventReducer.snapshot} / {@link EventReducer.restore} when a subclass hasn't opted in. */
export class SnapshotNotSupportedError extends Error {
    constructor(reducerName: string) {
        super(
            `Reducer ${reducerName} does not support snapshotting. Override snapshot() and restore() to opt in.`,
        );
        this.name = "SnapshotNotSupportedError";
    }
}

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

    /**
     * Capture the current state as a {@link ProjectionSnapshot}. Override in subclasses
     * that want snapshot support; the default throws {@link SnapshotNotSupportedError}.
     */
    snapshot(): ProjectionSnapshot {
        throw new SnapshotNotSupportedError(this.constructor.name);
    }

    /**
     * Restore the reducer from a previously captured snapshot. Replaces all internal
     * state and sets {@link lastSequence} to `snapshot.lastSequence`.
     */
    restore(snapshot: ProjectionSnapshot): void {
        this.restoreState(snapshot.stateJson);
        this._lastSequence = snapshot.lastSequence;
    }

    /**
     * Hook for {@link restore} — implementations replace their internal state from the
     * JSON produced by their own {@link snapshot}.
     */
    protected restoreState(_stateJson: string): void {
        throw new SnapshotNotSupportedError(this.constructor.name);
    }
}
