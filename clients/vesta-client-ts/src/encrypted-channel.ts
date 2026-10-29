/**
 * An end-to-end encrypted channel on a `VestaConnection` (PLANNING #33 phase C). Encrypts on `publish`,
 * decrypts incoming events into the `event` listener (payload replaced by the plaintext; the original
 * ciphertext event is `sealed`), requests keys from an admin when it has none, buffers events whose epoch
 * key has not arrived, and — when opened as admin — answers key requests and rotates keys. The relay never
 * sees a key. Mirrors the C# `EncryptedChannel`.
 */

import {
    ChannelCryptoError,
    ChannelKeyPair,
    decryptPayload,
    encryptPayload,
    generateChannelKey,
    openKey,
    readEpoch,
    sealKey,
} from "./channel-crypto.js";
import type { ChannelKey, SealedChannelKey } from "./channel-crypto.js";
import {
    buildKeyGrant,
    buildKeyRequest,
    KEY_GRANT_EVENT_TYPE,
    KEY_REQUEST_EVENT_TYPE,
    tryParseKeyGrant,
    tryParseKeyRequest,
} from "./channel-key-events.js";
import type { ChannelKeyRequest } from "./channel-key-events.js";
import { InMemoryChannelKeyStore } from "./channel-key-store.js";
import type { ChannelKeyStore } from "./channel-key-store.js";
import type { VestaConnection } from "./connection.js";
import type { VestaIdentity } from "./identity.js";
import { signEvent } from "./signing.js";
import type { EventMessage, EventsBatchMessage, SequencedEvent, VestaEvent } from "./types.js";

export interface EncryptedChannelOptions {
    /** Where keys (and, for admins, member keys) live. Defaults to `InMemoryChannelKeyStore`. */
    keyStore?: ChannelKeyStore;
    /** Ed25519 public keys of the admins whose `vesta.channel-key` events this client trusts. */
    adminPublicKeys?: Uint8Array[];
    /** This identity is a channel admin: it answers key requests and can rotate the key. */
    isAdmin?: boolean;
    /** Admin-only hook to approve a key request (the relay already restricts publishing to members). */
    approveKeyRequest?: (request: ChannelKeyRequest) => boolean;
    /** Catch-up position for the subscription. Defaults to 0 so historical key grants are replayed. */
    fromSequence?: number;
    /** Always publish a key request on open, even when the store already holds keys. */
    alwaysRequestKeys?: boolean;
    /** How long `createEncryptedChannel` waits for the relay's control ACK (ms). */
    createTimeoutMs?: number;
}

/** A decrypted event: `event` carries the plaintext payload, `sealed` the original. */
export interface DecryptedEvent {
    event: SequencedEvent;
    sealed: SequencedEvent;
}

export interface EncryptedChannelEvents {
    /** Every app event on the channel once decrypted, in sequence order per epoch. */
    event: (decrypted: DecryptedEvent) => void;
    /** An event could not be decrypted with the key it names (tampering or a stale key). */
    decryptFailed: (sealed: SequencedEvent, error: unknown) => void;
    /** A new epoch key became available (received from an admin, or created locally). */
    keyAvailable: (epoch: number) => void;
    /** Admin only: a key request was answered. */
    keyGranted: (request: ChannelKeyRequest) => void;
}

type EventKey = keyof EncryptedChannelEvents;

export class EncryptedChannel {
    readonly channelId: string;
    private readonly connection: VestaConnection;
    private readonly identity: VestaIdentity;
    private readonly keyPair: ChannelKeyPair;
    private readonly store: ChannelKeyStore;
    private readonly options: EncryptedChannelOptions;
    private readonly adminKeys: Uint8Array[];
    private readonly keys = new Map<number, ChannelKey>();
    private readonly pending = new Map<number, Map<number, SequencedEvent>>();
    private readonly grantedEpochs = new Map<string, number>();
    private readonly listeners = new Map<EventKey, Set<(...args: never[]) => void>>();
    private keyWaiters: Array<() => void> = [];
    private adminChain: Promise<void> = Promise.resolve();
    private readonly onEvent = (msg: EventMessage): void => {
        if (msg.channelId !== this.channelId) return;
        void this.process([{ event: msg.event, sequence: msg.sequence, receivedAt: msg.receivedAt }], true);
    };
    private readonly onBatch = (msg: EventsBatchMessage): void => {
        if (msg.channelId !== this.channelId || msg.events.length === 0) return;
        void this.process(msg.events, false);
    };

    /** @internal Use `connection.createEncryptedChannel` / `openEncryptedChannel`. */
    constructor(connection: VestaConnection, identity: VestaIdentity, channelId: string, options: EncryptedChannelOptions) {
        this.connection = connection;
        this.identity = identity;
        this.channelId = channelId;
        this.options = options;
        this.store = options.keyStore ?? new InMemoryChannelKeyStore();
        this.keyPair = ChannelKeyPair.fromIdentity(identity);
        this.adminKeys = [...(options.adminPublicKeys ?? [])];
        if (options.isAdmin && !this.adminKeys.some((k) => equalBytes(k, identity.publicKey))) {
            this.adminKeys.push(identity.publicKey);
        }
        if (!options.isAdmin && this.adminKeys.length === 0) {
            throw new Error("A member must be given at least one admin public key to trust.");
        }
        connection.on("event", this.onEvent);
        connection.on("eventsBatch", this.onBatch);
    }

    /** Whether this client acts as a channel admin. */
    get isAdmin(): boolean {
        return this.options.isAdmin === true;
    }

    /** The newest epoch this client holds a key for, or `null` before the first key arrives. */
    get currentEpoch(): number | null {
        return this.keys.size === 0 ? null : Math.max(...this.keys.keys());
    }

    /** True once at least one epoch key is held. */
    get hasKeys(): boolean {
        return this.keys.size > 0;
    }

    on<K extends EventKey>(event: K, listener: EncryptedChannelEvents[K]): this {
        let set = this.listeners.get(event);
        if (!set) this.listeners.set(event, (set = new Set()));
        set.add(listener as (...args: never[]) => void);
        return this;
    }

    off<K extends EventKey>(event: K, listener: EncryptedChannelEvents[K]): this {
        this.listeners.get(event)?.delete(listener as (...args: never[]) => void);
        return this;
    }

    private emit<K extends EventKey>(event: K, ...args: Parameters<EncryptedChannelEvents[K]>): void {
        for (const listener of this.listeners.get(event) ?? []) {
            (listener as unknown as (...a: Parameters<EncryptedChannelEvents[K]>) => void)(...args);
        }
    }

    /** Wait until a key is available (resolves immediately when one already is). */
    waitForKey(timeoutMs = 10_000): Promise<void> {
        if (this.keys.size > 0) return Promise.resolve();
        return new Promise<void>((resolve, reject) => {
            const timer = setTimeout(() => {
                this.keyWaiters = this.keyWaiters.filter((w) => w !== done);
                reject(new Error("Timed out waiting for a channel key."));
            }, timeoutMs);
            const done = (): void => {
                clearTimeout(timer);
                resolve();
            };
            this.keyWaiters.push(done);
        });
    }

    /** @internal */
    async initialize(): Promise<void> {
        for (const key of await this.store.loadKeys(this.channelId)) this.addKey(key, false);
        this.connection.subscribe(this.channelId, this.options.fromSequence ?? 0);
        if (!this.isAdmin && (this.options.alwaysRequestKeys || !this.hasKeys)) {
            this.connection.publish(buildKeyRequest(this.identity, this.channelId, this.keyPair));
        }
    }

    /** @internal Admin only: create and store the first epoch key. */
    async createInitialKey(): Promise<void> {
        const key = generateChannelKey(1);
        await this.store.saveKey(this.channelId, key);
        this.addKey(key, true);
    }

    /** Encrypt `payload` under the newest key and publish it. */
    async publish(
        eventType: string,
        payload: unknown,
        options?: { replace?: boolean; metadata?: Record<string, unknown>; parentId?: string },
    ): Promise<VestaEvent> {
        if (eventType.startsWith("vesta.")) {
            throw new Error("Application events on an encrypted channel must not use the reserved 'vesta.' prefix.");
        }
        const epoch = this.currentEpoch;
        if (epoch === null) throw new Error("No channel key yet; await waitForKey() before publishing.");
        const key = this.keys.get(epoch)!;
        const id = globalThis.crypto.randomUUID();
        const envelope = await encryptPayload(key, this.channelId, id, eventType, payload);
        const event: VestaEvent = {
            id,
            channelId: this.channelId,
            timestamp: new Date().toISOString(),
            clientId: this.identity.clientId,
            eventType,
            payload: envelope,
            replace: options?.replace,
            parentId: options?.parentId,
            metadata: options?.metadata,
        };
        signEvent(event, this.identity.privateKey);
        this.connection.publish(event);
        return event;
    }

    /** Admin only: start a new epoch and seal it to every member in the store. */
    rotateKey(): Promise<void> {
        this.requireAdmin();
        return this.serialAdmin(async () => {
            const key = generateChannelKey((this.currentEpoch ?? 0) + 1);
            await this.store.saveKey(this.channelId, key);
            this.addKey(key, true);
            for (const [clientId, x25519] of await this.store.loadMembers(this.channelId)) {
                await this.publishGrant(clientId, x25519, [key]);
            }
        });
    }

    /** Admin only: revoke the member on the relay, forget its key, and rotate so it cannot read new events. */
    async removeMember(clientId: string): Promise<void> {
        this.requireAdmin();
        this.connection.revokeAccess(this.channelId, clientId);
        await this.store.removeMember(this.channelId, clientId);
        this.grantedEpochs.delete(clientId);
        await this.rotateKey();
    }

    /** Stop listening on the connection. */
    close(): void {
        this.connection.off("event", this.onEvent);
        this.connection.off("eventsBatch", this.onBatch);
    }

    private async process(events: SequencedEvent[], live: boolean): Promise<void> {
        try {
            // Pass 1: grants first, so a historical request that was already answered is not answered again.
            for (const sequenced of events) {
                if (sequenced.event.eventType !== KEY_GRANT_EVENT_TYPE) continue;
                const grant = tryParseKeyGrant(sequenced.event, this.adminKeys);
                if (!grant) continue;
                this.noteGranted(grant.recipientClientId, Math.max(...grant.keys.map((k) => k.epoch)));
                if (grant.recipientClientId === this.identity.clientId) await this.openGrant(grant.keys);
            }
            // Pass 2: requests (admin only).
            if (this.isAdmin) {
                for (const sequenced of events) {
                    if (sequenced.event.eventType !== KEY_REQUEST_EVENT_TYPE) continue;
                    const request = tryParseKeyRequest(sequenced.event);
                    if (!request || request.clientId === this.identity.clientId) continue;
                    await this.answerRequest(request);
                }
            }
            // Pass 3: app events, in order.
            for (const sequenced of events) {
                if (sequenced.event.eventType.startsWith("vesta.")) continue;
                await this.deliver(sequenced);
            }
        } catch (error) {
            if (!live) this.emit("decryptFailed", events[0], error);
        }
    }

    private async openGrant(sealedKeys: SealedChannelKey[]): Promise<void> {
        for (const sealed of sealedKeys) {
            if (this.keys.has(sealed.epoch)) continue;
            let key: ChannelKey;
            try {
                key = await openKey(sealed, this.channelId, this.identity.clientId, this.keyPair);
            } catch (error) {
                if (error instanceof ChannelCryptoError) continue;
                throw error;
            }
            await this.store.saveKey(this.channelId, key);
            this.addKey(key, true);
        }
    }

    private answerRequest(request: ChannelKeyRequest): Promise<void> {
        if (this.options.approveKeyRequest && !this.options.approveKeyRequest(request)) return Promise.resolve();
        return this.serialAdmin(async () => {
            await this.store.saveMember(this.channelId, request.clientId, request.x25519PublicKey);
            const current = this.currentEpoch;
            if (current === null) return;
            if ((this.grantedEpochs.get(request.clientId) ?? -1) >= current) return;
            const keys = [...this.keys.values()].sort((a, b) => a.epoch - b.epoch);
            await this.publishGrant(request.clientId, request.x25519PublicKey, keys);
            this.emit("keyGranted", request);
        });
    }

    private async publishGrant(recipientClientId: string, recipientX25519: Uint8Array, keys: ChannelKey[]): Promise<void> {
        const sealedKeys: SealedChannelKey[] = [];
        for (const key of keys) sealedKeys.push(await sealKey(key, this.channelId, recipientClientId, recipientX25519));
        this.noteGranted(recipientClientId, Math.max(...keys.map((k) => k.epoch)));
        this.connection.publish(buildKeyGrant(this.identity, this.channelId, recipientClientId, sealedKeys));
    }

    private noteGranted(recipientClientId: string, epoch: number): void {
        if ((this.grantedEpochs.get(recipientClientId) ?? -1) < epoch) this.grantedEpochs.set(recipientClientId, epoch);
    }

    private async deliver(sequenced: SequencedEvent): Promise<void> {
        let epoch: number;
        try {
            epoch = readEpoch(sequenced.event.payload);
        } catch (error) {
            this.emit("decryptFailed", sequenced, error);
            return;
        }
        const key = this.keys.get(epoch);
        if (!key) {
            let queue = this.pending.get(epoch);
            if (!queue) this.pending.set(epoch, (queue = new Map()));
            queue.set(sequenced.sequence, sequenced);
            return;
        }
        await this.decrypt(sequenced, key);
    }

    private async decrypt(sequenced: SequencedEvent, key: ChannelKey): Promise<void> {
        try {
            const plaintext = await decryptPayload(key, this.channelId, sequenced.event.id, sequenced.event.eventType, sequenced.event.payload);
            const event: SequencedEvent = { ...sequenced, event: { ...sequenced.event, payload: plaintext } };
            this.emit("event", { event, sealed: sequenced });
        } catch (error) {
            this.emit("decryptFailed", sequenced, error);
        }
    }

    private addKey(key: ChannelKey, announce: boolean): void {
        this.keys.set(key.epoch, key);
        const waiters = this.keyWaiters;
        this.keyWaiters = [];
        for (const w of waiters) w();
        if (announce) this.emit("keyAvailable", key.epoch);
        const queue = this.pending.get(key.epoch);
        if (queue) {
            this.pending.delete(key.epoch);
            const ordered = [...queue.values()].sort((a, b) => a.sequence - b.sequence);
            void (async () => {
                for (const sequenced of ordered) await this.decrypt(sequenced, key);
            })();
        }
    }

    private serialAdmin(work: () => Promise<void>): Promise<void> {
        const next = this.adminChain.then(work, work);
        this.adminChain = next.catch(() => undefined);
        return next;
    }

    private requireAdmin(): void {
        if (!this.isAdmin) throw new Error("Only a channel admin (EncryptedChannelOptions.isAdmin) can do this.");
    }
}

function equalBytes(a: Uint8Array, b: Uint8Array): boolean {
    if (a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
    return true;
}
