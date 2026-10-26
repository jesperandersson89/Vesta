// HELLO key-possession proof: CHALLENGE -> PROVE handshake and the signing vector shared with the
// C# (HelloProofTests) and Python SDKs. Run after `npm run build`.

import { test } from "node:test";
import assert from "node:assert/strict";
import * as ed from "@noble/ed25519";

import {
    base64UrlToBytes,
    setRelayPickerEnabled,
    signHelloProof,
    VestaConnection,
    VestaIdentity,
} from "../dist/index.js";

setRelayPickerEnabled(false);

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
    inject(msg) {
        for (const l of this._listeners.get("message") ?? []) l({ data: JSON.stringify(msg) });
    }
}

function connect(identity) {
    const sockets = [];
    const conn = new VestaConnection({
        relays: ["wss://relay.example/ws"],
        clientId: identity?.clientId ?? "anon-client",
        channels: ["game/farm"],
        autoReconnect: false,
        identity,
        publicKey: identity?.publicKeyB64,
        createSocket: () => {
            const s = new FakeSocket();
            sockets.push(s);
            return s;
        },
    });
    conn.on("error", () => {});
    conn.connect();
    sockets[0].open();
    return { conn, socket: sockets[0] };
}

test("signHelloProof: matches the cross-language vector", () => {
    const identity = VestaIdentity.fromPrivateKey(new Uint8Array(32));
    assert.equal(identity.clientId, "E545QOZLVJFyIIjZoNdBYo");
    assert.equal(
        signHelloProof("bm9uY2Utdm9uY2Utdm9uY2U", identity.clientId, identity.privateKey),
        "9hGCvE4icwU-17knAeGGHxw0xvyqpm38vbgrC0z8ZREQWCtRk9UoPGNP_o0bp0-ABGhIXJAY5rrDsblaYih_DQ",
    );
});

test("HELLO from an identity advertises proofSupported and a CHALLENGE is answered with a valid PROVE", () => {
    const identity = VestaIdentity.generate();
    const { socket } = connect(identity);

    assert.equal(socket.sent[0].type, "HELLO");
    assert.equal(socket.sent[0].proofSupported, true);

    socket.inject({ type: "CHALLENGE", nonce: "abc123" });

    const prove = socket.sent[1];
    assert.equal(prove.type, "PROVE");
    const message = new TextEncoder().encode(`vesta-hello-v1\nabc123\n${identity.clientId}`);
    assert.ok(ed.verify(base64UrlToBytes(prove.signature), message, identity.publicKey));
});

test("HELLO without an identity does not advertise proofSupported and ignores a CHALLENGE", () => {
    const { socket } = connect(undefined);

    assert.equal(socket.sent[0].proofSupported, undefined);

    socket.inject({ type: "CHALLENGE", nonce: "abc123" });

    assert.equal(socket.sent.length, 1);
});
