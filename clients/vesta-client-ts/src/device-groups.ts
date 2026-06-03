/**
 * Cross-device identity (device groups) — TypeScript SDK.
 *
 * Mirrors `VestaCore.Identity` from the C# implementation. See
 * `PLANNING.md` → "Cross-Device Identity" for the design rationale.
 */

import { createEvent } from "./events.js";
import type { VestaIdentity } from "./identity.js";
import { base64UrlToBytes, bytesToBase64Url, deriveClientId } from "./signing.js";
import type { SequencedEvent, VestaEvent } from "./types.js";

// ─── Constants ────────────────────────────────────────────────────────────

/** Channel-ID prefix reserved for protocol-level channels. */
export const PROTOCOL_CHANNEL_PREFIX = "vesta/";

/** Event type for a device announcing itself in a group. */
export const ANNOUNCE_EVENT_TYPE = "vesta.identity.announce";

/** Event type for a device vouching for another public key as a group member. */
export const LINK_EVENT_TYPE = "vesta.identity.link";

/** Event type for a device removing another public key from a group. */
export const UNLINK_EVENT_TYPE = "vesta.identity.unlink";

// ─── Payload shapes ───────────────────────────────────────────────────────

export interface DeviceAnnouncePayload {
    groupId: string;
    deviceName?: string;
}

export interface DeviceLinkPayload {
    targetPublicKey: string; // base64url
    targetClientId: string;
    groupId: string;
    reason?: string;
}

// ─── Helpers ──────────────────────────────────────────────────────────────

/** Canonical channel ID for a device group: `vesta/identity/{groupId}`. */
export function deviceGroupChannel(groupId: string): string {
    if (!groupId) throw new Error("Group ID must not be empty.");
    return `vesta/identity/${groupId}`;
}

/** Returns true if the channel ID is in the reserved `vesta/` namespace. */
export function isProtocolChannel(channelId: string): boolean {
    return channelId.startsWith(PROTOCOL_CHANNEL_PREFIX);
}

/**
 * Generate a new random group identifier suitable for embedding in a channel ID.
 * Returns 32 lowercase hex chars (128 bits of entropy). Hex is used because the
 * `_` character in base64url is not permitted in channel IDs.
 */
export function generateGroupId(): string {
    const bytes = new Uint8Array(16);
    globalThis.crypto.getRandomValues(bytes);
    return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

function validateGroupId(groupId: string): void {
    if (!groupId) throw new Error("Group ID must not be empty.");
    for (const c of groupId) {
        const ok =
            (c >= "a" && c <= "z") ||
            (c >= "0" && c <= "9") ||
            c === "-";
        if (!ok) {
            throw new Error(
                `Group ID '${groupId}' contains invalid character '${c}'. Only [a-z0-9-] are allowed.`,
            );
        }
    }
}

// ─── Event builders ───────────────────────────────────────────────────────

/**
 * Build and sign a `vesta.identity.announce` event from `identity` into `groupId`'s channel.
 */
export function buildAnnounce(
    identity: VestaIdentity,
    groupId: string,
    deviceName?: string,
): VestaEvent {
    validateGroupId(groupId);
    const payload: DeviceAnnouncePayload = { groupId };
    if (deviceName !== undefined) payload.deviceName = deviceName;
    return createEvent(
        deviceGroupChannel(groupId),
        identity,
        ANNOUNCE_EVENT_TYPE,
        payload,
    );
}

/**
 * Build and sign a `vesta.identity.link` event vouching for `targetPublicKey`
 * as a member of `groupId`. Signed by `signer`, who must already be a trusted
 * member of the group for the link to be honored.
 */
export function buildLink(
    signer: VestaIdentity,
    groupId: string,
    targetPublicKey: Uint8Array,
    reason?: string,
): VestaEvent {
    validateGroupId(groupId);
    if (targetPublicKey.length !== 32) {
        throw new Error("Target public key must be 32 bytes (Ed25519).");
    }
    const payload: DeviceLinkPayload = {
        targetPublicKey: bytesToBase64Url(targetPublicKey),
        targetClientId: deriveClientId(targetPublicKey),
        groupId,
    };
    if (reason !== undefined) payload.reason = reason;
    return createEvent(
        deviceGroupChannel(groupId),
        signer,
        LINK_EVENT_TYPE,
        payload,
    );
}

/**
 * Build and sign a `vesta.identity.unlink` event removing `targetPublicKey` from `groupId`.
 */
export function buildUnlink(
    signer: VestaIdentity,
    groupId: string,
    targetPublicKey: Uint8Array,
    reason?: string,
): VestaEvent {
    validateGroupId(groupId);
    if (targetPublicKey.length !== 32) {
        throw new Error("Target public key must be 32 bytes (Ed25519).");
    }
    const payload: DeviceLinkPayload = {
        targetPublicKey: bytesToBase64Url(targetPublicKey),
        targetClientId: deriveClientId(targetPublicKey),
        groupId,
    };
    if (reason !== undefined) payload.reason = reason;
    return createEvent(
        deviceGroupChannel(groupId),
        signer,
        UNLINK_EVENT_TYPE,
        payload,
    );
}

// ─── PairingPayload ──────────────────────────────────────────────────────

/**
 * The information one device delivers to another out-of-band to bootstrap a
 * device-group link. Not secret — contains only public information.
 */
export interface PairingPayload {
    groupId: string;
    /** base64url-encoded 32-byte Ed25519 public key of the inviting device. */
    publicKey: string;
    /** Optional Vesta server URL. */
    serverUrl?: string;
}

export const PairingPayload = {
    /** Encode as a compact base64url string (suitable for QR codes, deep links). */
    toBase64(payload: PairingPayload): string {
        const json = JSON.stringify(payload);
        const bytes = new TextEncoder().encode(json);
        return bytesToBase64Url(bytes);
    },

    /** Decode from a base64url-encoded payload previously produced by `toBase64`. */
    fromBase64(encoded: string): PairingPayload {
        if (!encoded) throw new Error("Encoded pairing payload must not be empty.");
        const bytes = base64UrlToBytes(encoded);
        const json = new TextDecoder().decode(bytes);
        return JSON.parse(json) as PairingPayload;
    },
};

// ─── DeviceGroup view ────────────────────────────────────────────────────

/**
 * The current materialized state of a device group. Produced by
 * `DeviceGroupProjection` by replaying the identity channel.
 */
export interface DeviceGroup {
    groupId: string;
    /** Map of trusted clientId → base64url public key (empty string for the founder until they're cross-linked). */
    members: Record<string, string>;
}

// ─── DeviceGroupProjection ───────────────────────────────────────────────

/**
 * Client-side reducer that materializes the current membership of a device
 * group by replaying its `vesta/identity/{groupId}` channel.
 *
 * Trust rules (Phase 1):
 *  - The author of the first `announce` event is self-trusted (founder).
 *  - A `link` event is honored only if its signer is already trusted; it
 *    adds the target to the trusted set.
 *  - An `unlink` event is honored only if its signer is already trusted;
 *    it removes the target.
 *  - Signature verification of events is the caller's responsibility.
 */
export class DeviceGroupProjection {
    readonly groupId: string;
    private readonly members = new Map<string, string>();
    private _lastSequence = 0;

    constructor(groupId: string) {
        if (!groupId) throw new Error("Group ID must not be empty.");
        this.groupId = groupId;
    }

    get lastSequence(): number {
        return this._lastSequence;
    }

    get state(): DeviceGroup {
        const out: Record<string, string> = {};
        for (const [k, v] of this.members) out[k] = v;
        return { groupId: this.groupId, members: out };
    }

    /** True if the given client ID is currently a trusted member. */
    isMember(clientId: string): boolean {
        return this.members.has(clientId);
    }

    /** Apply a server-confirmed event. Advances `lastSequence`. */
    apply(sequenced: SequencedEvent): void {
        this.reduce(sequenced.event);
        if (sequenced.sequence > this._lastSequence) {
            this._lastSequence = sequenced.sequence;
        }
    }

    /** Apply a batch of server-confirmed events. */
    applyBatch(events: SequencedEvent[]): void {
        for (const s of events) this.apply(s);
    }

    /** Apply a locally-authored event before the server has sequenced it. Does not advance `lastSequence`. */
    applyLocal(event: VestaEvent): void {
        this.reduce(event);
    }

    private reduce(evt: VestaEvent): void {
        switch (evt.eventType) {
            case ANNOUNCE_EVENT_TYPE:
                this.reduceAnnounce(evt);
                break;
            case LINK_EVENT_TYPE:
                this.reduceLink(evt);
                break;
            case UNLINK_EVENT_TYPE:
                this.reduceUnlink(evt);
                break;
        }
    }

    private reduceAnnounce(evt: VestaEvent): void {
        const payload = evt.payload as DeviceAnnouncePayload | undefined;
        if (!payload || payload.groupId !== this.groupId) return;
        // Founder bootstrap.
        if (this.members.size === 0) {
            this.members.set(evt.clientId, "");
        }
        // Non-founder announces are ignored until linked.
    }

    private reduceLink(evt: VestaEvent): void {
        if (!this.members.has(evt.clientId)) return;
        const payload = evt.payload as DeviceLinkPayload | undefined;
        if (!payload || payload.groupId !== this.groupId) return;

        try {
            const pubKey = base64UrlToBytes(payload.targetPublicKey);
            if (pubKey.length !== 32) return;
            const derived = deriveClientId(pubKey);
            if (derived !== payload.targetClientId) return;
            this.members.set(payload.targetClientId, payload.targetPublicKey);
        } catch {
            // Malformed base64url — ignore.
        }
    }

    private reduceUnlink(evt: VestaEvent): void {
        if (!this.members.has(evt.clientId)) return;
        const payload = evt.payload as DeviceLinkPayload | undefined;
        if (!payload || payload.groupId !== this.groupId) return;
        this.members.delete(payload.targetClientId);
    }
}
