/**
 * Where a client keeps the channel keys it has received or created, plus (for admins) the X25519 public
 * keys of the members it has seen, so it can seal new epochs to them after a rotation. Keys are stored in
 * the clear: the device already holds them to read the channel. Pluggable like `ProjectionStore`;
 * `FileChannelKeyStore` lives in `vesta-client/node`.
 */

import type { ChannelKey } from "./channel-crypto.js";
import { base64UrlToBytes, bytesToBase64Url } from "./signing.js";

export interface ChannelKeyStore {
    /** Every epoch key known for the channel, any order. */
    loadKeys(channelId: string): Promise<ChannelKey[]>;
    /** Save (or overwrite) one epoch key. */
    saveKey(channelId: string, key: ChannelKey): Promise<void>;
    /** Members the admin has seen on the channel: client id → X25519 public key. */
    loadMembers(channelId: string): Promise<Map<string, Uint8Array>>;
    /** Remember (or update) a member's X25519 public key. */
    saveMember(channelId: string, clientId: string, x25519PublicKey: Uint8Array): Promise<void>;
    /** Forget a member (after revocation) so the next rotation is not sealed to it. */
    removeMember(channelId: string, clientId: string): Promise<void>;
}

/** Serialised form shared by the persistent stores. */
export interface ChannelKeyStoreRecord {
    keys: Record<string, string>; // epoch → base64url key
    members: Record<string, string>; // clientId → base64url X25519 public key
}

export function encodeRecord(keys: Map<number, Uint8Array>, members: Map<string, Uint8Array>): ChannelKeyStoreRecord {
    const record: ChannelKeyStoreRecord = { keys: {}, members: {} };
    for (const [epoch, key] of keys) record.keys[String(epoch)] = bytesToBase64Url(key);
    for (const [clientId, key] of members) record.members[clientId] = bytesToBase64Url(key);
    return record;
}

export function decodeRecord(record: ChannelKeyStoreRecord | null): { keys: Map<number, Uint8Array>; members: Map<string, Uint8Array> } {
    const keys = new Map<number, Uint8Array>();
    const members = new Map<string, Uint8Array>();
    if (!record) return { keys, members };
    for (const [epoch, key] of Object.entries(record.keys ?? {})) keys.set(Number(epoch), base64UrlToBytes(key));
    for (const [clientId, key] of Object.entries(record.members ?? {})) members.set(clientId, base64UrlToBytes(key));
    return { keys, members };
}

/** Volatile store for tests and throwaway sessions; keys are lost on reload. */
export class InMemoryChannelKeyStore implements ChannelKeyStore {
    private readonly keys = new Map<string, Map<number, Uint8Array>>();
    private readonly members = new Map<string, Map<string, Uint8Array>>();

    async loadKeys(channelId: string): Promise<ChannelKey[]> {
        return [...(this.keys.get(channelId) ?? new Map<number, Uint8Array>())].map(([epoch, key]) => ({ epoch, key }));
    }

    async saveKey(channelId: string, key: ChannelKey): Promise<void> {
        let byEpoch = this.keys.get(channelId);
        if (!byEpoch) this.keys.set(channelId, (byEpoch = new Map()));
        byEpoch.set(key.epoch, Uint8Array.from(key.key));
    }

    async loadMembers(channelId: string): Promise<Map<string, Uint8Array>> {
        return new Map(this.members.get(channelId) ?? []);
    }

    async saveMember(channelId: string, clientId: string, x25519PublicKey: Uint8Array): Promise<void> {
        let byClient = this.members.get(channelId);
        if (!byClient) this.members.set(channelId, (byClient = new Map()));
        byClient.set(clientId, Uint8Array.from(x25519PublicKey));
    }

    async removeMember(channelId: string, clientId: string): Promise<void> {
        this.members.get(channelId)?.delete(clientId);
    }
}

/** A `localStorage`-backed store for browser apps (one entry per channel under `keyPrefix`). */
export class LocalStorageChannelKeyStore implements ChannelKeyStore {
    constructor(private readonly keyPrefix: string) {}

    private read(channelId: string): { keys: Map<number, Uint8Array>; members: Map<string, Uint8Array> } {
        const raw = globalThis.localStorage?.getItem(`${this.keyPrefix}:${channelId}`);
        try {
            return decodeRecord(raw ? (JSON.parse(raw) as ChannelKeyStoreRecord) : null);
        } catch {
            return decodeRecord(null);
        }
    }

    private write(channelId: string, keys: Map<number, Uint8Array>, members: Map<string, Uint8Array>): void {
        globalThis.localStorage?.setItem(`${this.keyPrefix}:${channelId}`, JSON.stringify(encodeRecord(keys, members)));
    }

    async loadKeys(channelId: string): Promise<ChannelKey[]> {
        return [...this.read(channelId).keys].map(([epoch, key]) => ({ epoch, key }));
    }

    async saveKey(channelId: string, key: ChannelKey): Promise<void> {
        const { keys, members } = this.read(channelId);
        keys.set(key.epoch, Uint8Array.from(key.key));
        this.write(channelId, keys, members);
    }

    async loadMembers(channelId: string): Promise<Map<string, Uint8Array>> {
        return this.read(channelId).members;
    }

    async saveMember(channelId: string, clientId: string, x25519PublicKey: Uint8Array): Promise<void> {
        const { keys, members } = this.read(channelId);
        members.set(clientId, Uint8Array.from(x25519PublicKey));
        this.write(channelId, keys, members);
    }

    async removeMember(channelId: string, clientId: string): Promise<void> {
        const { keys, members } = this.read(channelId);
        members.delete(clientId);
        this.write(channelId, keys, members);
    }
}
