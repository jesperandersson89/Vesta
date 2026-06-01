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
    /** The WebSocket URL to connect to (e.g. "ws://localhost:5150/ws"). */
    serverUrl: string;

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
     * Optional local store. When provided, events published while disconnected
     * are enqueued and flushed on the next WELCOME. Events received from the
     * server (EVENT, EVENTS_BATCH, ACK) are also cached locally.
     *
     * Server-side appends are idempotent on event id, so a publish that died
     * between SEND and ACK is safely retried on reconnect.
     */
    localStore?: ClientEventStore;
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

    private readonly serverUrl: string;
    private readonly clientId: string;
    private readonly createSocket: SocketFactory;
    private readonly autoReconnect: boolean;
    private readonly initialReconnectDelay: number;
    private readonly maxReconnectDelay: number;
    private readonly lastSequences: Record<string, number>;
    private readonly publicKey: string | undefined;
    private readonly localStore: ClientEventStore | undefined;
    private readonly pendingPublishes = new Map<string, VestaEvent>();

    constructor(options: VestaConnectionOptions) {
        this.serverUrl = options.serverUrl;
        this.clientId = options.clientId;
        this._channels = [...options.channels];
        this.createSocket = options.createSocket;
        this.autoReconnect = options.autoReconnect ?? true;
        this.initialReconnectDelay = options.initialReconnectDelay ?? 1000;
        this.maxReconnectDelay = options.maxReconnectDelay ?? 30000;
        this.lastSequences = { ...options.lastSequences };
        this.publicKey = options.publicKey;
        this.localStore = options.localStore;

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

    // ── Connection lifecycle ─────────────────────────────────────────────────

    /** Open the WebSocket connection. */
    connect(): void {
        if (this.disposed) throw new Error("Connection has been disposed");
        if (this.socket) return;

        this.socket = this.createSocket(this.serverUrl);

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
            this.emit("disconnected", ev.reason || "Connection closed");

            if (this.autoReconnect && !this.disposed) {
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
                this.emit("connected", msg);
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
