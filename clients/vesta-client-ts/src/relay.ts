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

/** Persists the user's local relay override — the individual escape hatch. */
export interface RelayOverrideStore {
    getOverride(): string | null;
    setOverride(url: string): void;
    clearOverride(): void;
}

/** Caches the latest verified manifest. */
export interface ManifestStore {
    getCached(): RelayManifest | null;
    save(manifest: RelayManifest): void;
}

export class InMemoryRelayOverrideStore implements RelayOverrideStore {
    private override: string | null = null;
    getOverride(): string | null {
        return this.override;
    }
    setOverride(url: string): void {
        this.override = url;
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
    getOverride(): string | null {
        return globalThis.localStorage?.getItem(this.key) ?? null;
    }
    setOverride(url: string): void {
        globalThis.localStorage?.setItem(this.key, url);
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

/**
 * Turns an app config, the user override, and the latest owner-signed manifest into an
 * ordered relay candidate list — and decides whether to trust an incoming manifest.
 */
export class RelayDirectory {
    private current: RelayManifest | null = null;

    constructor(
        private readonly config: VestaAppConfig,
        private readonly overrideStore?: RelayOverrideStore,
        private readonly manifestStore?: ManifestStore,
    ) {
        const cached = manifestStore?.getCached() ?? null;
        if (
            cached &&
            cached.appId === config.appId &&
            verifyManifest(cached, config.ownerPublicKey)
        ) {
            this.current = cached;
        }
    }

    get manifestChannel(): string {
        return manifestChannelFor(this.config.appId);
    }

    get currentManifest(): RelayManifest | null {
        return this.current;
    }

    resolveCandidates(): string[] {
        const override = this.overrideStore?.getOverride() ?? undefined;
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

    setUserOverride(url: string): void {
        if (!this.overrideStore) {
            throw new Error("No relay override store was configured.");
        }
        this.overrideStore.setOverride(url);
    }

    clearUserOverride(): void {
        if (!this.overrideStore) {
            throw new Error("No relay override store was configured.");
        }
        this.overrideStore.clearOverride();
    }
}
