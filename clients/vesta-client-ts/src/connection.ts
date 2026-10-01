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
import { classifyErrorCode } from "./limits.js";
import type { VestaLimitNotice } from "./limits.js";
import { FederationClient } from "./federation.js";
import { RELAY_MANIFEST_EVENT_TYPE } from "./relay.js";
import type {
    RelayAttempt,
    RelayDirectory,
    RelayManifest,
    RelayOverride,
    RelaysExhaustedInfo,
    VestaAppConfig,
} from "./relay.js";
import { signEvent } from "./signing.js";
import { RelayRecoverySession } from "./relay-recovery.js";
import type { RelayAdoptResult, RelayRecoveryHost } from "./relay-recovery.js";
import { mountRelayPickerOverlay } from "./relay-picker.js";

// ── Built-in relay picker wiring ─────────────────────────────────────────────
// The picker is part of the connection: when every relay is exhausted the connection prompts the
// user itself. Node registers a loopback web page (node.ts); browsers get the overlay element.

/** A UI that can prompt the user to pick another relay. */
export interface RelayPickerHandle {
    show(info: RelaysExhaustedInfo): Promise<boolean>;
    dispose(): void;
}

type RelayPickerFactory = (host: RelayRecoveryHost, config: VestaAppConfig) => RelayPickerHandle;

let relayPickerFactory: RelayPickerFactory | null = null;
let relayPickerEnabled = true;

/** Register the platform picker used when relays are exhausted (called by `node.ts` on import). */
export function setRelayPickerFactory(factory: RelayPickerFactory | null): void {
    relayPickerFactory = factory;
}

/** Enable or disable the built-in relay picker globally (on by default). Mainly for tests. */
export function setRelayPickerEnabled(enabled: boolean): void {
    relayPickerEnabled = enabled;
}

/** Error codes that mean "the relay does not accept this app namespace". */
export const NAMESPACE_ERROR_CODES: ReadonlySet<string> = new Set([
    "APP_NOT_ALLOWED",
    "UNKNOWN_APP",
    "INVALID_APP",
    "APPS_NOT_SUPPORTED",
    "INVALID_CHANNEL",
    "ACCESS_DENIED",
]);

/** How long a freshly adopted relay must stay free of namespace errors before it is committed. */
export const RELAY_ADOPT_PROBATION_MS = 1500;

const NIL_EVENT_ID = "00000000-0000-0000-0000-000000000000";

/** Rewrite the first path segment of a channel id from `from` to `to` (protocol channels are left alone). */
export function remapChannel(
    channelId: string,
    from: string | null,
    to: string | null,
): string {
    if (!from || !to || from === to) return channelId;
    if (channelId.startsWith("vesta/")) return channelId;
    if (channelId === from) return to;
    return channelId.startsWith(from + "/") ? to + channelId.slice(from.length) : channelId;
}

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

    /**
     * How many full, failed passes over the relay candidate list count as "exhausted" under
     * `autoReconnect`, firing `relaysExhausted`. Default: 3.
     */
    relayExhaustionPasses?: number;

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
    /** A semantic "your app is being limited" signal — see {@link classifyErrorCode}. */
    limited: (notice: VestaLimitNotice) => void;
    connected: (msg: WelcomeMessage) => void;
    /** Fires after WELCOME when this connection had previously reached WELCOME at least once. */
    reconnected: (msg: WelcomeMessage) => void;
    disconnected: (reason: string) => void;
    reconnecting: (attempt: number) => void;
    relaySwitched: (url: string) => void;
    manifestApplied: (manifest: RelayManifest) => void;
    /** One connection attempt against one relay candidate failed before WELCOME. */
    relayAttemptFailed: (attempt: RelayAttempt) => void;
    /**
     * Fires once per outage when every candidate has failed `relayExhaustionPasses` full passes.
     * Auto-reconnect keeps retrying in the background; surface a recovery prompt
     * (see `RelayRecoverySession`).
     */
    relaysExhausted: (info: RelaysExhaustedInfo) => void;
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
    private hasReachedWelcomeOnce = false;
    private _channels: string[];
    private relayCandidates: string[];
    private activeRelayIndex = 0;
    private notifiedRelayIndex = -1;
    private attemptReachedWelcome = false;
    private consecutiveFailures = 0;
    private exhaustionRaised = false;
    private readonly lastAttempts = new Map<string, string>();
    private readonly retiredSockets = new WeakSet<VestaSocket>();
    private federation: FederationClient | undefined;
    private remoteAppId: string | null = null;
    private registerOnConnect = false;
    private namespaceConfirmed = false;
    private probationFail: ((reason: string) => void) | null = null;
    private registration: { resolve(): void; reject(error: Error): void } | null = null;
    private picker: RelayPickerHandle | null = null;
    private overlayDispose: (() => void) | null = null;
    private pickerShown = false;

    /** The attached relay directory, if any. */
    relayDirectory: RelayDirectory | undefined;

    /** Whether the connection reconnects on its own after a drop. */
    readonly autoReconnect: boolean;

    private readonly clientId: string;
    private readonly createSocket: SocketFactory;
    private readonly relayExhaustionPasses: number;
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
        this.relayExhaustionPasses = Math.max(1, options.relayExhaustionPasses ?? 3);
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
        const attemptedRelay: string = this.activeRelay;
        this.resolveActiveNamespace(attemptedRelay);
        const socket: VestaSocket = this.createSocket(attemptedRelay);
        this.socket = socket;
        let attemptError: string | null = null;

        this.socket.addEventListener("open", () => {
            if (this.retiredSockets.has(socket)) return;
            // Registering first: greet with no channels so nothing is requested before the namespace exists.
            const registerFirst: boolean = this.registerOnConnect;
            this.sendRaw({
                type: "HELLO",
                clientId: this.clientId,
                channels: registerFirst ? [] : this._channels,
                lastSequences: registerFirst ? {} : this.lastSequences,
                publicKey: this.publicKey ?? null,
            });
        });

        this.socket.addEventListener("message", (ev) => {
            if (this.retiredSockets.has(socket)) return;
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
            if (this.retiredSockets.has(socket)) return;
            this._isConnected = false;
            this.socket = null;
            const reached = this.attemptReachedWelcome;
            this.emit("disconnected", ev.reason || "Connection closed");

            if (!reached && !this.disposed) {
                this.recordAttemptFailure(
                    attemptedRelay,
                    attemptError ?? (ev.reason || "Connection closed before the relay accepted it"),
                );
            }

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

        this.socket.addEventListener("error", (ev) => {
            // The close event will follow and handles reconnect; just remember why.
            const message: unknown = (ev as { message?: unknown } | null)?.message;
            if (typeof message === "string" && message) attemptError = message;
        });
    }

    /**
     * Close the current socket (if any) and connect again right now, walking the candidate list.
     * Resolves true once WELCOME arrives, false once a full pass (or one attempt when
     * `autoReconnect` is off) has failed. Auto-reconnect keeps retrying in the background.
     */
    reconnect(): Promise<boolean> {
        if (this.disposed) return Promise.resolve(false);
        if (this._isConnected) return Promise.resolve(true);
        const outcome = this.waitForOutcome(this.autoReconnect ? this.relayCandidates.length : 1);
        this.restart();
        return outcome;
    }

    /**
     * Move to the relay (and app namespace) the user chose. The override is applied provisionally:
     * it is only persisted once the relay is reachable and stays free of namespace errors for a
     * short probation; otherwise the previous state is restored and the reason is returned.
     * Requires an attached {@link RelayDirectory}.
     */
    async adoptRelay(override: RelayOverride): Promise<RelayAdoptResult> {
        const directory = this.relayDirectory;
        if (!directory) {
            throw new Error("No relayDirectory attached — cannot set a relay override.");
        }

        const probation: { reason: string | null } = { reason: null };
        let signalFailure: () => void = () => {};
        const failure = new Promise<void>((resolve) => {
            signalFailure = resolve;
        });
        this.probationFail = (reason: string): void => {
            if (probation.reason !== null) return;
            probation.reason = reason;
            signalFailure();
        };
        this.namespaceConfirmed = false;

        try {
            directory.setUserOverride(override, false);
            this.updateRelayCandidates(directory.resolveCandidates());
            const index: number = this.relayCandidates.indexOf(override.relay);
            this.activeRelayIndex = index >= 0 ? index : 0;
            const outcome: Promise<boolean> = this.waitForOutcome(1);
            this.restart();
            const connected: boolean = await outcome;
            const onTarget: boolean = connected && this.activeRelay === override.relay;

            let reason: string | null;
            if (onTarget) {
                if (probation.reason === null && !this.namespaceConfirmed) {
                    let timer: ReturnType<typeof setTimeout> | undefined;
                    const elapsed = new Promise<void>((resolve) => {
                        timer = setTimeout(resolve, RELAY_ADOPT_PROBATION_MS);
                    });
                    await Promise.race([failure, elapsed]);
                    if (timer) clearTimeout(timer);
                }
                if (probation.reason === null) {
                    directory.commitPendingOverride();
                    return { connected: true, failureReason: null };
                }
                reason = probation.reason;
            } else {
                reason = this.lastAttempts.get(override.relay) ?? "Could not connect";
            }

            directory.discardPendingOverride();
            this.updateRelayCandidates(directory.resolveCandidates());
            if (onTarget && !this.disposed) {
                this.activeRelayIndex = 0;
                const again: Promise<boolean> = this.waitForOutcome(
                    this.autoReconnect ? this.relayCandidates.length : 1,
                );
                this.restart();
                await again;
            }
            return { connected: false, failureReason: reason };
        } finally {
            this.probationFail = null;
        }
    }

    /** Clear the user relay override and reconnect using the freshly resolved candidates. */
    clearRelayOverride(): Promise<boolean> {
        const directory = this.relayDirectory;
        if (!directory) {
            return Promise.reject(new Error("No relayDirectory attached — cannot clear a relay override."));
        }
        directory.clearUserOverride();
        this.updateRelayCandidates(directory.resolveCandidates());
        return this.reconnect();
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
        this.picker?.dispose();
        this.picker = null;
        this.overlayDispose?.();
        this.overlayDispose = null;
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

    private handleMessage(raw: ServerMessage): void {
        const msg: ServerMessage = this.mapInbound(raw);
        switch (msg.type) {
            case "WELCOME": {
                const wasReconnect = this.hasReachedWelcomeOnce;
                const registerFirst: boolean = this.registerOnConnect;
                const requested: string[] = this._channels;
                this.namespaceConfirmed =
                    !registerFirst &&
                    msg.channels.length > 0 &&
                    msg.channels.some((c: string) => requested.includes(c));
                this._isConnected = true;
                this._serverId = msg.serverId;
                if (!registerFirst) this._channels = [...msg.channels];
                this.pickerShown = false;
                this.reconnectAttempt = 0;
                this.attemptReachedWelcome = true;
                this.hasReachedWelcomeOnce = true;
                this.consecutiveFailures = 0;
                this.exhaustionRaised = false;
                this.lastAttempts.clear();
                if (this.activeRelayIndex !== this.notifiedRelayIndex) {
                    this.notifiedRelayIndex = this.activeRelayIndex;
                    this.emit("relaySwitched", this.activeRelay);
                }
                this.emit("connected", msg);
                if (wasReconnect) {
                    this.emit("reconnected", msg);
                }
                if (registerFirst) {
                    void this.registerThenSubscribe();
                    void this.refreshPeerCache(this.activeRelay);
                    break;
                }
                if (
                    this.relayDirectory &&
                    !this._channels.includes(this.relayDirectory.manifestChannel)
                ) {
                    this.subscribe(this.relayDirectory.manifestChannel, 0);
                }
                if (this.localStore) {
                    void this.flushOutbox();
                }
                void this.refreshPeerCache(this.activeRelay);
                break;
            }

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
                if (msg.eventId === NIL_EVENT_ID) {
                    // Registration acknowledgement, not an event.
                    this.registration?.resolve();
                    break;
                }
                this.updateSequence(msg.channelId, msg.sequence);
                if (this.localStore) {
                    void this.cacheEventOnAck(msg);
                }
                this.emit("ack", msg);
                break;

            case "ERROR":
                if (this.registration) {
                    if (msg.code === "DUPLICATE_APP") this.registration.resolve();
                    else this.registration.reject(new Error(`${msg.code}: ${msg.message}`));
                    break;
                }
                if (this.probationFail && NAMESPACE_ERROR_CODES.has(msg.code)) {
                    this.probationFail(`${msg.code}: ${msg.message}`);
                }
                this.emit("error", msg);
                this.handlePossibleLimit(msg);
                break;
        }
    }

    /** Decide which app namespace (if any) applies to `candidate`, from the active override. */
    private resolveActiveNamespace(candidate: string): void {
        const localAppId: string | undefined = this.relayDirectory?.config.appId;
        const active: RelayOverride | null | undefined = this.relayDirectory?.activeOverride;
        const applies: boolean = !!active && active.relay === candidate;
        const ns: string | null | undefined = applies ? active?.appId : null;
        this.remoteAppId = !ns || ns === localAppId ? null : ns;
        this.registerOnConnect = applies && active?.registerApp === true;
    }

    /** After a register-first handshake: claim the namespace, then subscribe and flush as usual. */
    private async registerThenSubscribe(): Promise<void> {
        const appId: string | undefined = this.remoteAppId ?? this.relayDirectory?.config.appId;
        try {
            if (!appId) throw new Error("No app id to register");
            await new Promise<void>((resolve, reject) => {
                this.registration = { resolve, reject };
                this.sendRaw({ type: "REGISTER_APP", appId });
            });
        } catch (error) {
            const reason: string = error instanceof Error ? error.message : String(error);
            this.registration = null;
            if (this.probationFail) this.probationFail(reason);
            else this.emit("error", { type: "ERROR", code: "REGISTER_APP_FAILED", message: reason });
            return;
        }
        this.registration = null;
        try {
            for (const channelId of this._channels) {
                const last: number = this.lastSequences[channelId] ?? 0;
                this.sendRaw({ type: "SUBSCRIBE", channelId, fromSequence: last + 1 });
            }
            if (
                this.relayDirectory &&
                !this._channels.includes(this.relayDirectory.manifestChannel)
            ) {
                this.sendRaw({
                    type: "SUBSCRIBE",
                    channelId: this.relayDirectory.manifestChannel,
                    fromSequence: 0,
                });
            }
            this.namespaceConfirmed = true;
            if (this.localStore) await this.flushOutbox();
        } catch {
            // Socket dropped mid-setup; the next connect repeats the handshake.
        }
    }

    private mapOutbound(msg: ClientMessage): ClientMessage {
        const remote: string | null = this.remoteAppId;
        const local: string | undefined = this.relayDirectory?.config.appId;
        if (!remote || !local) return msg;
        const map = (channelId: string): string => remapChannel(channelId, local, remote);
        switch (msg.type) {
            case "HELLO": {
                const lastSequences: Record<string, number> = {};
                for (const [channelId, sequence] of Object.entries(msg.lastSequences)) {
                    lastSequences[map(channelId)] = sequence;
                }
                return { ...msg, channels: msg.channels.map(map), lastSequences };
            }
            case "PUBLISH": {
                const channelId: string = map(msg.channelId);
                if (channelId === msg.event.channelId) return { ...msg, channelId };
                const event: VestaEvent = { ...msg.event, channelId };
                delete event.signature;
                if (this.identity) signEvent(event, this.identity.privateKey);
                return { ...msg, channelId, event };
            }
            case "SUBSCRIBE":
            case "UNSUBSCRIBE":
            case "FETCH":
            case "CREATE_CHANNEL":
            case "GRANT_ACCESS":
            case "DELETE_CHANNEL":
                return { ...msg, channelId: map(msg.channelId) };
            default:
                return msg;
        }
    }

    private mapInbound(msg: ServerMessage): ServerMessage {
        const remote: string | null = this.remoteAppId;
        const local: string | undefined = this.relayDirectory?.config.appId;
        if (!remote || !local) return msg;
        const map = (channelId: string): string => remapChannel(channelId, remote, local);
        switch (msg.type) {
            case "WELCOME":
                return { ...msg, channels: msg.channels.map(map) };
            case "EVENT":
                return {
                    ...msg,
                    channelId: map(msg.channelId),
                    event: { ...msg.event, channelId: map(msg.event.channelId) },
                };
            case "EVENTS_BATCH":
                return {
                    ...msg,
                    channelId: map(msg.channelId),
                    events: msg.events.map((se) => ({
                        ...se,
                        event: { ...se.event, channelId: map(se.event.channelId) },
                    })),
                };
            case "ACK":
                return { ...msg, channelId: map(msg.channelId) };
            case "ERROR":
                return msg.channelId ? { ...msg, channelId: map(msg.channelId) } : msg;
            default:
                return msg;
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

    private handlePossibleLimit(msg: ErrorMessage): void {
        const classification = classifyErrorCode(msg.code);
        if (classification.isLimit) {
            this.emit("limited", {
                code: msg.code,
                message: msg.message,
                channelId: msg.channelId,
                eventId: msg.eventId,
                isTransient: classification.isTransient,
            });
        }
        if (classification.isEventFatal && msg.eventId && this.localStore) {
            this.pendingPublishes.delete(msg.eventId);
            void this.localStore.markOutboxRejected(msg.eventId, msg.code);
        }
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
        this.socket.send(JSON.stringify(this.mapOutbound(msg)));
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

    private restart(): void {
        this.cancelReconnect();
        if (this.socket) {
            this.retiredSockets.add(this.socket);
            this.socket.close(1000, "Reconnecting");
            this.socket = null;
        }
        this._isConnected = false;
        this.connect();
    }

    private waitForOutcome(maxFailures: number): Promise<boolean> {
        return new Promise<boolean>((resolve) => {
            let failures = 0;
            const finish = (ok: boolean): void => {
                this.off("connected", onConnected);
                this.off("relayAttemptFailed", onFailed);
                resolve(ok);
            };
            const onConnected = (): void => finish(true);
            const onFailed = (): void => {
                failures++;
                if (failures >= maxFailures) finish(false);
            };
            this.on("connected", onConnected);
            this.on("relayAttemptFailed", onFailed);
        });
    }

    private recordAttemptFailure(relay: string, reason: string): void {
        this.lastAttempts.set(relay, reason);
        this.consecutiveFailures++;
        this.emit("relayAttemptFailed", { relay, reason });

        const threshold: number = this.relayExhaustionPasses * this.relayCandidates.length;
        if (this.autoReconnect && !this.exhaustionRaised && this.consecutiveFailures >= threshold) {
            this.exhaustionRaised = true;
            const attempts: RelayAttempt[] = this.relayCandidates
                .filter((url) => this.lastAttempts.has(url))
                .map((url) => ({ relay: url, reason: this.lastAttempts.get(url)! }));
            const info: RelaysExhaustedInfo = {
                attempts,
                passes: Math.floor(this.consecutiveFailures / this.relayCandidates.length),
            };
            this.emit("relaysExhausted", info);
            void this.maybeShowPicker(info);
        }
    }

    /** Prompt the user (once per outage) to pick another relay, using the platform's built-in picker. */
    private async maybeShowPicker(info: RelaysExhaustedInfo): Promise<void> {
        if (!relayPickerEnabled || this.disposed || this.pickerShown) return;
        const directory: RelayDirectory | undefined = this.relayDirectory;
        if (!directory) return;
        this.pickerShown = true;
        try {
            if (relayPickerFactory) {
                this.picker ??= relayPickerFactory(this, directory.config);
                await this.picker.show(info);
                return;
            }
            if (!this.overlayDispose) {
                const session = new RelayRecoverySession(this);
                const dispose: (() => void) | null = mountRelayPickerOverlay(session);
                if (!dispose) {
                    session.dispose();
                    return;
                }
                this.overlayDispose = (): void => {
                    dispose();
                    session.dispose();
                };
                session.reportExhausted(info);
            }
        } catch {
            // A picker failure must never disturb reconnecting.
        }
    }

    // Best-effort: remember verified federation peers so recovery has hints if every known relay dies.
    private async refreshPeerCache(relay: string): Promise<void> {
        const directory = this.relayDirectory;
        const cache = directory?.peerCache;
        if (!directory || !cache) return;
        const baseUrl: string | null = FederationClient.toFederationBaseUrl(relay);
        if (!baseUrl) return;
        try {
            this.federation ??= new FederationClient(directory.config);
            const peers = await this.federation.listAllRelays(baseUrl);
            if (peers.length > 0) cache.save(peers);
        } catch {
            // Cache refresh must never disturb a healthy connection.
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
