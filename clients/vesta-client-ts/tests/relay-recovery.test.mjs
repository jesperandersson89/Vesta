// Relay-recovery tests: the headless RelayRecoverySession state machine, the peer cache, and the
// console picker. Mirrors tests/VestaClient.Tests/RelayRecoveryTests.cs.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import * as ed from "@noble/ed25519";

import {
    buildDescriptorSigningInput,
    bytesToBase64Url,
    deriveClientId,
    FederationClient,
    InMemoryPeerCacheStore,
    InMemoryRelayOverrideStore,
    RelayDirectory,
    RelayRecoverySession,
    setRelayPickerEnabled,
    VestaConnection,
    VestaIdentity,
} from "../dist/index.js";
import { FilePeerCacheStore } from "../dist/node.js";

// Importing the Node entry registers the web picker; never open a browser from tests.
setRelayPickerEnabled(false);

const DEAD_RELAY = "wss://dead.example/ws";
const owner = VestaIdentity.generate();
const ownerClientId = deriveClientId(owner.publicKey);

function config() {
    return { appId: "chess", ownerPublicKey: owner.publicKeyB64, defaultRelays: [DEAD_RELAY] };
}

function descriptor(url, hostsApp) {
    const relay = VestaIdentity.generate();
    const unsigned = {
        relayPublicKey: relay.publicKeyB64,
        urls: [url],
        apps: hostsApp ? [{ appId: "chess", ownerClientId }] : [],
        issuedAt: new Date().toISOString(),
        ttlSeconds: 300,
    };
    const sig = ed.sign(buildDescriptorSigningInput(unsigned), relay.privateKey);
    return { ...unsigned, signature: bytesToBase64Url(sig) };
}

function federation(...descriptors) {
    const fetchImpl = async () => ({ ok: true, json: async () => descriptors });
    return new FederationClient(config(), fetchImpl);
}

class FakeHost extends EventEmitter {
    constructor(directory, connectSucceeds = true) {
        super();
        this.relayDirectory = directory;
        this.relays = [DEAD_RELAY];
        this.autoReconnect = true;
        this.adopted = [];
        this.overrides = [];
        this.connectSucceeds = connectSucceeds;
    }
    async reconnect() {
        return this.connectSucceeds;
    }
    async adoptRelay(override) {
        this.adopted.push(override.relay);
        this.overrides.push(override);
        return this.connectSucceeds
            ? { connected: true, failureReason: null }
            : { connected: false, failureReason: "refused" };
    }
    async clearRelayOverride() {
        return true;
    }
}

function exhausted() {
    return { attempts: [{ relay: DEAD_RELAY, reason: "connection refused" }], passes: 3 };
}

function setup(opts = {}) {
    const directory = new RelayDirectory(
        config(),
        new InMemoryRelayOverrideStore(),
        undefined,
        opts.peerCache,
    );
    const host = new FakeHost(directory, opts.connectSucceeds ?? true);
    const session = new RelayRecoverySession(host, opts.federation ?? federation());
    return { host, session };
}

test("FilePeerCacheStore round-trips peers and tolerates a corrupt file", () => {
    const dir = mkdtempSync(join(tmpdir(), "vesta-peers-"));
    try {
        const path = join(dir, "peers.json");
        const peer = {
            relayPublicKey: "key",
            urls: ["wss://a.example/ws"],
            hostsRequestedApp: true,
            issuedAt: new Date().toISOString(),
        };
        new FilePeerCacheStore(path).save([peer]);
        const loaded = new FilePeerCacheStore(path).load();
        assert.equal(loaded.length, 1);
        assert.equal(loaded[0].relayPublicKey, "key");

        const corrupt = join(dir, "corrupt.json");
        writeFileSync(corrupt, "{ not json");
        assert.deepEqual(new FilePeerCacheStore(corrupt).load(), []);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});

class RefusingSocket {
    constructor() {
        this._listeners = new Map();
        setImmediate(() => {
            for (const l of this._listeners.get("close") ?? []) l({ reason: "connection refused" });
        });
    }
    addEventListener(event, listener) {
        if (!this._listeners.has(event)) this._listeners.set(event, new Set());
        this._listeners.get(event).add(listener);
    }
    removeEventListener() {}
    send() {}
    close() {}
}

test("VestaConnection with all relays unreachable fires relaysExhausted with per-relay reasons", async () => {
    const relay = "ws://unreachable.example/ws";
    const conn = new VestaConnection({
        relays: [relay],
        clientId: "test-client",
        channels: ["chess/room"],
        createSocket: () => new RefusingSocket(),
        autoReconnect: true,
        relayExhaustionPasses: 2,
        initialReconnectDelay: 1,
        maxReconnectDelay: 2,
    });
    const info = await new Promise((resolve) => {
        conn.on("relaysExhausted", resolve);
        conn.connect();
    });
    conn.dispose();
    assert.equal(info.passes, 2);
    assert.equal(info.attempts[0].relay, relay);
    assert.equal(info.attempts[0].reason, "connection refused");
});

test("RelayRecoverySession: exhaustion signal moves to exhausted with attempts", () => {
    const { host, session } = setup();

    host.emit("disconnected", "lost");
    assert.equal(session.snapshot.phase, "degraded");

    host.emit("relaysExhausted", exhausted());
    assert.equal(session.snapshot.phase, "exhausted");
    assert.equal(session.snapshot.triedRelays[0].reason, "connection refused");
    assert.equal(session.snapshot.backgroundRetrying, true);
});

test("RelayRecoverySession.reportExhausted opens the prompt without a prior outage", () => {
    const { session } = setup();

    session.reportExhausted(exhausted());

    assert.equal(session.snapshot.phase, "exhausted");
    assert.equal(session.snapshot.triedRelays[0].reason, "connection refused");
});

test("RelayRecoverySession.discover lists app-hosting relays first and never adopts", async () => {
    const { host, session } = setup({
        federation: federation(
            descriptor("wss://other.example/ws", false),
            descriptor("wss://hosts.example/ws", true),
        ),
    });

    host.emit("relaysExhausted", exhausted());
    await session.discover();

    assert.equal(session.snapshot.phase, "choices");
    assert.equal(session.snapshot.choices[0].url, "wss://hosts.example/ws");
    assert.equal(session.snapshot.choices[0].hostsRequestedApp, true);
    assert.ok(session.snapshot.choices.some((c) => !c.hostsRequestedApp));
    assert.equal(host.adopted.length, 0);
});

test("RelayRecoverySession.discover with nothing reachable reports nothingFound", async () => {
    const { host, session } = setup();
    host.emit("relaysExhausted", exhausted());
    await session.discover();
    assert.equal(session.snapshot.phase, "nothingFound");
});

test("RelayRecoverySession.discover falls back to the persisted peer cache", async () => {
    const peerCache = new InMemoryPeerCacheStore();
    peerCache.save([
        {
            relayPublicKey: "k",
            urls: ["wss://remembered.example/ws"],
            hostsRequestedApp: true,
            issuedAt: new Date().toISOString(),
        },
    ]);
    const { host, session } = setup({ peerCache });

    host.emit("relaysExhausted", exhausted());
    await session.discover();

    assert.equal(session.snapshot.choices.length, 1);
    assert.equal(session.snapshot.choices[0].fromCache, true);
    assert.equal(session.snapshot.choices[0].url, "wss://remembered.example/ws");
});

test("RelayRecoverySession.adopt success returns to healthy", async () => {
    const { host, session } = setup();
    const choice = {
        url: "wss://new.example/ws",
        relayPublicKey: "k",
        hostsRequestedApp: true,
        fromCache: false,
    };

    host.emit("relaysExhausted", exhausted());
    await session.adopt(choice);

    assert.equal(session.snapshot.phase, "healthy");
    assert.deepEqual(host.adopted, ["wss://new.example/ws"]);
});

test("RelayRecoverySession.useManual failure reports failed and keeps retrying", async () => {
    const { host, session } = setup({ connectSucceeds: false });

    host.emit("relaysExhausted", exhausted());
    await session.useManual("https://typed.example");

    assert.equal(session.snapshot.phase, "failed");
    assert.deepEqual(host.adopted, ["wss://typed.example/"]);
    assert.equal(session.snapshot.backgroundRetrying, true);
});

test("RelayRecoverySession.useManual rejects an invalid URL without adopting", async () => {
    const { host, session } = setup();
    await session.useManual("ftp://nope.example");
    assert.equal(session.snapshot.phase, "failed");
    assert.equal(host.adopted.length, 0);
});

test("RelayRecoverySession.dismiss hides the prompt but keeps retrying", () => {
    const { host, session } = setup();
    host.emit("relaysExhausted", exhausted());
    session.dismiss();
    assert.equal(session.snapshot.phase, "degraded");
    assert.equal(session.snapshot.backgroundRetrying, true);
});

test("RelayRecoverySession: background reconnect returns to healthy", () => {
    const { host, session } = setup();
    host.emit("relaysExhausted", exhausted());
    host.emit("reconnected");
    assert.equal(session.snapshot.phase, "healthy");
    assert.equal(session.snapshot.backgroundRetrying, false);
});

test("RelayRecoverySession.adopt forwards the chosen app namespace and registration", async () => {
    const { host, session } = setup();
    const choice = {
        url: "wss://new.example/ws",
        relayPublicKey: "k",
        hostsRequestedApp: false,
        fromCache: false,
        acceptsUnregisteredApps: true,
    };

    host.emit("relaysExhausted", exhausted());
    await session.adopt(choice, { appId: "chess-2", registerApp: true });

    assert.equal(host.overrides[0].appId, "chess-2");
    assert.equal(host.overrides[0].registerApp, true);
});

test("RelayRecoverySession.adopt rejects an invalid namespace without adopting", async () => {
    const { host, session } = setup();
    const choice = {
        url: "wss://new.example/ws",
        relayPublicKey: "k",
        hostsRequestedApp: false,
        fromCache: false,
        acceptsUnregisteredApps: null,
    };

    await session.adopt(choice, { appId: "Not Valid!" });

    assert.equal(session.snapshot.phase, "failed");
    assert.equal(host.adopted.length, 0);
});
