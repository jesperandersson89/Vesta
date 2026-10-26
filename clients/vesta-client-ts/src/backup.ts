/**
 * Identity backup and recovery (mirrors the C# `IdentityBackup`, `RelayBackup` and `RecoveryPhrase`;
 * blobs, phrases and relay channels are byte-for-byte interchangeable across the SDKs).
 *
 * - {@link exportIdentity} / {@link importIdentity}: password-encrypted blob (Argon2id + AES-256-GCM).
 * - {@link recoveryPhraseFromSeed} / {@link seedFromRecoveryPhrase}: 24 words (BIP-39 wordlist).
 * - {@link RelayBackup}: park the blob on a relay, recoverable with just (app, username, password).
 */

import { argon2idAsync } from "@noble/hashes/argon2";
import { sha256 } from "@noble/hashes/sha256";

import { BIP39_ENGLISH } from "./bip39-wordlist.js";
import { createEvent } from "./events.js";
import { VestaIdentity } from "./identity.js";
import { base64UrlToBytes, bytesToBase64Url } from "./signing.js";
import type { VestaEvent } from "./types.js";

export const BACKUP_FORMAT = "vesta-identity-backup";
export const BACKUP_VERSION = 1;
export const RELAY_BACKUP_EVENT_TYPE = "vesta.identity-backup";

const SALT_LENGTH = 16;
const NONCE_LENGTH = 12;
const TAG_LENGTH = 16;
const SEED_LENGTH = 32;
const MATERIAL_LENGTH = 64;

// Blobs are untrusted input: refuse parameters that would make opening one a denial of service.
const MIN_MEMORY_KIB = 8;
const MAX_MEMORY_KIB = 524_288;
const MAX_ITERATIONS = 10;

/** A backup cannot be opened: wrong password, corrupted or unsupported blob. */
export class IdentityBackupError extends Error {
    constructor(message: string) {
        super(message);
        this.name = "IdentityBackupError";
    }
}

/** A recovery phrase is malformed or its checksum does not match. */
export class RecoveryPhraseError extends Error {
    constructor(message: string) {
        super(message);
        this.name = "RecoveryPhraseError";
    }
}

/** Argon2id cost: memory in KiB (default 64 MiB) and passes (default 3); parallelism is fixed at 1. */
export interface IdentityBackupOptions {
    memoryKiB?: number;
    iterations?: number;
}

interface Cost {
    memoryKiB: number;
    iterations: number;
}

function resolveCost(options?: IdentityBackupOptions): Cost {
    return validate({ memoryKiB: options?.memoryKiB ?? 65536, iterations: options?.iterations ?? 3 });
}

function validate(cost: Cost): Cost {
    if (
        !Number.isInteger(cost.memoryKiB) ||
        !Number.isInteger(cost.iterations) ||
        cost.memoryKiB < MIN_MEMORY_KIB ||
        cost.memoryKiB > MAX_MEMORY_KIB ||
        cost.iterations < 1 ||
        cost.iterations > MAX_ITERATIONS
    ) {
        throw new IdentityBackupError(
            `Unsupported Argon2id cost (memory ${cost.memoryKiB} KiB, ${cost.iterations} passes).`,
        );
    }
    return cost;
}

const encoder = new TextEncoder();

// NFKC so the same password typed on different platforms and keyboards derives the same key.
async function deriveMaterial(password: string, salt: Uint8Array, cost: Cost): Promise<Uint8Array> {
    return argon2idAsync(encoder.encode(password.normalize("NFKC")), salt, {
        t: cost.iterations,
        m: cost.memoryKiB,
        p: 1,
        dkLen: MATERIAL_LENGTH,
    });
}

function aad(clientId: string): Uint8Array {
    return encoder.encode(`vesta-identity-backup-v1\n${clientId}`);
}

async function aesKey(material: Uint8Array, usage: "encrypt" | "decrypt"): Promise<CryptoKey> {
    return globalThis.crypto.subtle.importKey("raw", material.slice(0, 32), "AES-GCM", false, [usage]);
}

function randomBytes(length: number): Uint8Array {
    return globalThis.crypto.getRandomValues(new Uint8Array(length));
}

async function seal(
    identity: VestaIdentity,
    material: Uint8Array,
    salt: Uint8Array,
    cost: Cost,
): Promise<Record<string, unknown>> {
    const nonce = randomBytes(NONCE_LENGTH);
    const sealed = new Uint8Array(
        await globalThis.crypto.subtle.encrypt(
            { name: "AES-GCM", iv: nonce as BufferSource, additionalData: aad(identity.clientId) as BufferSource, tagLength: TAG_LENGTH * 8 },
            await aesKey(material, "encrypt"),
            identity.privateKey as BufferSource,
        ),
    );
    return {
        format: BACKUP_FORMAT,
        version: BACKUP_VERSION,
        clientId: identity.clientId,
        kdf: {
            name: "argon2id",
            memoryKiB: cost.memoryKiB,
            iterations: cost.iterations,
            parallelism: 1,
            salt: bytesToBase64Url(salt),
        },
        cipher: { name: "aes-256-gcm", nonce: bytesToBase64Url(nonce) },
        ciphertext: bytesToBase64Url(sealed),
    };
}

interface ParsedBlob {
    clientId: string;
    cost: Cost;
    salt: Uint8Array;
    nonce: Uint8Array;
    ciphertext: Uint8Array;
}

function malformed(): IdentityBackupError {
    return new IdentityBackupError("The backup is malformed.");
}

function parse(data: unknown): ParsedBlob {
    const root = data as Record<string, any> | null;
    if (typeof root !== "object" || root === null) throw malformed();
    if (root.format !== BACKUP_FORMAT) throw new IdentityBackupError("Not a Vesta identity backup.");
    if (root.version !== BACKUP_VERSION) {
        throw new IdentityBackupError(`Unsupported backup version ${String(root.version)}.`);
    }
    const kdf = root.kdf;
    const cipher = root.cipher;
    if (typeof kdf !== "object" || kdf === null || typeof cipher !== "object" || cipher === null) throw malformed();
    if (kdf.name !== "argon2id" || kdf.parallelism !== 1 || cipher.name !== "aes-256-gcm") {
        throw new IdentityBackupError("Unsupported backup algorithms.");
    }
    const cost = validate({ memoryKiB: kdf.memoryKiB, iterations: kdf.iterations });
    try {
        const salt = base64UrlToBytes(String(kdf.salt));
        const nonce = base64UrlToBytes(String(cipher.nonce));
        const ciphertext = base64UrlToBytes(String(root.ciphertext));
        if (
            typeof root.clientId !== "string" ||
            salt.length !== SALT_LENGTH ||
            nonce.length !== NONCE_LENGTH ||
            ciphertext.length !== SEED_LENGTH + TAG_LENGTH
        ) {
            throw malformed();
        }
        return { clientId: root.clientId, cost, salt, nonce, ciphertext };
    } catch (error) {
        throw error instanceof IdentityBackupError ? error : malformed();
    }
}

async function open(blob: ParsedBlob, material: Uint8Array): Promise<VestaIdentity> {
    let seed: Uint8Array;
    try {
        seed = new Uint8Array(
            await globalThis.crypto.subtle.decrypt(
                { name: "AES-GCM", iv: blob.nonce as BufferSource, additionalData: aad(blob.clientId) as BufferSource, tagLength: TAG_LENGTH * 8 },
                await aesKey(material, "decrypt"),
                blob.ciphertext as BufferSource,
            ),
        );
    } catch {
        throw new IdentityBackupError("Wrong password, or the backup is corrupted.");
    }
    const identity = VestaIdentity.fromPrivateKey(seed);
    if (identity.clientId !== blob.clientId) {
        throw new IdentityBackupError("The backup does not match its client id.");
    }
    return identity;
}

/** Encrypt `identity`'s seed under `password` into a versioned JSON blob (random salt). */
export async function exportIdentity(
    identity: VestaIdentity,
    password: string,
    options?: IdentityBackupOptions,
): Promise<string> {
    const cost = resolveCost(options);
    const salt = randomBytes(SALT_LENGTH);
    const material = await deriveMaterial(password, salt, cost);
    return JSON.stringify(await seal(identity, material, salt, cost));
}

/** Open a blob made by {@link exportIdentity}. Rejects with {@link IdentityBackupError} on failure. */
export async function importIdentity(blob: string, password: string): Promise<VestaIdentity> {
    let data: unknown;
    try {
        data = JSON.parse(blob);
    } catch {
        throw new IdentityBackupError("The backup is not valid JSON.");
    }
    const parsed = parse(data);
    return open(parsed, await deriveMaterial(password, parsed.salt, parsed.cost));
}

function toHex(bytes: Uint8Array): string {
    return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

function equalBytes(a: Uint8Array, b: Uint8Array): boolean {
    return a.length === b.length && a.every((value, index) => value === b[index]);
}

/**
 * A backup an app can park on a relay so a player who lost every device can recover with just
 * (username, password). The encryption key and the unguessable channel
 * (`vesta/identity-backup/{hex}`) come from one Argon2id run over (app, username, password). Anyone can
 * publish junk to the channel, so {@link RelayBackup.restore} trusts only blobs that decrypt.
 */
export class RelayBackup {
    readonly channelId: string;

    private constructor(
        private readonly material: Uint8Array,
        private readonly salt: Uint8Array,
        private readonly cost: Cost,
    ) {
        this.channelId = `vesta/identity-backup/${toHex(material.slice(32, 48))}`;
    }

    /** Derive keys and channel. The username is trimmed, NFKC-normalised and lower-cased. */
    static async derive(
        appId: string,
        username: string,
        password: string,
        options?: IdentityBackupOptions,
    ): Promise<RelayBackup> {
        if (!appId || !username) throw new Error("appId and username are required");
        const cost = resolveCost(options);
        const normalized = username.trim().normalize("NFKC").toLowerCase();
        const salt = sha256(encoder.encode(`vesta-backup-v1\n${appId}\n${normalized}`)).slice(0, SALT_LENGTH);
        return new RelayBackup(await deriveMaterial(password, salt, cost), salt, cost);
    }

    /** A signed, replace-style event carrying the encrypted identity; publish it on `channelId`. */
    async createEvent(identity: VestaIdentity): Promise<VestaEvent> {
        const payload = await seal(identity, this.material, this.salt, this.cost);
        return createEvent(this.channelId, identity, RELAY_BACKUP_EVENT_TYPE, payload, { replace: true });
    }

    /** The identity from the first event on `channelId` that decrypts under the derived key, or null. */
    async restore(events: Iterable<VestaEvent>): Promise<VestaIdentity | null> {
        for (const event of events) {
            if (event.eventType !== RELAY_BACKUP_EVENT_TYPE || event.channelId !== this.channelId) continue;
            try {
                const blob = parse(event.payload);
                if (
                    !equalBytes(blob.salt, this.salt) ||
                    blob.cost.memoryKiB !== this.cost.memoryKiB ||
                    blob.cost.iterations !== this.cost.iterations
                ) {
                    continue;
                }
                return await open(blob, this.material);
            } catch (error) {
                if (!(error instanceof IdentityBackupError)) throw error;
            }
        }
        return null;
    }
}

const WORD_INDEX = new Map<string, number>(BIP39_ENGLISH.map((word, index) => [word, index]));
const PHRASE_WORDS = 24;

/** The 24-word phrase (BIP-39 entropy-to-mnemonic, no PBKDF2) for a 32-byte seed. */
export function recoveryPhraseFromSeed(seed: Uint8Array): string {
    if (seed.length !== SEED_LENGTH) throw new Error(`A recovery phrase encodes exactly ${SEED_LENGTH} bytes.`);
    const data = new Uint8Array(SEED_LENGTH + 1);
    data.set(seed);
    data[SEED_LENGTH] = sha256(seed)[0]!;
    let bits = 0n;
    for (const byte of data) bits = (bits << 8n) | BigInt(byte);
    const words: string[] = [];
    for (let i = 0; i < PHRASE_WORDS; i++) {
        words.unshift(BIP39_ENGLISH[Number(bits & 0x7ffn)]!);
        bits >>= 11n;
    }
    return words.join(" ");
}

/** The seed for a phrase; case and extra whitespace are ignored. Throws {@link RecoveryPhraseError}. */
export function seedFromRecoveryPhrase(phrase: string): Uint8Array {
    const words = phrase.trim().split(/\s+/).filter((w) => w.length > 0);
    if (words.length !== PHRASE_WORDS) {
        throw new RecoveryPhraseError(`A recovery phrase has ${PHRASE_WORDS} words, got ${words.length}.`);
    }
    let bits = 0n;
    for (const raw of words) {
        const index = WORD_INDEX.get(raw.toLowerCase());
        if (index === undefined) throw new RecoveryPhraseError(`'${raw}' is not a recovery-phrase word.`);
        bits = (bits << 11n) | BigInt(index);
    }
    const data = new Uint8Array(SEED_LENGTH + 1);
    for (let i = SEED_LENGTH; i >= 0; i--) {
        data[i] = Number(bits & 0xffn);
        bits >>= 8n;
    }
    const seed = data.slice(0, SEED_LENGTH);
    if (sha256(seed)[0] !== data[SEED_LENGTH]) {
        throw new RecoveryPhraseError(
            "The recovery phrase checksum does not match; check for a mistyped or misordered word.",
        );
    }
    return seed;
}
