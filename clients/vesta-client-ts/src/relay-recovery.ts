/**
 * Headless relay-recovery state machine — the TypeScript mirror of C# `RelayRecoverySession`.
 * Every recovery UI (browser element, console, future iOS/Android views) only renders
 * {@link RelayRecoverySession.snapshot} and calls the input methods. A relay is never adopted
 * without an explicit `adopt` / `useManual` call, and background reconnect keeps running.
 *
 * Healthy → Degraded → Exhausted → Discovering → Choices | NothingFound → Adopting → Healthy | Failed
 */

import { FederationClient } from "./federation.js";
import type { DiscoveredRelay } from "./federation.js";
import type { RelayAttempt, RelayDirectory, RelaysExhaustedInfo } from "./relay.js";

/** The slice of `VestaConnection` a {@link RelayRecoverySession} drives. */
export interface RelayRecoveryHost {
    readonly relayDirectory: RelayDirectory | undefined;
    readonly relays: readonly string[];
    readonly autoReconnect: boolean;
    on(event: "disconnected", listener: (reason: string) => void): unknown;
    on(event: "reconnected", listener: () => void): unknown;
    on(event: "relaysExhausted", listener: (info: RelaysExhaustedInfo) => void): unknown;
    off(event: "disconnected", listener: (reason: string) => void): unknown;
    off(event: "reconnected", listener: () => void): unknown;
    off(event: "relaysExhausted", listener: (info: RelaysExhaustedInfo) => void): unknown;
    reconnect(): Promise<boolean>;
    adoptRelay(url: string): Promise<boolean>;
    clearRelayOverride(): Promise<boolean>;
}

export type RelayRecoveryPhase =
    | "healthy"
    | "degraded"
    | "exhausted"
    | "discovering"
    | "choices"
    | "nothingFound"
    | "adopting"
    | "failed";

/** A relay the user can choose to move to. Always unverified as to whether it holds the app's data. */
export interface RelayChoice {
    url: string;
    /** The relay's descriptor key, or null for a manually entered URL. */
    relayPublicKey: string | null;
    /** True if the relay verifiably advertised this app under the trusted owner. */
    hostsRequestedApp: boolean;
    /** True if only the persisted peer cache knows this relay (not confirmed live this session). */
    fromCache: boolean;
}

/** Everything a view needs to render the recovery prompt. Views hold no other state. */
export interface RelayRecoverySnapshot {
    phase: RelayRecoveryPhase;
    triedRelays: RelayAttempt[];
    choices: RelayChoice[];
    activeOverride: string | null;
    backgroundRetrying: boolean;
    failedPasses: number;
    adopting: string | null;
    failureReason: string | null;
}

const MAX_FEDERATION_BASES = 16;
const INVALID_URL_MESSAGE =
    "Not a valid relay URL (expected ws://, wss://, http:// or https://).";

export class RelayRecoverySession {
    private readonly host: RelayRecoveryHost;
    private readonly directory: RelayDirectory;
    private readonly federation: FederationClient;
    private current: RelayRecoverySnapshot;
    private readonly listeners = new Set<(snapshot: RelayRecoverySnapshot) => void>();

    private readonly onDisconnected = (): void => this.handleDisconnected();
    private readonly onReconnected = (): void => this.update(this.build("healthy"));
    private readonly onExhausted = (info: RelaysExhaustedInfo): void => this.handleExhausted(info);

    constructor(host: RelayRecoveryHost, federation?: FederationClient) {
        if (!host.relayDirectory) {
            throw new Error("RelayRecoverySession requires a connection with a relayDirectory attached.");
        }
        this.host = host;
        this.directory = host.relayDirectory;
        this.federation = federation ?? new FederationClient(this.directory.config);
        this.current = this.build("healthy");

        host.on("disconnected", this.onDisconnected);
        host.on("reconnected", this.onReconnected);
        host.on("relaysExhausted", this.onExhausted);
    }

    get snapshot(): RelayRecoverySnapshot {
        return this.current;
    }

    /** Subscribe to state changes. Returns an unsubscribe function. */
    onChange(listener: (snapshot: RelayRecoverySnapshot) => void): () => void {
        this.listeners.add(listener);
        return () => {
            this.listeners.delete(listener);
        };
    }

    dispose(): void {
        this.host.off("disconnected", this.onDisconnected);
        this.host.off("reconnected", this.onReconnected);
        this.host.off("relaysExhausted", this.onExhausted);
        this.listeners.clear();
    }

    /** Open the prompt for an outage the session didn't witness (e.g. a first connect that failed). */
    reportExhausted(info: RelaysExhaustedInfo): void {
        this.handleExhausted(info);
    }

    /** Try the current candidates again right now. */
    async retry(): Promise<void> {
        if (await this.host.reconnect()) this.update(this.build("healthy"));
    }

    /** Ask reachable relays (cache, seeds, known candidates) which relays host this app. */
    async discover(): Promise<void> {
        this.update({ ...this.current, phase: "discovering", choices: [], failureReason: null });
        const choices: RelayChoice[] = await this.collectChoices();
        this.update({
            ...this.current,
            phase: choices.length > 0 ? "choices" : "nothingFound",
            choices,
        });
    }

    /** Move to a relay the user picked from `snapshot.choices`. */
    adopt(choice: RelayChoice): Promise<void> {
        return this.adoptCore(choice.url);
    }

    /** Move to a relay URL the user typed or scanned. Accepts ws/wss (http/https are mapped). */
    useManual(url: string): Promise<void> {
        const relay: string | null = RelayRecoverySession.normalize(url);
        if (relay === null) {
            this.update({ ...this.current, phase: "failed", failureReason: INVALID_URL_MESSAGE });
            return Promise.resolve();
        }
        return this.adoptCore(relay);
    }

    /** Drop the user's override and go back to manifest/default relays. */
    async clearOverride(): Promise<void> {
        const connected: boolean = await this.host.clearRelayOverride();
        this.update(connected ? this.build("healthy") : { ...this.current, activeOverride: null });
    }

    /** Hide the prompt. Background reconnect continues; the prompt returns on the next exhaustion. */
    dismiss(): void {
        this.update({ ...this.current, phase: "degraded", choices: [], failureReason: null });
    }

    private async adoptCore(relay: string): Promise<void> {
        this.update({ ...this.current, phase: "adopting", adopting: relay, failureReason: null });

        let connected: boolean;
        try {
            connected = await this.host.adoptRelay(relay);
        } catch (error) {
            this.update({
                ...this.current,
                phase: "failed",
                adopting: null,
                failureReason: error instanceof Error ? error.message : String(error),
            });
            return;
        }

        this.update(
            connected
                ? this.build("healthy")
                : {
                      ...this.current,
                      phase: "failed",
                      adopting: null,
                      activeOverride: this.directory.activeOverride,
                      failureReason: `Could not connect to ${relay}.`,
                  },
        );
    }

    private async collectChoices(): Promise<RelayChoice[]> {
        const config = this.directory.config;
        const cached: DiscoveredRelay[] = this.directory.peerCache?.load() ?? [];

        const seeds: string[] = [
            ...cached.flatMap((peer) => peer.urls),
            ...(config.discoverySeeds ?? []),
            ...this.host.relays,
            ...config.defaultRelays,
        ];

        const bases: string[] = [];
        for (const seed of seeds) {
            const base: string | null = FederationClient.toFederationBaseUrl(seed);
            if (base !== null && !bases.includes(base)) bases.push(base);
        }

        const live: DiscoveredRelay[][] = await Promise.all(
            bases.slice(0, MAX_FEDERATION_BASES).map((base) => this.query(base)),
        );

        const merged = new Map<string, RelayChoice>();
        for (const peer of cached) {
            for (const url of peer.urls) {
                merged.set(url, {
                    url,
                    relayPublicKey: peer.relayPublicKey,
                    hostsRequestedApp: peer.hostsRequestedApp,
                    fromCache: true,
                });
            }
        }
        for (const relays of live) {
            for (const relay of relays) {
                for (const url of relay.urls) {
                    const prior: RelayChoice | undefined = merged.get(url);
                    const hosts: boolean =
                        relay.hostsRequestedApp ||
                        (prior !== undefined && !prior.fromCache && prior.hostsRequestedApp);
                    merged.set(url, {
                        url,
                        relayPublicKey: relay.relayPublicKey,
                        hostsRequestedApp: hosts,
                        fromCache: false,
                    });
                }
            }
        }

        // Relays that just failed us are not useful choices.
        const failed = new Set<string>(this.current.triedRelays.map((a) => a.relay));
        return [...merged.values()]
            .filter((c) => !failed.has(c.url))
            .sort(
                (a, b) =>
                    Number(b.hostsRequestedApp) - Number(a.hostsRequestedApp) ||
                    Number(a.fromCache) - Number(b.fromCache),
            );
    }

    private async query(base: string): Promise<DiscoveredRelay[]> {
        const hosting: DiscoveredRelay[] = await this.federation.discoverRelaysForApp(base);
        const all: DiscoveredRelay[] = await this.federation.listAllRelays(base);
        return [...hosting, ...all];
    }

    /** Returns a normalized ws/wss URL, or null when `url` is not a usable relay address. */
    static normalize(url: string): string | null {
        let parsed: URL;
        try {
            parsed = new URL(url.trim());
        } catch {
            return null;
        }
        const scheme: string | null = (
            { "ws:": "ws:", "wss:": "wss:", "http:": "ws:", "https:": "wss:" } as Record<string, string>
        )[parsed.protocol] ?? null;
        if (scheme === null) return null;
        // Rebuild rather than assign `protocol`, whose special-scheme rules vary between runtimes.
        return new URL(`${scheme}//${parsed.host}${parsed.pathname}${parsed.search}`).toString();
    }

    private handleDisconnected(): void {
        if (this.current.phase === "healthy") this.update({ ...this.current, phase: "degraded" });
    }

    private handleExhausted(info: RelaysExhaustedInfo): void {
        this.update({
            ...this.current,
            phase: "exhausted",
            triedRelays: [...info.attempts],
            failedPasses: info.passes,
            choices: [],
            failureReason: null,
        });
    }

    private build(phase: RelayRecoveryPhase): RelayRecoverySnapshot {
        return {
            phase,
            triedRelays: [],
            choices: [],
            activeOverride: this.directory.activeOverride,
            backgroundRetrying: false,
            failedPasses: 0,
            adopting: null,
            failureReason: null,
        };
    }

    private update(next: RelayRecoverySnapshot): void {
        const retrying: boolean =
            next.phase !== "healthy" && next.phase !== "adopting" && this.host.autoReconnect;
        this.current = { ...next, backgroundRetrying: retrying };
        for (const listener of [...this.listeners]) listener(this.current);
    }
}
