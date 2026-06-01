/**
 * SDK conflict-resolution primitives.
 *
 * Mirrors `VestaCore.Projections` from the C# SDK. See `docs/projections.md`
 * for the picking guide and semantics.
 */
export { AppendOnlyLog } from "./append-only-log.js";
export { LwwMap, LwwMapUpdate } from "./lww-map.js";
export { LwwRegister } from "./lww-register.js";
export { EventReducer } from "./reducer.js";

/** Lightweight pair to persist alongside projection snapshots for resumable replay. */
export interface ProjectionCheckpoint {
    readonly channelId: string;
    readonly lastSequence: number;
}
