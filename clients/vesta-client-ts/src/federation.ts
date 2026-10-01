/**
 * Server-to-server discovery (federation) — the TypeScript mirror of the C#
 * `VestaCore.Relay.ServerDescriptor` / `VestaCore.Relay.DescriptorSigner` /
 * `VestaClient.Federation.FederationClient`.
 *
 * A discovery-enabled relay self-signs and publishes a `ServerDescriptor` advertising itself
 * and the discoverable apps it hosts. Peers gossip these by anti-entropy pull, so a client
 * reaching any relay in the mesh can learn which relays host a given app — without a central
 * hub. Discovered relays are always show-only: verified but never auto-adopted.
 */

import * as ed from "@noble/ed25519";

import { base64UrlToBytes, canonicalize, deriveClientId, normalizeTimestampForSigning } from "./signing.js";
import type { VestaAppConfig } from "./relay.js";

// ─── Types ───────────────────────────────────────────────────────────────────

/** An app a relay advertises as discoverable. */
export interface DiscoverableApp {
    appId: string;
    /** The client id recorded as the app's owner on that relay (hash of the owner's Ed25519 public key). */
    ownerClientId: string;
}

/** A relay's self-asserted, signed advertisement of itself for server-to-server discovery. */
export interface ServerDescriptor {
    /** base64url Ed25519 public key that identifies this relay and signs the descriptor. */
    relayPublicKey: string;
    /** The relay's publicly reachable WebSocket URLs, in preference order. */
    urls: string[];
    /** The discoverable apps this relay hosts. May be empty. */
    apps: DiscoverableApp[];
    /** When the descriptor was issued (ISO 8601). */
    issuedAt: string;
    /** How long (seconds) the descriptor stays valid. */
    ttlSeconds: number;
    /** True if the relay runs in open mode (accepts apps without registration). Absent on older relays. */
    acceptsUnregisteredApps?: boolean | null;
    /** base64url Ed25519 signature over the canonical descriptor. */
    signature?: string;
}

/** A relay discovered through federation — verified but never auto-adopted. */
export interface DiscoveredRelay {
    relayPublicKey: string;
    urls: string[];
    /** True when this relay verifiably hosts the specifically queried app. */
    hostsRequestedApp: boolean;
    issuedAt: string;
    /** True if the relay's signed descriptor says it is open; null/undefined if it didn't say. */
    acceptsUnregisteredApps?: boolean | null;
}

// ─── Signing / verification ──────────────────────────────────────────────────

/** Canonical signing-input bytes for a descriptor (matches C# DescriptorSigner.BuildSigningInput). */
export function buildDescriptorSigningInput(descriptor: ServerDescriptor): Uint8Array {
    const fields: Record<string, unknown> = {
        apps: descriptor.apps.map((a) => ({ appId: a.appId, ownerClientId: a.ownerClientId })),
        issuedAt: normalizeTimestampForSigning(descriptor.issuedAt),
        relayPublicKey: descriptor.relayPublicKey,
        ttlSeconds: descriptor.ttlSeconds,
        urls: descriptor.urls,
    };
    if (typeof descriptor.acceptsUnregisteredApps === "boolean") {
        fields.acceptsUnregisteredApps = descriptor.acceptsUnregisteredApps;
    }
    return new TextEncoder().encode(canonicalize(fields));
}

/** True once `issuedAt + ttlSeconds` has passed relative to `now`. */
export function isDescriptorExpired(descriptor: ServerDescriptor, now: Date = new Date()): boolean {
    const expiry = new Date(descriptor.issuedAt).getTime() + descriptor.ttlSeconds * 1000;
    return now.getTime() > expiry;
}

/** Verify a descriptor's self-signature. Proves authorship, not trustworthiness of its claims. */
export function verifyDescriptor(descriptor: ServerDescriptor): boolean {
    if (!descriptor.signature || !descriptor.relayPublicKey) return false;
    try {
        const input = buildDescriptorSigningInput(descriptor);
        const sig = base64UrlToBytes(descriptor.signature);
        const pub = base64UrlToBytes(descriptor.relayPublicKey);
        return ed.verify(sig, input, pub);
    } catch {
        return false;
    }
}

// ─── FederationClient ────────────────────────────────────────────────────────

/**
 * Pulls signed `ServerDescriptor` records from a relay's public `/federation/*` HTTP surface so
 * a client whose relays are failing can discover other relays in the mesh.
 *
 * Every descriptor is verified two ways before being surfaced: (1) signature — proves the
 * relay key authored it; (2) ownership — the advertised `ownerClientId` is compared against the
 * client id derived from the app's compiled-in `ownerPublicKey`; a relay claiming to host the
 * app under a different owner is dropped as a spoof.
 */
export class FederationClient {
    private readonly expectedOwnerClientId: string;

    constructor(
        private readonly appConfig: VestaAppConfig,
        private readonly fetchImpl: typeof fetch = fetch,
    ) {
        this.expectedOwnerClientId = deriveClientId(base64UrlToBytes(appConfig.ownerPublicKey));
    }

    /** Ask a reachable relay which relays (itself included) host this app. */
    async discoverRelaysForApp(federationBaseUrl: string): Promise<DiscoveredRelay[]> {
        const url = new URL(`/federation/apps/${encodeURIComponent(this.appConfig.appId)}`, federationBaseUrl);
        const descriptors = await this.fetchDescriptors(url);

        const results: DiscoveredRelay[] = [];
        for (const descriptor of descriptors) {
            if (!this.isAuthentic(descriptor)) continue;
            if (!this.advertisesAppForOwner(descriptor)) continue;
            results.push(this.toDiscoveredRelay(descriptor, true));
        }
        return this.orderNewestFirst(results);
    }

    /** Browse every relay a reachable relay knows about. */
    async listAllRelays(federationBaseUrl: string): Promise<DiscoveredRelay[]> {
        const url = new URL("/federation/peers", federationBaseUrl);
        const descriptors = await this.fetchDescriptors(url);

        const results: DiscoveredRelay[] = [];
        for (const descriptor of descriptors) {
            if (!this.isAuthentic(descriptor)) continue;
            results.push(this.toDiscoveredRelay(descriptor, this.advertisesAppForOwner(descriptor)));
        }
        return this.orderNewestFirst(results);
    }

    private async fetchDescriptors(url: URL): Promise<ServerDescriptor[]> {
        try {
            const response = await this.fetchImpl(url, { signal: AbortSignal.timeout(10_000) });
            if (!response.ok) return [];
            const body = (await response.json()) as ServerDescriptor[];
            return Array.isArray(body) ? body : [];
        } catch {
            return [];
        }
    }

    private isAuthentic(descriptor: ServerDescriptor): boolean {
        return !isDescriptorExpired(descriptor) && verifyDescriptor(descriptor);
    }

    private advertisesAppForOwner(descriptor: ServerDescriptor): boolean {
        return descriptor.apps.some(
            (a) => a.appId === this.appConfig.appId && a.ownerClientId === this.expectedOwnerClientId,
        );
    }

    private toDiscoveredRelay(descriptor: ServerDescriptor, hostsRequestedApp: boolean): DiscoveredRelay {
        return {
            relayPublicKey: descriptor.relayPublicKey,
            urls: descriptor.urls,
            hostsRequestedApp,
            issuedAt: descriptor.issuedAt,
            acceptsUnregisteredApps: descriptor.acceptsUnregisteredApps ?? null,
        };
    }

    private orderNewestFirst(relays: DiscoveredRelay[]): DiscoveredRelay[] {
        return [...relays].sort((a, b) => new Date(b.issuedAt).getTime() - new Date(a.issuedAt).getTime());
    }

    /**
     * Derive the HTTP(S) federation base URL from a relay's WebSocket URL: `ws` → `http`,
     * `wss` → `https`, with any path/query stripped.
     */
    static toFederationBaseUrl(relayUrl: string): string | null {
        let parsed: URL;
        try {
            parsed = new URL(relayUrl);
        } catch {
            return null;
        }
        const scheme = parsed.protocol === "ws:" || parsed.protocol === "http:"
            ? "http:"
            : parsed.protocol === "wss:" || parsed.protocol === "https:"
              ? "https:"
              : null;
        if (!scheme) return null;
        return `${scheme}//${parsed.host}/`;
    }
}
