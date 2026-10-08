// Zero-plumbing connection surface: appConfig auto-wiring, identity-derived clientId, the global
// WebSocket default, and the owner relay-manifest helper. Run after `npm run build`.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import {
    InMemoryClientEventStore,
    RelayDirectory,
    VestaConnection,
    VestaIdentity,
    loadIdentityFile,
    manifestChannelFor,
    verifyManifest,
} from "../dist/index.js";

test("loadIdentityFile restores the identity from an exported JSON file", async () => {
    const identity = VestaIdentity.generate();
    const path = join(mkdtempSync(join(tmpdir(), "vesta-id-")), "app.identity.json");
    writeFileSync(path, JSON.stringify(identity.toJSON()));

    const loaded = await loadIdentityFile(path);

    assert.equal(loaded.clientId, identity.clientId);
    assert.equal(loaded.publicKeyB64, identity.publicKeyB64);
});

class FakeSocket {
    constructor() {
        this.readyState = 0;
        this.sent = [];
        this._listeners = new Map();
    }
    addEventListener(event, listener) {
        if (!this._listeners.has(event)) this._listeners.set(event, new Set());
        this._listeners.get(event).add(listener);
    }
    removeEventListener(event, listener) {
        this._listeners.get(event)?.delete(listener);
    }
    send(data) {
        this.sent.push(JSON.parse(data));
    }
    close() {
        this.readyState = 3;
    }
    open() {
        this.readyState = 1;
        for (const l of this._listeners.get("open") ?? []) l();
    }
}

function appConfig(owner, overrides = {}) {
    return {
        appId: "zero-plumbing",
        ownerPublicKey: owner.publicKeyB64,
        defaultRelays: ["wss://a.example/ws", "wss://b.example/ws"],
        ...overrides,
    };
}

test("RelayDirectory.createDefault builds a directory whose candidates are the default relays", () => {
    const owner = VestaIdentity.generate();

    const directory = RelayDirectory.createDefault(appConfig(owner));

    assert.deepEqual(directory.resolveCandidates(), ["wss://a.example/ws", "wss://b.example/ws"]);
});

test("VestaConnection seeds relays and the directory from appConfig alone", () => {
    const owner = VestaIdentity.generate();

    const conn = new VestaConnection({
        appConfig: appConfig(owner),
        identity: owner,
        channels: ["zero-plumbing/room"],
        createSocket: () => new FakeSocket(),
    });

    assert.deepEqual([...conn.relays], ["wss://a.example/ws", "wss://b.example/ws"]);
    assert.ok(conn.relayDirectory);
    conn.dispose();
});

test("VestaConnection derives clientId and publicKey from identity", () => {
    const owner = VestaIdentity.generate();
    const sockets = [];
    const conn = new VestaConnection({
        appConfig: appConfig(owner),
        identity: owner,
        channels: ["zero-plumbing/room"],
        autoReconnect: false,
        createSocket: () => {
            const s = new FakeSocket();
            sockets.push(s);
            return s;
        },
    });

    conn.connect();
    sockets[0].open();

    const hello = sockets[0].sent.find((m) => m.type === "HELLO");
    assert.equal(hello.clientId, owner.clientId);
    assert.equal(hello.publicKey, owner.publicKeyB64);
    conn.dispose();
});

test("VestaConnection without relays, appConfig or clientId throws a descriptive error", () => {
    assert.throws(
        () => new VestaConnection({ channels: ["a/b"], clientId: "c", createSocket: () => new FakeSocket() }),
        /appConfig/,
    );
    const owner = VestaIdentity.generate();
    assert.throws(
        () => new VestaConnection({ appConfig: appConfig(owner), channels: ["a/b"], createSocket: () => new FakeSocket() }),
        /clientId/,
    );
});

test("VestaConnection without createSocket explains how to supply one when no global WebSocket exists", () => {
    const owner = VestaIdentity.generate();
    const original = Object.getOwnPropertyDescriptor(globalThis, "WebSocket");
    Object.defineProperty(globalThis, "WebSocket", { value: undefined, configurable: true, writable: true });
    try {
        assert.throws(
            () => new VestaConnection({ appConfig: appConfig(owner), identity: owner, channels: ["a/b"] }),
            /createSocket/,
        );
    } finally {
        if (original) Object.defineProperty(globalThis, "WebSocket", original);
        else delete globalThis.WebSocket;
    }
});

test("publishRelayManifest as owner queues a verifiable manifest event with the next version", async () => {
    const owner = VestaIdentity.generate();
    const store = new InMemoryClientEventStore();
    const conn = new VestaConnection({
        appConfig: appConfig(owner),
        identity: owner,
        channels: ["zero-plumbing/room"],
        localStore: store,
        createSocket: () => new FakeSocket(),
    });

    const manifest = conn.publishRelayManifest(["wss://new.example/ws"]);

    assert.equal(manifest.version, 1);
    assert.ok(verifyManifest(manifest, owner.publicKeyB64));
    const pending = await store.getPendingOutbox();
    assert.equal(pending.length, 1);
    assert.equal(pending[0].event.channelId, manifestChannelFor("zero-plumbing"));
    assert.equal(pending[0].event.eventType, "vesta.relay-manifest");
    conn.dispose();
});

test("publishRelayManifest as a non-owner throws", () => {
    const owner = VestaIdentity.generate();
    const stranger = VestaIdentity.generate();
    const conn = new VestaConnection({
        appConfig: appConfig(owner),
        identity: stranger,
        channels: ["zero-plumbing/room"],
        localStore: new InMemoryClientEventStore(),
        createSocket: () => new FakeSocket(),
    });

    assert.throws(() => conn.publishRelayManifest(["wss://new.example/ws"]), /app owner/);
    conn.dispose();
});
