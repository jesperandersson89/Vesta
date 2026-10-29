/**
 * The two control events that distribute channel keys on an encrypted channel (PLANNING #33 phase C).
 * Both are ordinary signed events with `vesta.*` types, so the relay lets them through the envelope
 * check and they ride the normal catch-up. Mirrors the C# `ChannelKeyEvents`.
 *
 * - `vesta.channel-key-request` — payload `{ publicKey, x25519PublicKey }` (both base64url).
 *   Self-authenticating: the Ed25519 key must derive the author's `clientId` and the signature must verify.
 * - `vesta.channel-key` — payload `{ recipient, keys: [{ epoch, epk, n, ct }] }`. Accepted only when
 *   signed by one of the admin keys the app trusts.
 */

import type { ChannelKeyPair, SealedChannelKey } from "./channel-crypto.js";
import type { VestaIdentity } from "./identity.js";
import { base64UrlToBytes, bytesToBase64Url, deriveClientId, signEvent, verifyEvent } from "./signing.js";
import type { VestaEvent } from "./types.js";

export const KEY_REQUEST_EVENT_TYPE = "vesta.channel-key-request";
export const KEY_GRANT_EVENT_TYPE = "vesta.channel-key";

/** A verified key request: a member announcing its X25519 public key. */
export interface ChannelKeyRequest {
    clientId: string;
    publicKey: Uint8Array;
    x25519PublicKey: Uint8Array;
}

/** A verified key grant: an admin handing one recipient one or more sealed epochs. */
export interface ChannelKeyGrant {
    adminClientId: string;
    recipientClientId: string;
    keys: SealedChannelKey[];
}

function newEvent(identity: VestaIdentity, channelId: string, eventType: string, payload: unknown): VestaEvent {
    const event: VestaEvent = {
        id: globalThis.crypto.randomUUID(),
        channelId,
        timestamp: new Date().toISOString(),
        clientId: identity.clientId,
        eventType,
        payload,
    };
    signEvent(event, identity.privateKey);
    return event;
}

/** Build and sign a key request from `identity` for `channelId`. */
export function buildKeyRequest(identity: VestaIdentity, channelId: string, keyPair: ChannelKeyPair): VestaEvent {
    return newEvent(identity, channelId, KEY_REQUEST_EVENT_TYPE, {
        publicKey: bytesToBase64Url(identity.publicKey),
        x25519PublicKey: bytesToBase64Url(keyPair.publicKey),
    });
}

/** Build and sign a key grant from `admin` to `recipientClientId`. */
export function buildKeyGrant(
    admin: VestaIdentity,
    channelId: string,
    recipientClientId: string,
    keys: SealedChannelKey[],
): VestaEvent {
    if (keys.length === 0) throw new Error("A key grant must carry at least one sealed key.");
    return newEvent(admin, channelId, KEY_GRANT_EVENT_TYPE, {
        recipient: recipientClientId,
        keys: keys.map((k) => ({ epoch: k.epoch, epk: k.epk, n: k.n, ct: k.ct })),
    });
}

function tryDecode(value: unknown, expectedLength: number): Uint8Array | null {
    if (typeof value !== "string") return null;
    try {
        const bytes = base64UrlToBytes(value);
        return bytes.length === expectedLength ? bytes : null;
    } catch {
        return null;
    }
}

/**
 * Parse and verify a key request. Returns `null` for any other event type, a malformed payload, a
 * public key that does not derive the author's client id, or a bad signature.
 */
export function tryParseKeyRequest(event: VestaEvent): ChannelKeyRequest | null {
    if (event.eventType !== KEY_REQUEST_EVENT_TYPE) return null;
    const payload = event.payload as Record<string, unknown> | null;
    if (typeof payload !== "object" || payload === null) return null;
    const publicKey = tryDecode(payload.publicKey, 32);
    const x25519PublicKey = tryDecode(payload.x25519PublicKey, 32);
    if (!publicKey || !x25519PublicKey) return null;
    if (deriveClientId(publicKey) !== event.clientId) return null;
    if (!event.signature || !verifyEvent(event, publicKey)) return null;
    return { clientId: event.clientId, publicKey, x25519PublicKey };
}

/**
 * Parse and verify a key grant against the admin keys the app trusts. Returns `null` for any other
 * event type, an author that is not a trusted admin, a bad signature or a malformed payload. The caller
 * still has to check `recipientClientId`.
 */
export function tryParseKeyGrant(event: VestaEvent, adminPublicKeys: Uint8Array[]): ChannelKeyGrant | null {
    if (event.eventType !== KEY_GRANT_EVENT_TYPE) return null;
    const admin = adminPublicKeys.find((k) => deriveClientId(k) === event.clientId);
    if (!admin || !event.signature || !verifyEvent(event, admin)) return null;
    const payload = event.payload as Record<string, unknown> | null;
    if (typeof payload !== "object" || payload === null) return null;
    if (typeof payload.recipient !== "string" || !Array.isArray(payload.keys) || payload.keys.length === 0) return null;
    const keys: SealedChannelKey[] = [];
    for (const item of payload.keys as unknown[]) {
        const k = item as Record<string, unknown> | null;
        if (typeof k !== "object" || k === null) return null;
        if (!Number.isInteger(k.epoch) || typeof k.epk !== "string" || typeof k.n !== "string" || typeof k.ct !== "string") return null;
        keys.push({ epoch: k.epoch as number, epk: k.epk, n: k.n, ct: k.ct });
    }
    return { adminClientId: event.clientId, recipientClientId: payload.recipient, keys };
}
