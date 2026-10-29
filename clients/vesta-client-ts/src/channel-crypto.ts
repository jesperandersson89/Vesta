/**
 * Encryption primitives for end-to-end encrypted channels (PLANNING #33, phase B).
 *
 * Byte-compatible with the C# `ChannelCrypto` and the Python `channel_crypto.py`; the shared
 * vectors live in `tests/channel-crypto.test.mjs`.
 *
 * - `ChannelKeyPair`: a member's X25519 pair, derived from the Ed25519 seed with
 *   `HKDF-SHA256(ikm = seed, salt = "", info = "vesta-x25519-v1")` so no second secret is stored.
 * - `encryptPayload` / `decryptPayload`: AES-256-GCM over the RFC 8785 canonical JSON of the app
 *   payload, 96-bit nonce, AAD `"vesta-enc-v1\n{channelId}\n{eventId}\n{eventType}"`, carried as the
 *   `{ enc: 1, kid, n, ct }` envelope the relay enforces.
 * - `sealKey` / `openKey`: a channel key sealed to one recipient — ephemeral X25519 × recipient
 *   X25519 → HKDF-SHA256(salt = epk ‖ recipientPub, info = "vesta-channel-key-seal-v1") → AES-256-GCM
 *   over the 32-byte key, AAD `"vesta-channel-key-v1\n{channelId}\n{epoch}\n{recipientClientId}"`.
 *
 * Signing is unchanged: the Ed25519 signature covers the envelope. AES-GCM goes through WebCrypto
 * (async, available in Node ≥ 22 and every browser); X25519 and HKDF are `@noble` (sync).
 */

import { x25519 } from "@noble/curves/ed25519";
import { hkdf } from "@noble/hashes/hkdf";
import { sha256 } from "@noble/hashes/sha256";

import type { VestaIdentity } from "./identity.js";
import { base64UrlToBytes, bytesToBase64Url, canonicalize } from "./signing.js";

export const ENVELOPE_VERSION = 1;
export const NONCE_BYTES = 12;
export const KEY_BYTES = 32;
const TAG_BYTES = 16;
const DERIVATION_INFO = "vesta-x25519-v1";
const PAYLOAD_AAD_PREFIX = "vesta-enc-v1";
const SEAL_AAD_PREFIX = "vesta-channel-key-v1";
const SEAL_INFO = "vesta-channel-key-seal-v1";

const encoder = new TextEncoder();
const decoder = new TextDecoder();

/** Decryption or unsealing failed: wrong key, wrong recipient, tampered data or malformed input. */
export class ChannelCryptoError extends Error {
    constructor(message: string) {
        super(message);
        this.name = "ChannelCryptoError";
    }
}

/** The relay-enforced payload envelope on an encrypted channel. */
export interface EncryptedEnvelope {
    enc: number;
    kid: number;
    n: string;
    ct: string;
}

/** A channel's symmetric key for one epoch: 32 random bytes plus the integer epoch carried as `kid`. */
export interface ChannelKey {
    epoch: number;
    key: Uint8Array;
}

/** A channel key sealed to one recipient, as carried inside a `vesta.channel-key` event. */
export interface SealedChannelKey {
    epoch: number;
    epk: string;
    n: string;
    ct: string;
}

/** A member's X25519 key agreement pair. */
export class ChannelKeyPair {
    readonly publicKey: Uint8Array;
    private readonly privateKey: Uint8Array;

    private constructor(privateKey: Uint8Array) {
        this.privateKey = privateKey;
        this.publicKey = x25519.getPublicKey(privateKey);
    }

    /** Derive the pair from a Vesta identity (its 32-byte Ed25519 seed). */
    static fromIdentity(identity: VestaIdentity): ChannelKeyPair {
        return ChannelKeyPair.fromSeed(identity.privateKey);
    }

    /** Derive the pair from a raw 32-byte Ed25519 seed. */
    static fromSeed(seed: Uint8Array): ChannelKeyPair {
        if (seed.length !== 32) throw new Error("Seed must be 32 bytes.");
        return ChannelKeyPair.fromPrivateKey(hkdf(sha256, seed, undefined, encoder.encode(DERIVATION_INFO), 32));
    }

    /** Wrap a raw 32-byte X25519 private key (ephemeral keys, test vectors). */
    static fromPrivateKey(privateKey: Uint8Array): ChannelKeyPair {
        if (privateKey.length !== 32) throw new Error("X25519 private key must be 32 bytes.");
        return new ChannelKeyPair(Uint8Array.from(privateKey));
    }

    /** A fresh random pair (ephemeral keys for sealing). */
    static generate(): ChannelKeyPair {
        return new ChannelKeyPair(x25519.utils.randomPrivateKey());
    }

    /** The raw 32-byte private scalar. Only needed for tests and key stores. */
    exportPrivateKey(): Uint8Array {
        return Uint8Array.from(this.privateKey);
    }

    /** Raw X25519 shared secret with `peerPublicKey`. */
    agree(peerPublicKey: Uint8Array): Uint8Array {
        return x25519.getSharedSecret(this.privateKey, peerPublicKey);
    }
}

/** A fresh random channel key for `epoch`. */
export function generateChannelKey(epoch: number): ChannelKey {
    if (!Number.isInteger(epoch) || epoch < 0) throw new Error("Epoch must be a non-negative integer.");
    return { epoch, key: globalThis.crypto.getRandomValues(new Uint8Array(KEY_BYTES)) };
}

/** Mirror of the relay's shape check: exactly `{ enc: 1, kid: int ≥ 0, n: b64url(12), ct: b64url(≥ 1) }`. */
export function isEnvelope(payload: unknown): payload is EncryptedEnvelope {
    if (typeof payload !== "object" || payload === null || Array.isArray(payload)) return false;
    const keys: string[] = Object.keys(payload).sort();
    if (keys.join(",") !== "ct,enc,kid,n") return false;
    const { enc, kid, n, ct } = payload as Record<string, unknown>;
    if (enc !== ENVELOPE_VERSION) return false;
    if (typeof kid !== "number" || !Number.isInteger(kid) || kid < 0) return false;
    if (typeof n !== "string" || typeof ct !== "string") return false;
    try {
        return base64UrlToBytes(n).length === NONCE_BYTES && base64UrlToBytes(ct).length >= 1;
    } catch {
        return false;
    }
}

/** The epoch (`kid`) an envelope was encrypted under, so the caller can pick the key. */
export function readEpoch(envelope: unknown): number {
    if (!isEnvelope(envelope)) throw new ChannelCryptoError("Payload is not an encrypted envelope.");
    return envelope.kid;
}

/** Encrypt an app payload into an envelope. `nonce` is random unless pinned (tests, vectors). */
export async function encryptPayload(
    key: ChannelKey,
    channelId: string,
    eventId: string,
    eventType: string,
    payload: unknown,
    nonce?: Uint8Array,
): Promise<EncryptedEnvelope> {
    if (key.key.length !== KEY_BYTES) throw new Error("Channel key must be 32 bytes.");
    const iv: Uint8Array = nonce ?? globalThis.crypto.getRandomValues(new Uint8Array(NONCE_BYTES));
    if (iv.length !== NONCE_BYTES) throw new Error(`Nonce must be ${NONCE_BYTES} bytes.`);
    const plaintext: Uint8Array = encoder.encode(canonicalize(payload));
    const ct = new Uint8Array(
        await globalThis.crypto.subtle.encrypt(
            { name: "AES-GCM", iv: iv as BufferSource, additionalData: payloadAad(channelId, eventId, eventType) as BufferSource, tagLength: TAG_BYTES * 8 },
            await aesKey(key.key, "encrypt"),
            plaintext as BufferSource,
        ),
    );
    return { enc: ENVELOPE_VERSION, kid: key.epoch, n: bytesToBase64Url(iv), ct: bytesToBase64Url(ct) };
}

/** Decrypt an envelope back to the app payload; throws `ChannelCryptoError` on any mismatch. */
export async function decryptPayload(
    key: ChannelKey,
    channelId: string,
    eventId: string,
    eventType: string,
    envelope: unknown,
): Promise<unknown> {
    const epoch: number = readEpoch(envelope);
    if (epoch !== key.epoch) {
        throw new ChannelCryptoError(`Envelope uses key epoch ${epoch}, but key epoch ${key.epoch} was supplied.`);
    }
    const env = envelope as EncryptedEnvelope;
    const ct: Uint8Array = base64UrlToBytes(env.ct);
    if (ct.length < TAG_BYTES) throw new ChannelCryptoError("Ciphertext shorter than the authentication tag.");
    let plaintext: ArrayBuffer;
    try {
        plaintext = await globalThis.crypto.subtle.decrypt(
            { name: "AES-GCM", iv: base64UrlToBytes(env.n) as BufferSource, additionalData: payloadAad(channelId, eventId, eventType) as BufferSource, tagLength: TAG_BYTES * 8 },
            await aesKey(key.key, "decrypt"),
            ct as BufferSource,
        );
    } catch {
        throw new ChannelCryptoError("Payload decryption failed.");
    }
    return JSON.parse(decoder.decode(plaintext));
}

/** Seal a channel key to a recipient's X25519 public key. `ephemeral` / `nonce` are random unless pinned. */
export async function sealKey(
    key: ChannelKey,
    channelId: string,
    recipientClientId: string,
    recipientPublicKey: Uint8Array,
    ephemeral?: ChannelKeyPair,
    nonce?: Uint8Array,
): Promise<SealedChannelKey> {
    if (key.key.length !== KEY_BYTES) throw new Error("Channel key must be 32 bytes.");
    if (recipientPublicKey.length !== 32) throw new Error("Recipient X25519 public key must be 32 bytes.");
    const eph: ChannelKeyPair = ephemeral ?? ChannelKeyPair.generate();
    const iv: Uint8Array = nonce ?? globalThis.crypto.getRandomValues(new Uint8Array(NONCE_BYTES));
    if (iv.length !== NONCE_BYTES) throw new Error(`Nonce must be ${NONCE_BYTES} bytes.`);
    const wrapKey: Uint8Array = deriveWrapKey(eph.agree(recipientPublicKey), eph.publicKey, recipientPublicKey);
    const ct = new Uint8Array(
        await globalThis.crypto.subtle.encrypt(
            { name: "AES-GCM", iv: iv as BufferSource, additionalData: sealAad(channelId, key.epoch, recipientClientId) as BufferSource, tagLength: TAG_BYTES * 8 },
            await aesKey(wrapKey, "encrypt"),
            key.key as BufferSource,
        ),
    );
    return { epoch: key.epoch, epk: bytesToBase64Url(eph.publicKey), n: bytesToBase64Url(iv), ct: bytesToBase64Url(ct) };
}

/** Open a sealed key with the recipient's pair; throws `ChannelCryptoError` if it was sealed to someone else. */
export async function openKey(
    sealed: SealedChannelKey,
    channelId: string,
    recipientClientId: string,
    recipient: ChannelKeyPair,
): Promise<ChannelKey> {
    let epk: Uint8Array;
    let iv: Uint8Array;
    let ct: Uint8Array;
    try {
        epk = base64UrlToBytes(sealed.epk);
        iv = base64UrlToBytes(sealed.n);
        ct = base64UrlToBytes(sealed.ct);
    } catch {
        throw new ChannelCryptoError("Malformed sealed channel key.");
    }
    if (epk.length !== 32 || iv.length !== NONCE_BYTES || ct.length !== KEY_BYTES + TAG_BYTES) {
        throw new ChannelCryptoError("Malformed sealed channel key.");
    }
    const wrapKey: Uint8Array = deriveWrapKey(recipient.agree(epk), epk, recipient.publicKey);
    try {
        const key = new Uint8Array(
            await globalThis.crypto.subtle.decrypt(
                { name: "AES-GCM", iv: iv as BufferSource, additionalData: sealAad(channelId, sealed.epoch, recipientClientId) as BufferSource, tagLength: TAG_BYTES * 8 },
                await aesKey(wrapKey, "decrypt"),
                ct as BufferSource,
            ),
        );
        return { epoch: sealed.epoch, key };
    } catch {
        throw new ChannelCryptoError("Sealed channel key was not sealed to this recipient.");
    }
}

function payloadAad(channelId: string, eventId: string, eventType: string): Uint8Array {
    return encoder.encode(`${PAYLOAD_AAD_PREFIX}\n${channelId}\n${eventId.toLowerCase()}\n${eventType}`);
}

function sealAad(channelId: string, epoch: number, recipientClientId: string): Uint8Array {
    return encoder.encode(`${SEAL_AAD_PREFIX}\n${channelId}\n${epoch}\n${recipientClientId}`);
}

function deriveWrapKey(sharedSecret: Uint8Array, ephemeralPublicKey: Uint8Array, recipientPublicKey: Uint8Array): Uint8Array {
    const salt = new Uint8Array(ephemeralPublicKey.length + recipientPublicKey.length);
    salt.set(ephemeralPublicKey, 0);
    salt.set(recipientPublicKey, ephemeralPublicKey.length);
    return hkdf(sha256, sharedSecret, salt, encoder.encode(SEAL_INFO), 32);
}

async function aesKey(raw: Uint8Array, usage: "encrypt" | "decrypt"): Promise<CryptoKey> {
    return globalThis.crypto.subtle.importKey("raw", raw as BufferSource, "AES-GCM", false, [usage]);
}
