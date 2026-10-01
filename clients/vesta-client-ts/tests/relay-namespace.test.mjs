// Relay namespace tests: override parsing, channel remapping, register-on-connect and the
// probationary adoption. Mirrors tests/VestaClient.Tests/RelayNamespaceTests.cs.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";
import * as ed from "@noble/ed25519";

import {
    base64UrlToBytes,
    buildSigningInput,
    InMemoryRelayOverrideStore,
    parseRelayOverride,
    RelayDirectory,
    remapChannel,
    setRelayPickerEnabled,
    VestaConnection,
    VestaIdentity,
} from "../dist/index.js";

setRelayPickerEnabled(false);

const tick = () => new Promise((resolve) => setImmediate(resolve));
const NIL = "00000000-0000-0000-0000-000000000000";
const CONFIG = { appId: "chess", ownerPublicKey: "unused", defaultRelays: ["wss://home.example/ws"] };

// ── parseRelayOverride / remapChannel ────────────────────────────────────────

test("parseRelayOverride: reads the object form", () => {
    const parsed = parseRelayOverride('{"relay":"wss://a/ws","appId":"chess-2","registerApp":true}');
    assert.deepEqual(parsed, { relay: "wss://a/ws", appId: "chess-2", registerApp: true });
});

test("parseRelayOverride: accepts the legacy plain-URL and {url} forms", () => {
    assert.deepEqual(parseRelayOverride("wss://a/ws\n"), { relay: "wss://a/ws" });
    assert.deepEqual(parseRelayOverride('{"url":"wss://a/ws"}'), {
        relay: "wss://a/ws",
        appId: null,
        registerApp: false,
    });
});

test("parseRelayOverride: rejects empty and malformed input", () => {
    assert.equal(parseRelayOverride(""), null);
    assert.equal(parseRelayOverride("{ not json"), null);
    assert.equal(parseRelayOverride('{"appId":"x"}'), null);
    assert.equal(parseRelayOverride(42), null);
});

test("remapChannel: rewrites only the namespace segment", () => {
    assert.equal(remapChannel("chess/room", "chess", "chess-2"), "chess-2/room");
    assert.equal(remapChannel("chess/a/b", "chess", "chess-2"), "chess-2/a/b");
    assert.equal(remapChannel("chess", "chess", "chess-2"), "chess-2");
    assert.equal(remapChannel("chessboard/room", "chess", "chess-2"), "chessboard/room");
});

test("remapChannel: leaves protocol channels and identical or missing namespaces alone", () => {
    assert.equal(remapChannel("vesta/relay-manifest", "vesta", "x"), "vesta/relay-manifest");
    assert.equal(remapChannel("chess/room", "chess", "chess"), "chess/room");
    assert.equal(remapChannel("chess/room", null, "x"), "chess/room");
    assert.equal(remapChannel("chess/room", "chess", null), "chess/room");
});

// ── Connection harness ───────────────────────────────────────────────────────

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
        if (this.readyState !== 1) throw new Error("socket not open");
        this.sent.push(JSON.parse(data));
    }
    close(_code, reason) {
        this.readyState = 3;
        for (const l of this._listeners.get("close") ?? []) l({ code: 1000, reason: reason ?? "" });
    }
    open() {
        this.readyState = 1;
        for (const l of this._listeners.get("open") ?? []) l();
    }
    inject(msg) {
        for (const l of this._listeners.get("message") ?? []) l({ data: JSON.stringify(msg) });
    }
}

function welcome(channels) {
    return { type: "WELCOME", serverId: "srv", channels, serverTime: new Date().toISOString() };
}

function makeConnection(override, extra = {}) {
    const store = new InMemoryRelayOverrideStore();
    if (override) store.setOverride(override);
    const directory = new RelayDirectory(CONFIG, store);
    const sockets = [];
    const conn = new VestaConnection({
        relays: override ? [override.relay] : ["wss://home.example/ws"],
        clientId: "test-client",
        channels: ["chess/room"],
        autoReconnect: false,
        relayDirectory: directory,
        createSocket: () => {
            const s = new FakeSocket();
            sockets.push(s);
            return s;
        },
        ...extra,
    });
    conn.on("error", () => {});
    return { conn, sockets, store, directory };
}

// ── Namespace remapping on the wire ──────────────────────────────────────────

test("VestaConnection: an override namespace remaps HELLO, inbound events and PUBLISH (re-signed)", async () => {
    const identity = VestaIdentity.generate();
    const { conn, sockets } = makeConnection(
        { relay: "wss://alt.example/ws", appId: "chess-2" },
        { identity, publicKey: identity.publicKeyB64 },
    );
    conn.connect();
    const socket = sockets[0];
    socket.open();

    assert.deepEqual(socket.sent[0].channels, ["chess-2/room"]);

    socket.inject(welcome(["chess-2/room"]));
    assert.ok(conn.channels.includes("chess/room"));
    assert.ok(!conn.channels.some((c) => c.startsWith("chess-2/")));

    const received = [];
    conn.on("event", (m) => received.push(m));
    socket.inject({
        type: "EVENT",
        channelId: "chess-2/room",
        sequence: 1,
        receivedAt: new Date().toISOString(),
        event: {
            id: "e1",
            channelId: "chess-2/room",
            timestamp: "2026-01-01T00:00:00.000Z",
            clientId: "peer",
            eventType: "t",
            payload: {},
        },
    });
    assert.equal(received[0].channelId, "chess/room");
    assert.equal(received[0].event.channelId, "chess/room");

    conn.publish({
        id: "e2",
        channelId: "chess/room",
        timestamp: "2026-01-01T00:00:00.000Z",
        clientId: "test-client",
        eventType: "t",
        payload: { a: 1 },
        signature: "stale",
    });
    const publish = socket.sent.find((m) => m.type === "PUBLISH");
    assert.equal(publish.channelId, "chess-2/room");
    assert.equal(publish.event.channelId, "chess-2/room");
    assert.notEqual(publish.event.signature, "stale");
    assert.ok(
        ed.verify(base64UrlToBytes(publish.event.signature), buildSigningInput(publish.event), identity.publicKey),
    );

    conn.dispose();
});

test("VestaConnection: the namespace only applies to the overridden relay", async () => {
    const { conn, sockets } = makeConnection({ relay: "wss://alt.example/ws", appId: "chess-2" });
    conn.attachRelayDirectory; // directory already attached
    conn.updateRelayCandidates(["wss://home.example/ws"]);
    conn.connect();
    sockets[0].open();
    assert.deepEqual(sockets[0].sent[0].channels, ["chess/room"]);
    conn.dispose();
});

// ── Register on connect ──────────────────────────────────────────────────────

test("VestaConnection: registerApp greets with no channels, registers, then subscribes", async () => {
    const { conn, sockets } = makeConnection({ relay: "wss://alt.example/ws", appId: "chess-2", registerApp: true });
    conn.connect();
    const socket = sockets[0];
    socket.open();

    assert.equal(socket.sent[0].type, "HELLO");
    assert.deepEqual(socket.sent[0].channels, []);

    socket.inject(welcome([]));
    await tick();
    const register = socket.sent.find((m) => m.type === "REGISTER_APP");
    assert.equal(register.appId, "chess-2");
    assert.ok(!socket.sent.some((m) => m.type === "SUBSCRIBE"));

    socket.inject({ type: "ACK", eventId: NIL, channelId: "chess-2", sequence: 0 });
    await tick();
    const subscribe = socket.sent.find((m) => m.type === "SUBSCRIBE" && m.channelId === "chess-2/room");
    assert.ok(subscribe);
    conn.dispose();
});

test("VestaConnection: DUPLICATE_APP during registration counts as registered", async () => {
    const { conn, sockets } = makeConnection({ relay: "wss://alt.example/ws", appId: "chess-2", registerApp: true });
    conn.connect();
    const socket = sockets[0];
    socket.open();
    socket.inject(welcome([]));
    await tick();

    socket.inject({ type: "ERROR", code: "DUPLICATE_APP", message: "exists" });
    await tick();
    assert.ok(socket.sent.some((m) => m.type === "SUBSCRIBE" && m.channelId === "chess-2/room"));
    conn.dispose();
});

test("VestaConnection: a failed registration never subscribes and surfaces an error", async () => {
    const { conn, sockets } = makeConnection({ relay: "wss://alt.example/ws", appId: "chess-2", registerApp: true });
    const errors = [];
    conn.on("error", (e) => errors.push(e));
    conn.connect();
    const socket = sockets[0];
    socket.open();
    socket.inject(welcome([]));
    await tick();

    socket.inject({ type: "ERROR", code: "APP_NOT_ALLOWED", message: "closed" });
    await tick();
    assert.ok(!socket.sent.some((m) => m.type === "SUBSCRIBE"));
    assert.equal(errors[0].code, "REGISTER_APP_FAILED");
    conn.dispose();
});

// ── Probationary adoption ────────────────────────────────────────────────────

test("VestaConnection.adoptRelay: commits the override once the relay confirms the namespace", async () => {
    const { conn, sockets, store } = makeConnection(null);
    const adopting = conn.adoptRelay({ relay: "wss://alt.example/ws", appId: "chess-2" });
    const socket = sockets[0];
    socket.open();
    socket.inject(welcome(["chess-2/room"]));

    const result = await adopting;
    assert.deepEqual(result, { connected: true, failureReason: null });
    assert.equal(store.getOverride().relay, "wss://alt.example/ws");
    assert.equal(store.getOverride().appId, "chess-2");
    conn.dispose();
});

test("VestaConnection.adoptRelay: a namespace error during probation discards the override", async () => {
    const { conn, sockets, store, directory } = makeConnection(null);
    const adopting = conn.adoptRelay({ relay: "wss://alt.example/ws", appId: "chess-2" });
    const socket = sockets[0];
    socket.open();
    socket.inject(welcome([]));
    socket.inject({ type: "ERROR", code: "APP_NOT_ALLOWED", message: "unregistered apps refused" });

    // The discard triggers a reconnect on the original candidates; fail that attempt.
    for (let i = 0; i < 20 && sockets.length < 2; i++) await tick();
    sockets[1]?.close();

    const result = await adopting;
    assert.equal(result.connected, false);
    assert.match(result.failureReason, /APP_NOT_ALLOWED/);
    assert.equal(store.getOverride(), null);
    assert.equal(directory.activeOverride, null);
    conn.dispose();
});

test("VestaConnection.adoptRelay: an unreachable relay is discarded with the connection reason", async () => {
    const { conn, sockets, store } = makeConnection(null);
    const adopting = conn.adoptRelay({ relay: "wss://alt.example/ws", appId: "chess-2" });
    sockets[0].close();

    const result = await adopting;
    assert.equal(result.connected, false);
    assert.ok(result.failureReason);
    assert.equal(store.getOverride(), null);
    conn.dispose();
});
