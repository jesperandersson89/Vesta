import type { ClientEventStore } from "./storage.js";
import type {
    AckMessage,
    ClientMessage,
    ErrorMessage,
    EventMessage,
    EventsBatchMessage,
    ServerMessage,
    VestaEvent,
    WelcomeMessage,
} from "./types.js";
import type { VestaIdentity } from "./identity.js";
import {
    buildAnnounce,
    buildLink,
    buildUnlink,
    DeviceGroupProjection,
    deviceGroupChannel,
    generateGroupId,
} from "./device-groups.js";
import type { DeviceGroup } from "./device-groups.js";
import { RELAY_MANIFEST_EVENT_TYPE } from "./relay.js";
import type { RelayDirectory, RelayManifest } from "./relay.js";

// ── WebSocket abstraction ────────────────────────────────────────────────────
// We support both the `ws` package (Node.js) and the browser WebSocket API.
// The connection expects a factory function that returns a WebSocket-like object.

export interface VestaSocket {
    readonly readyState: number;
    send(data: string): void;
    close(code?: number, reason?: string): void;
    addEventListener(event: "open", listener: () => void): void;
    addEventListener(
        event: "close",
        listener: (ev: { code: number; reason: string }) => void,
    ): void;
    addEventListener(event: "error", listener: (ev: unknown) => void): void;
    addEventListener(
        event: "message",
        listener: (ev: { data: unknown }) => void,
    ): void;
    removeEventListener(
        event: string,
        listener: (...args: unknown[]) => void,
    ): void;
}

export type SocketFactory = (url: string) => VestaSocket;

// ── Connection options ───────────────────────────────────────────────────────

export interface VestaConnectionOptions {
    /** The WebSocket URL to connect to (e.g. "ws://localhost:5150/ws"). Use this OR `relays`. */
    serverUrl?: string;

    /**
     * Ordered list of candidate relay URLs to try, in priority order. On failover the
     * connection walks this list. Supersedes `serverUrl` when provided. Typically the
     * resolved output of a {@link RelayDirectory}.
     */
    relays?: string[];

    /** Unique client identifier. */
    clientId: string;

    /** Channels to subscribe to on connect. */
    channels: string[];

    /**
     * Factory function that creates a WebSocket instance.
     * For Node.js: `(url) => new WebSocket(url)` (using the `ws` package).
     * For browsers: `(url) => new WebSocket(url)`.
     */
    createSocket: SocketFactory;

    /** Enable automatic reconnection on disconnect. Default: true. */
    autoReconnect?: boolean;

    /** Initial reconnect delay in ms. Default: 1000. */
    initialReconnectDelay?: number;

    /** Maximum reconnect delay in ms. Default: 30000. */
    maxReconnectDelay?: number;

    /**
     * Known last sequences per channel. Used for catch-up on connect.
     * Channels not listed here default to 0 (full catch-up).
     */
    lastSequences?: Record<string, number>;

    /** Optional Ed25519 public key (base64url). */
    publicKey?: string;

    /**
     * Optional identity. When provided, enables device-group convenience
     * methods (`createDeviceGroup`, `linkDevice`, etc.).
     * The `publicKey` option is automatically derived from it if not set explicitly.
     */
    identity?: VestaIdentity;

    /**
     * Optional local store. When provided, events published while disconnected
     * are enqueued and flushed on the next WELCOME. Events received from the
     * server (EVENT, EVENTS_BATCH, ACK) are also cached locally.
     *
     * Server-side appends are idempotent on event id, so a publish that died
     * between SEND and ACK is safely retried on reconnect.
     */
    localStore?: ClientEventStore;

    /**
     * Optional relay directory. When provided, the connection subscribes to the app's manifest
     * channel, verifies owner-signed manifests, and refreshes its relay candidate list when a
     * newer manifest is accepted (emitting `manifestApplied`).
     */
    relayDirectory?: RelayDirectory;
}

// ── Event emitter types ──────────────────────────────────────────────────────

export interface VestaConnectionEvents {
    event: (msg: EventMessage) => void;
    eventsBatch: (msg: EventsBatchMessage) => void;
    ack: (msg: AckMessage) => void;
    error: (msg: ErrorMessage) => void;
    connected: (msg: WelcomeMessage) => void;
    disconnected: (reason: string) => void;
    reconnecting: (attempt: number) => void;
    relaySwitched: (url: string) => void;
    manifestApplied: (manifest: RelayManifest) => void;
}

type EventKey = keyof VestaConnectionEvents;

// ── VestaConnection ──────────────────────────────────────────────────────────

export class VestaConnection {
    private socket: VestaSocket | null = null;
    private listeners = new Map<EventKey, Set<(...args: unknown[]) => void>>();
    private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
    private reconnectAttempt = 0;
    private disposed = false;
    private _isConnected = false;
    private _serverId: string | null = null;
    private _channels: string[];
    private relayCandidates: string[];
    private activeRelayIndex = 0;
    private notifiedRelayIndex = -1;
    private attemptReachedWelcome = false;
    private relayDirectory: RelayDirectory | undefined;

    private readonly clientId: string;
    private readonly createSocket: SocketFactory;
    private readonly autoReconnect: boolean;
    private readonly initialReconnectDelay: number;
    private readonly maxReconnectDelay: number;
    private readonly lastSequences: Record<string, number>;
    private readonly publicKey: string | undefined;
    private readonly identity: VestaIdentity | undefined;
    private readonly localStore: ClientEventStore | undefined;
    private readonly pendingPublishes = new Map<string, VestaEvent>();

    constructor(options: VestaConnectionOptions) {
        const candidates =
            options.relays && options.relays.length > 0
                ? [...options.relays]
                : options.serverUrl
                  ? [options.serverUrl]
                  : [];
        if (candidates.length === 0) {
            throw new Error(
                "VestaConnection requires either 'serverUrl' or a non-empty 'relays' list.",
            );
        }
        this.relayCandidates = candidates;
        this.clientId = options.clientId;
        this._channels = [...options.channels];
        this.createSocket = options.createSocket;
        this.autoReconnect = options.autoReconnect ?? true;
        this.initialReconnectDelay = options.initialReconnectDelay ?? 1000;
        this.maxReconnectDelay = options.maxReconnectDelay ?? 30000;
        this.lastSequences = { ...options.lastSequences };
        this.identity = options.identity;
        this.publicKey = options.publicKey ?? options.identity?.publicKeyB64;
        this.localStore = options.localStore;
        this.relayDirectory = options.relayDirectory;

        // Default all channels to sequence 0
        for (const ch of this._channels) {
            if (!(ch in this.lastSequences)) {
                this.lastSequences[ch] = 0;
            }
        }
    }

    /** Whether the connection is currently open and has received WELCOME. */
    get isConnected(): boolean {
        return this._isConnected;
    }

    /** The server ID from the WELCOME message. */
    get serverId(): string | null {
        return this._serverId;
    }

    /** Currently subscribed channels. */
    get channels(): readonly string[] {
        return this._channels;
    }

    /** The relay the connection is currently using (or last attempted). */
    get activeRelay(): string {
        return this.relayCandidates[this.activeRelayIndex]!;
    }

    /** The ordered relay candidate list tried on connect/failover. */
    get relays(): readonly string[] {
        return this.relayCandidates;
    }

    /**
     * Attach a relay directory so the connection discovers, verifies, and adopts owner-signed
     * manifests. Call BEFORE `connect()`. Accepted manifests refresh the candidate list and emit
     * `manifestApplied`; the new relays take effect on the next failover, or call `switchRelay()`.
     */
    attachRelayDirectory(directory: RelayDirectory): void {
        this.relayDirectory = directory;
    }

    /**
     * Replace the relay candidate list (e.g. after a newer manifest). Keeps the active relay if
     * still present, else resets to the top. Does not reconnect.
     */
    updateRelayCandidates(relays: string[]): void {
        if (relays.length === 0) {
            throw new Error("At least one relay URL is required.");
        }
        const active = this.activeRelay;
        this.relayCandidates = [...relays];
        const idx = this.relayCandidates.indexOf(active);
        this.activeRelayIndex = idx >= 0 ? idx : 0;
        if (idx < 0) this.notifiedRelayIndex = -1;
    }

    /**
     * Switch to a specific relay (must be in the candidate list) and reconnect immediately.
     */
    switchRelay(url: string): void {
        const idx = this.relayCandidates.indexOf(url);
        if (idx < 0) {
            throw new Error(`Relay '${url}' is not in the current candidate list.`);
        }
        this.activeRelayIndex = idx;
        this.cancelReconnect();
        if (this.socket) {
            this.socket.close(1000, "Switching relay");
            this.socket = null;
        }
        this._isConnected = false;
        this.connect();
    }

    // ── Connection lifecycle ─────────────────────────────────────────────────

    /** Open the WebSocket connection. */
    connect(): void {
        if (this.disposed) throw new Error("Connection has been disposed");
        if (this.socket) return;

        this.attemptReachedWelcome = false;
        this.socket = this.createSocket(this.activeRelay);

        this.socket.addEventListener("open", () => {
            this.sendRaw({
                type: "HELLO",
                clientId: this.clientId,
                channels: this._channels,
                lastSequences: this.lastSequences,
                publicKey: this.publicKey ?? null,
            });
        });

        this.socket.addEventListener("message", (ev) => {
            const data =
                typeof ev.data === "string" ? ev.data : ev.data?.toString?.();
            if (!data) return;

            let msg: ServerMessage;
            try {
                msg = JSON.parse(data as string) as ServerMessage;
            } catch {
                return;
            }

            this.handleMessage(msg);
        });

        this.socket.addEventListener("close", (ev) => {
            this._isConnected = false;
            this.socket = null;
            const reached = this.attemptReachedWelcome;
            this.emit("disconnected", ev.reason || "Connection closed");

            if (this.autoReconnect && !this.disposed) {
                // If this attempt never reached WELCOME, the relay is unreachable —
                // fail over to the next candidate before retrying.
                if (!reached && this.relayCandidates.length > 1) {
                    this.activeRelayIndex =
                        (this.activeRelayIndex + 1) % this.relayCandidates.length;
                }
                this.scheduleReconnect();
            }
        });

        this.socket.addEventListener("error", () => {
            // The close event will follow; we handle reconnect there.
        });
    }

    /** Gracefully disconnect. Does not trigger auto-reconnect. */
    disconnect(): void {
        this.cancelReconnect();
        if (this.socket) {
            this.socket.close(1000, "Client disconnecting");
            this.socket = null;
        }
        this._isConnected = false;
    }

    /** Dispose the connection permanently. Cannot be reused after this. */
    dispose(): void {
        this.disposed = true;
        this.disconnect();
        this.listeners.clear();
    }

    // ── Publishing ───────────────────────────────────────────────────────────

    /**
     * Publish a pre-built event to its channel.
     *
     * If connected, sends immediately and — when a `localStore` is configured —
     * caches the event on ACK.
     *
     * If disconnected and a `localStore` is configured, the event is enqueued
     * in the outbox and flushed on the next WELCOME.
     *
     * If disconnected and no `localStore` is configured, throws.
     */
    publish(event: VestaEvent): void {
        if (this._isConnected && this.socket?.readyState === 1) {
            if (this.localStore) {
                this.pendingPublishes.set(event.id, event);
            }
            this.sendRaw({
                type: "PUBLISH",
                channelId: event.channelId,
                event,
            });
            return;
        }

        if (this.localStore) {
            void this.localStore.enqueueOutbox(event);
            return;
        }

        throw new Error(
            "Not connected and no localStore configured for offline publishing",
        );
    }

    // ── Subscriptions ────────────────────────────────────────────────────────

    /** Subscribe to a new channel (after initial connect). */
    subscribe(channelId: string, fromSequence?: number): void {
        if (!this._channels.includes(channelId)) {
            this._channels.push(channelId);
        }
        this.sendRaw({
            type: "SUBSCRIBE",
            channelId,
            fromSequence: fromSequence ?? null,
        });
    }

    /** Unsubscribe from a channel. */
    unsubscribe(channelId: string): void {
        this._channels = this._channels.filter((ch) => ch !== channelId);
        this.sendRaw({
            type: "UNSUBSCRIBE",
            channelId,
        });
    }

    /** Fetch historical events from a channel. */
    fetch(
        channelId: string,
        fromSequence: number,
        options?: { toSequence?: number; limit?: number },
    ): void {
        this.sendRaw({
            type: "FETCH",
            channelId,
            fromSequence,
            toSequence: options?.toSequence ?? null,
            limit: options?.limit ?? null,
        });
    }

    // ── Channel management (ACL) ─────────────────────────────────────────────

    /**
     * Create a channel with explicit visibility and initial members.
     * For private channels, only the caller (admin) and listed members can publish/subscribe.
     * The caller is auto-subscribed.
     */
    createChannel(
        channelId: string,
        options?: { visibility?: "public" | "private"; members?: string[] },
    ): void {
        this.sendRaw({
            type: "CREATE_CHANNEL",
            channelId,
            visibility: options?.visibility ?? "private",
            initialMembers: options?.members ?? [],
        });
    }

    /** Grant a client access to a private channel. Caller must be the channel admin. */
    grantAccess(
        channelId: string,
        clientId: string,
        role: "member" | "admin" = "member",
    ): void {
        this.sendRaw({
            type: "GRANT_ACCESS",
            channelId,
            clientId,
            role,
        });
    }

    /**
     * Register an app namespace. The first slug segment of every channel ID belongs to an app.
     * When the server is configured with `Protocol:RequireAppRegistration=true`, the app must
     * be registered before publishing or subscribing on any channel in its namespace.
     * The connecting client becomes the owner.
     */
    registerApp(appId: string): void {
        this.sendRaw({
            type: "REGISTER_APP",
            appId,
        });
    }

    /**
     * Soft-delete a channel. Requires the connection's public key to be in the
     * server's `Admin:BootstrapPublicKeys` allow-list. Existing events are
     * retained for a future hard-delete sweep; further PUBLISH / SUBSCRIBE /
     * FETCH / CREATE_CHANNEL for that channel are rejected with `CHANNEL_DELETED`.
     * Idempotent: deleting an already-deleted channel succeeds.
     */
    deleteChannel(channelId: string): void {
        this.sendRaw({
            type: "DELETE_CHANNEL",
            channelId,
        });
    }

    // ── Device group convenience methods ─────────────────────────────────────

    /**
     * Create a new device group with this connection's identity as the founder.
     * Publishes a `vesta.identity.announce` event and returns the generated `groupId`.
     * Requires `identity` to be set in connection options.
     */
    createDeviceGroup(deviceName?: string): string {
        const identity = this.requireIdentity("createDeviceGroup");
        const groupId = generateGroupId();
        this.publish(buildAnnounce(identity, groupId, deviceName));
        return groupId;
    }

    /**
     * Vouch for another device as a member of the given group.
     * Publishes a `vesta.identity.link` event signed by this connection's identity.
     * Requires `identity` to be set in connection options.
     */
    linkDevice(groupId: string, targetPublicKey: Uint8Array, reason?: string): void {
        const identity = this.requireIdentity("linkDevice");
        this.publish(buildLink(identity, groupId, targetPublicKey, reason));
    }

    /**
     * Announce this connection's identity as joining an existing group.
     * Publishes a `vesta.identity.announce` event.
     * Requires `identity` to be set in connection options.
     */
    joinDeviceGroup(groupId: string, deviceName?: string): void {
        const identity = this.requireIdentity("joinDeviceGroup");
        this.publish(buildAnnounce(identity, groupId, deviceName));
    }

    /**
     * Remove a device from the group. Publishes a `vesta.identity.unlink` event.
     * Requires `identity` to be set in connection options.
     */
    unlinkDevice(groupId: string, targetPublicKey: Uint8Array, reason?: string): void {
        const identity = this.requireIdentity("unlinkDevice");
        this.publish(buildUnlink(identity, groupId, targetPublicKey, reason));
    }

    /**
     * Subscribe to the group's identity channel, replay the full history into a
     * `DeviceGroupProjection`, and resolve with the current membership.
     *
     * This is a one-shot convenience method for occasional inspection.
     * For continuous tracking, subscribe to the channel directly and feed
     * events into your own `DeviceGroupProjection`.
     */
    getDeviceGroupMembers(
        groupId: string,
        timeoutMs = 5000,
    ): Promise<DeviceGroup> {
        const channelId = deviceGroupChannel(groupId);
        const projection = new DeviceGroupProjection(groupId);

        return new Promise((resolve) => {
            let settled = false;

            const onBatch = (msg: EventsBatchMessage) => {
                if (msg.channelId !== channelId) return;
                projection.applyBatch(msg.events);
                if (!settled) {
                    settled = true;
                    cleanup();
                    resolve(projection.state);
                }
            };

            const onEvt = (msg: EventMessage) => {
                if (msg.channelId !== channelId) return;
                projection.apply({ event: msg.event, sequence: msg.sequence, receivedAt: msg.receivedAt });
            };

            const timer = setTimeout(() => {
                if (!settled) {
                    settled = true;
                    cleanup();
                    resolve(projection.state);
                }
            }, timeoutMs);

            const cleanup = () => {
                clearTimeout(timer);
                this.off("eventsBatch", onBatch);
                this.off("event", onEvt);
            };

            this.on("eventsBatch", onBatch);
            this.on("event", onEvt);
            this.subscribe(channelId, 0);
        });
    }

    private requireIdentity(methodName: string): VestaIdentity {
        if (!this.identity) {
            throw new Error(
                `${methodName}() requires the VestaConnection to be constructed with an 'identity' option.`,
            );
        }
        return this.identity;
    }

    // ── Sequence tracking ────────────────────────────────────────────────────

    /** Update the last known sequence for a channel (used on reconnect for catch-up). */
    updateSequence(channelId: string, sequence: number): void {
        this.lastSequences[channelId] = sequence;
    }

    // ── Event emitter ────────────────────────────────────────────────────────

    on<K extends EventKey>(event: K, listener: VestaConnectionEvents[K]): this {
        if (!this.listeners.has(event)) {
            this.listeners.set(event, new Set());
        }
        this.listeners
            .get(event)!
            .add(listener as (...args: unknown[]) => void);
        return this;
    }

    off<K extends EventKey>(
        event: K,
        listener: VestaConnectionEvents[K],
    ): this {
        this.listeners
            .get(event)
            ?.delete(listener as (...args: unknown[]) => void);
        return this;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private handleMessage(msg: ServerMessage): void {
        switch (msg.type) {
            case "WELCOME":
                this._isConnected = true;
                this._serverId = msg.serverId;
                this._channels = [...msg.channels];
                this.reconnectAttempt = 0;
                this.attemptReachedWelcome = true;
                if (this.activeRelayIndex !== this.notifiedRelayIndex) {
                    this.notifiedRelayIndex = this.activeRelayIndex;
                    this.emit("relaySwitched", this.activeRelay);
                }
                this.emit("connected", msg);
                if (
                    this.relayDirectory &&
                    !this._channels.includes(this.relayDirectory.manifestChannel)
                ) {
                    this.subscribe(this.relayDirectory.manifestChannel, 0);
                }
                if (this.localStore) {
                    void this.flushOutbox();
                }
                break;

            case "EVENT":
                this.updateSequence(msg.channelId, msg.sequence);
                if (this.localStore) {
                    void this.localStore.storeEvent({
                        event: msg.event,
                        sequence: msg.sequence,
                        receivedAt: msg.receivedAt,
                    });
                }
                this.maybeApplyManifestEvent(msg.channelId, msg.event);
                this.emit("event", msg);
                break;

            case "EVENTS_BATCH":
                if (msg.events.length > 0) {
                    const lastSeq = msg.events[msg.events.length - 1].sequence;
                    this.updateSequence(msg.channelId, lastSeq);
                }
                if (this.localStore && msg.events.length > 0) {
                    void this.localStore.storeEvents(msg.events);
                }
                if (this.relayDirectory && msg.channelId === this.relayDirectory.manifestChannel) {
                    for (const se of msg.events) {
                        this.maybeApplyManifestEvent(msg.channelId, se.event);
                    }
                }
                this.emit("eventsBatch", msg);
                break;

            case "ACK":
                this.updateSequence(msg.channelId, msg.sequence);
                if (this.localStore) {
                    void this.cacheEventOnAck(msg);
                }
                this.emit("ack", msg);
                break;

            case "ERROR":
                this.emit("error", msg);
                break;
        }
    }

    private maybeApplyManifestEvent(channelId: string, event: VestaEvent): void {
        const directory = this.relayDirectory;
        if (!directory || channelId !== directory.manifestChannel) return;
        if (event.eventType !== RELAY_MANIFEST_EVENT_TYPE) return;

        const manifest = event.payload as RelayManifest;
        if (!directory.tryApplyManifest(manifest)) return;

        this.updateRelayCandidates(directory.resolveCandidates());
        this.emit("manifestApplied", manifest);
    }

    private async cacheEventOnAck(ack: AckMessage): Promise<void> {
        if (!this.localStore) return;
        const evt = this.pendingPublishes.get(ack.eventId);
        if (evt) {
            this.pendingPublishes.delete(ack.eventId);
            await this.localStore.storeEvent({
                event: evt,
                sequence: ack.sequence,
                receivedAt: new Date().toISOString(),
            });
        }
        await this.localStore.markOutboxConfirmed(ack.eventId);
    }

    private async flushOutbox(): Promise<void> {
        if (!this.localStore) return;
        const pending = await this.localStore.getPendingOutbox();
        for (const entry of pending) {
            this.pendingPublishes.set(entry.event.id, entry.event);
            try {
                this.sendRaw({
                    type: "PUBLISH",
                    channelId: entry.event.channelId,
                    event: entry.event,
                });
                await this.localStore.markOutboxSent(entry.event.id);
            } catch {
                // Socket dropped mid-flush. The remaining entries stay in the
                // outbox and will be picked up on the next WELCOME.
                this.pendingPublishes.delete(entry.event.id);
                return;
            }
        }
    }

    private sendRaw(msg: ClientMessage): void {
        if (!this.socket || this.socket.readyState !== 1 /* OPEN */) {
            throw new Error("Not connected");
        }
        this.socket.send(JSON.stringify(msg));
    }

    private emit<K extends EventKey>(
        event: K,
        ...args: Parameters<VestaConnectionEvents[K]>
    ): void {
        const set = this.listeners.get(event);
        if (!set) return;
        for (const fn of set) {
            try {
                fn(...args);
            } catch {
                // Listener errors should not break the connection
            }
        }
    }

    private scheduleReconnect(): void {
        this.cancelReconnect();
        this.reconnectAttempt++;
        const delay = Math.min(
            this.initialReconnectDelay * 2 ** (this.reconnectAttempt - 1),
            this.maxReconnectDelay,
        );
        this.emit("reconnecting", this.reconnectAttempt);
        this.reconnectTimer = setTimeout(() => this.connect(), delay);
    }

    private cancelReconnect(): void {
        if (this.reconnectTimer) {
            clearTimeout(this.reconnectTimer);
            this.reconnectTimer = null;
        }
    }
}
