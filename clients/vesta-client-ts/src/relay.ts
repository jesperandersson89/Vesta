/**
 * Relay independence: owner-signed relay manifests, candidate resolution, and the
 * per-user override — the TypeScript mirror of the C# `VestaCore.Relay` /
 * `VestaClient.Relay` types. Manifest signing input MUST match the C#
 * `ManifestSigner.BuildSigningInput` byte-for-byte (RFC 8785 JCS + Ed25519).
 */

import * as ed from "@noble/ed25519";

import {
    base64UrlToBytes,
    bytesToBase64Url,
    canonicalize,
    normalizeTimestampForSigning,
} from "./signing.js";
import type { VestaIdentity } from "./identity.js";
import type { DiscoveredRelay } from "./federation.js";

// ─── Types ───────────────────────────────────────────────────────────────────

/** The reserved event type used for relay manifest events. */
export const RELAY_MANIFEST_EVENT_TYPE = "vesta.relay-manifest";

/** The channel an app's relay manifest is published to: `{appId}/vesta/relays`. */
export function manifestChannelFor(appId: string): string {
    return `${appId}/vesta/relays`;
}

export interface RelayEndpoint {
    /** Relay WebSocket URL (e.g. `wss://relay.example/ws`). */
    url: string;
    /** Lower numbers are preferred. */
    priority: number;
    /** Optional human-readable label. */
    label?: string | null;
}

export interface EscapeFallback {
    /** Fallback relay WebSocket URL. */
    url: string;
    /** ISO 8601 instant from which clients may treat this fallback as usable. */
    validFrom: string;
}

export interface RelayManifest {
    appId: string;
    /** Monotonic version counter — newest valid wins, lower is rejected (anti-rollback). */
    version: number;
    /** Informational issue time (ISO 8601). */
    issuedAt: string;
    relays: RelayEndpoint[];
    escapeFallbacks?: EscapeFallback[];
    /** base64url Ed25519 public key of the owner — the trust anchor. */
    ownerPublicKey: string;
    /** base64url Ed25519 signature. Absent until signed. */
    signature?: string;
}

/** App-level config compiled into the app for relay-independent coordination. */
export interface VestaAppConfig {
    appId: string;
    /** The owner's Ed25519 public key (base64url) — the manifest trust anchor. */
    ownerPublicKey: string;
    /** Compiled-in default relays, in preference order. */
    defaultRelays: string[];
    /** Extra relays to ask about federation peers when recovering from total relay loss. */
    discoverySeeds?: string[];
}

// ─── Manifest signing / verification ─────────────────────────────────────────

/** Canonical signing-input bytes for a manifest (matches C# ManifestSigner.BuildSigningInput). */
export function buildManifestSigningInput(manifest: RelayManifest): Uint8Array {
    const fields: Record<string, unknown> = {
        appId: manifest.appId,
        escapeFallbacks: (manifest.escapeFallbacks ?? []).map((f) => ({
            url: f.url,
            validFrom: normalizeTimestampForSigning(f.validFrom),
        })),
        issuedAt: normalizeTimestampForSigning(manifest.issuedAt),
        ownerPublicKey: manifest.ownerPublicKey,
        relays: manifest.relays.map((r) => ({
            label: r.label ?? null,
            priority: r.priority,
            url: r.url,
        })),
        version: manifest.version,
    };
    return new TextEncoder().encode(canonicalize(fields));
}

/**
 * Sign a manifest with the owner identity. Returns a new manifest with `ownerPublicKey`
 * set to the signer and `signature` populated.
 */
export function signManifest(
    manifest: RelayManifest,
    identity: VestaIdentity,
): RelayManifest {
    const ownerPublicKey = identity.publicKeyB64;
    if (manifest.ownerPublicKey && manifest.ownerPublicKey !== ownerPublicKey) {
        throw new Error(
            `Manifest ownerPublicKey '${manifest.ownerPublicKey}' does not match signing identity '${ownerPublicKey}'.`,
        );
    }

    const withOwner: RelayManifest = {
        ...manifest,
        ownerPublicKey,
        signature: undefined,
    };
    const sig = ed.sign(buildManifestSigningInput(withOwner), identity.privateKey);
    return { ...withOwner, signature: bytesToBase64Url(sig) };
}

/**
 * Verify a manifest against the expected owner public key (base64url) — the app's
 * compiled-in trust anchor. True only if the declared owner matches AND the signature verifies.
 */
export function verifyManifest(
    manifest: RelayManifest,
    expectedOwnerPublicKeyB64: string,
): boolean {
    if (!manifest.signature) return false;
    if (manifest.ownerPublicKey !== expectedOwnerPublicKeyB64) return false;

    try {
        const input = buildManifestSigningInput(manifest);
        const sig = base64UrlToBytes(manifest.signature);
        const pub = base64UrlToBytes(expectedOwnerPublicKeyB64);
        return ed.verify(sig, input, pub);
    } catch {
        return false;
    }
}

// ─── Candidate resolution ────────────────────────────────────────────────────

/**
 * Merge override + manifest relays + defaults into one ordered, de-duplicated candidate list.
 * Precedence: user override → manifest relays → app defaults.
 */
export function resolveRelayCandidates(
    defaults: readonly string[],
    userOverride?: string | null,
    manifestRelays?: readonly string[] | null,
): string[] {
    const ordered: string[] = [];
    const add = (url?: string | null) => {
        if (url && !ordered.includes(url)) ordered.push(url);
    };

    add(userOverride);
    if (manifestRelays) for (const url of manifestRelays) add(url);
    for (const url of defaults) add(url);

    if (ordered.length === 0) {
        throw new Error("No relay candidates could be resolved — defaults were empty.");
    }
    return ordered;
}

function extractManifestRelays(manifest: RelayManifest): string[] {
    const urls: string[] = [];
    for (const endpoint of [...manifest.relays].sort((a, b) => a.priority - b.priority)) {
        urls.push(endpoint.url);
    }
    const now = Date.now();
    for (const fallback of manifest.escapeFallbacks ?? []) {
        if (new Date(fallback.validFrom).getTime() <= now) urls.push(fallback.url);
    }
    return urls;
}

// ─── Override + manifest stores ──────────────────────────────────────────────

/**
 * The user's local relay choice: the relay, plus the per-relay inputs the app runs with there.
 * `appId` is the app namespace to use on that relay (channels are remapped transparently);
 * `registerApp` asks for the namespace to be registered on connect.
 */
export interface RelayOverride {
    relay: string;
    appId?: string | null;
    registerApp?: boolean;
}

/** Parse a persisted override: the JSON object form, or the legacy plain-URL / `{url}` forms. */
export function parseRelayOverride(raw: unknown): RelayOverride | null {
    let value: unknown = raw;
    if (typeof value === "string") {
        const text: string = value.trim();
        if (text.length === 0) return null;
        if (!text.startsWith("{")) return { relay: text };
        try {
            value = JSON.parse(text);
        } catch {
            return null;
        }
    }
    if (value === null || typeof value !== "object") return null;
    const obj = value as { relay?: unknown; url?: unknown; appId?: unknown; registerApp?: unknown };
    const relay: unknown = obj.relay ?? obj.url;
    if (typeof relay !== "string" || relay.length === 0) return null;
    return {
        relay,
        appId: typeof obj.appId === "string" && obj.appId.length > 0 ? obj.appId : null,
        registerApp: obj.registerApp === true,
    };
}

/** Persists the user's local relay override — the individual escape hatch. */
export interface RelayOverrideStore {
    getOverride(): RelayOverride | null;
    setOverride(override: RelayOverride): void;
    clearOverride(): void;
}

/** Caches the latest verified manifest. */
export interface ManifestStore {
    getCached(): RelayManifest | null;
    save(manifest: RelayManifest): void;
}

/** A failed connection attempt against one relay candidate. */
export interface RelayAttempt {
    relay: string;
    /** Short, human-readable failure reason. */
    reason: string;
}

/** A relay outage the client cannot heal on its own: every candidate failed `passes` full passes. */
export interface RelaysExhaustedInfo {
    attempts: RelayAttempt[];
    passes: number;
}

/** Remembers verified federation peers so recovery has hints if every known relay dies. */
export interface PeerCacheStore {
    load(): DiscoveredRelay[];
    save(peers: DiscoveredRelay[]): void;
}

export class InMemoryPeerCacheStore implements PeerCacheStore {
    private peers: DiscoveredRelay[] = [];
    load(): DiscoveredRelay[] {
        return [...this.peers];
    }
    save(peers: DiscoveredRelay[]): void {
        this.peers = [...peers];
    }
}

/** A `localStorage`-backed peer cache for browser apps. */
export class LocalStoragePeerCacheStore implements PeerCacheStore {
    constructor(private readonly key: string) {}
    load(): DiscoveredRelay[] {
        const raw = globalThis.localStorage?.getItem(this.key);
        if (!raw) return [];
        try {
            const parsed: unknown = JSON.parse(raw);
            return Array.isArray(parsed) ? (parsed as DiscoveredRelay[]) : [];
        } catch {
            return [];
        }
    }
    save(peers: DiscoveredRelay[]): void {
        globalThis.localStorage?.setItem(this.key, JSON.stringify(peers.slice(0, 64)));
    }
}

export class InMemoryRelayOverrideStore implements RelayOverrideStore {
    private override: RelayOverride | null = null;
    getOverride(): RelayOverride | null {
        return this.override;
    }
    setOverride(override: RelayOverride): void {
        this.override = override;
    }
    clearOverride(): void {
        this.override = null;
    }
}

export class InMemoryManifestStore implements ManifestStore {
    private manifest: RelayManifest | null = null;
    getCached(): RelayManifest | null {
        return this.manifest;
    }
    save(manifest: RelayManifest): void {
        this.manifest = manifest;
    }
}

/** A `localStorage`-backed override store for browser apps. */
export class LocalStorageRelayOverrideStore implements RelayOverrideStore {
    constructor(private readonly key: string) {}
    getOverride(): RelayOverride | null {
        return parseRelayOverride(globalThis.localStorage?.getItem(this.key) ?? null);
    }
    setOverride(override: RelayOverride): void {
        globalThis.localStorage?.setItem(this.key, JSON.stringify(override));
    }
    clearOverride(): void {
        globalThis.localStorage?.removeItem(this.key);
    }
}

/** A `localStorage`-backed manifest cache for browser apps. */
export class LocalStorageManifestStore implements ManifestStore {
    constructor(private readonly key: string) {}
    getCached(): RelayManifest | null {
        const raw = globalThis.localStorage?.getItem(this.key);
        if (!raw) return null;
        try {
            return JSON.parse(raw) as RelayManifest;
        } catch {
            return null;
        }
    }
    save(manifest: RelayManifest): void {
        globalThis.localStorage?.setItem(this.key, JSON.stringify(manifest));
    }
}

// ─── RelayDirectory ──────────────────────────────────────────────────────────

type RelayDirectoryFactory = (config: VestaAppConfig) => RelayDirectory;

let defaultRelayDirectoryFactory: RelayDirectoryFactory | null = null;

// Some runtimes expose a `localStorage` getter that throws when it is not configured.
function hasLocalStorage(): boolean {
    try {
        return typeof globalThis.localStorage?.getItem === "function";
    } catch {
        return false;
    }
}

/**
 * Register how `RelayDirectory.createDefault` persists state. `vesta-client/node` calls this on
 * import to use file-backed stores under `~/.vesta/relays/`.
 */
export function setDefaultRelayDirectoryFactory(factory: RelayDirectoryFactory | null): void {
    defaultRelayDirectoryFactory = factory;
}

/**
 * Turns an app config, the user override, and the latest owner-signed manifest into an
 * ordered relay candidate list — and decides whether to trust an incoming manifest.
 */
export class RelayDirectory {
    private current: RelayManifest | null = null;
    private pendingOverride: RelayOverride | null = null;

    /**
     * A directory with the platform's default persistence: file-backed under `~/.vesta/relays/`
     * once `vesta-client/node` is imported, otherwise `localStorage` in browsers, else in memory.
     */
    static createDefault(config: VestaAppConfig): RelayDirectory {
        if (defaultRelayDirectoryFactory) return defaultRelayDirectoryFactory(config);

        if (hasLocalStorage()) {
            const key = (kind: string): string => `vesta.relays.${config.appId}.${kind}`;
            return new RelayDirectory(
                config,
                new LocalStorageRelayOverrideStore(key("override")),
                new LocalStorageManifestStore(key("manifest")),
                new LocalStoragePeerCacheStore(key("peers")),
            );
        }
        return new RelayDirectory(
            config,
            new InMemoryRelayOverrideStore(),
            new InMemoryManifestStore(),
            new InMemoryPeerCacheStore(),
        );
    }

    constructor(
        private readonly appConfig: VestaAppConfig,
        private readonly overrideStore?: RelayOverrideStore,
        private readonly manifestStore?: ManifestStore,
        private readonly peerCacheStore?: PeerCacheStore,
    ) {
        const config = appConfig;
        const cached = manifestStore?.getCached() ?? null;
        if (
            cached &&
            cached.appId === config.appId &&
            verifyManifest(cached, config.ownerPublicKey)
        ) {
            this.current = cached;
        }
    }

    get config(): VestaAppConfig {
        return this.appConfig;
    }

    get peerCache(): PeerCacheStore | undefined {
        return this.peerCacheStore;
    }

    /** The override in effect: a pending (on-probation) one wins over the persisted one. */
    get activeOverride(): RelayOverride | null {
        return this.pendingOverride ?? this.overrideStore?.getOverride() ?? null;
    }

    get hasPendingOverride(): boolean {
        return this.pendingOverride !== null;
    }

    get manifestChannel(): string {
        return manifestChannelFor(this.config.appId);
    }

    get currentManifest(): RelayManifest | null {
        return this.current;
    }

    resolveCandidates(): string[] {
        const override = this.activeOverride?.relay ?? undefined;
        const manifestRelays = this.current
            ? extractManifestRelays(this.current)
            : undefined;
        return resolveRelayCandidates(this.config.defaultRelays, override, manifestRelays);
    }

    tryApplyManifest(manifest: RelayManifest): boolean {
        if (manifest.appId !== this.config.appId) return false;
        if (!verifyManifest(manifest, this.config.ownerPublicKey)) return false;
        if (this.current && manifest.version <= this.current.version) return false;

        this.current = manifest;
        this.manifestStore?.save(manifest);
        return true;
    }

    /**
     * Set the user's override. With `persist` false it is only held pending (probation) until
     * {@link commitPendingOverride}; {@link discardPendingOverride} restores the previous state.
     */
    setUserOverride(override: RelayOverride, persist = true): void {
        if (persist) {
            if (!this.overrideStore) {
                throw new Error("No relay override store was configured.");
            }
            this.pendingOverride = null;
            this.overrideStore.setOverride(override);
            return;
        }
        this.pendingOverride = override;
    }

    commitPendingOverride(): void {
        // Without a store the choice stays held in memory for this session.
        if (!this.pendingOverride || !this.overrideStore) return;
        this.overrideStore.setOverride(this.pendingOverride);
        this.pendingOverride = null;
    }

    discardPendingOverride(): void {
        this.pendingOverride = null;
    }

    /** Clear both the pending and the persisted override. */
    clearUserOverride(): void {
        this.pendingOverride = null;
        this.overrideStore?.clearOverride();
    }
}
