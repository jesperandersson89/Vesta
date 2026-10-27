// restoreIdentity / recoverIdentity: one-call identity recovery from a relay-stored backup.
// Mirrors tests/VestaClient.Tests/RecoverIdentityTests.cs and clients/vesta-client-py/tests/test_recover_identity.py.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";

import { RelayBackup, VestaConnection, VestaIdentity } from "../dist/index.js";

const FAST = { memoryKiB: 256, iterations: 1 };

class FakeRelaySocket {
    constructor(stored, { openAfter = true, failOpen = false } = {}) {
        this.readyState = 0;
        this.fetches = [];
        this._listeners = new Map();
        this._stored = stored;
        if (failOpen) {
            setImmediate(() => {
                this.readyState = 3;
                for (const l of this._listeners.get("close") ?? []) l({ code: 1006, reason: "unreachable" });
            });
        } else if (openAfter) {
            setImmediate(() => {
                this.readyState = 1;
                for (const l of this._listeners.get("open") ?? []) l();
            });
        }
    }
    addEventListener(event, listener) {
        if (!this._listeners.has(event)) this._listeners.set(event, new Set());
        this._listeners.get(event).add(listener);
    }
    removeEventListener(event, listener) {
        this._listeners.get(event)?.delete(listener);
    }
    send(data) {
        const msg = JSON.parse(data);
        if (msg.type === "HELLO") {
            setImmediate(() => this.inject({ type: "WELCOME", serverId: "scripted", channels: [] }));
        } else if (msg.type === "FETCH") {
            this.fetches.push(msg);
            setImmediate(() => {
                const events = this._stored
                    .filter((e) => e.event.channelId === msg.channelId && e.sequence >= msg.fromSequence)
                    .slice(0, 1);
                this.inject({ type: "EVENTS_BATCH", channelId: msg.channelId, events });
            });
        }
    }
    close() {
        this.readyState = 3;
    }
    inject(msg) {
        for (const l of this._listeners.get("message") ?? []) l({ data: JSON.stringify(msg) });
    }
}

async function relayHoldingBackup(identity, username, password) {
    const backup = await RelayBackup.derive("game", username, password, FAST);
    return [{ event: await backup.createEvent(identity), sequence: 1, receivedAt: "2026-01-01T00:00:00.000Z" }];
}

function recover(stored, username, password, extra = {}) {
    let socket;
    const promise = VestaConnection.recoverIdentity({
        username,
        password,
        appId: "game",
        backupOptions: FAST,
        connection: {
            serverUrl: "ws://scripted",
            createSocket: () => (socket = new FakeRelaySocket(stored, extra.socket)),
        },
        ...extra.args,
    });
    return { promise, socket: () => socket };
}

test("recoverIdentity returns the original identity for the right login", async () => {
    const original = VestaIdentity.generate();
    const stored = await relayHoldingBackup(original, "alice", "hunter2");

    const { promise } = recover(stored, "alice", "hunter2");

    const recovered = await promise;
    assert.ok(recovered);
    assert.equal(recovered.clientId, original.clientId);
});

test("recoverIdentity normalises the username spelling", async () => {
    const original = VestaIdentity.generate();
    const stored = await relayHoldingBackup(original, "alice", "hunter2");

    const recovered = await recover(stored, "  ALICE ", "hunter2").promise;

    assert.equal(recovered?.clientId, original.clientId);
});

test("recoverIdentity resolves null for a wrong password or unknown username", async () => {
    const stored = await relayHoldingBackup(VestaIdentity.generate(), "alice", "hunter2");

    assert.equal(await recover(stored, "alice", "wrong").promise, null);
    assert.equal(await recover(stored, "bob", "hunter2").promise, null);
});

test("recoverIdentity only fetches the derived backup channel", async () => {
    const stored = await relayHoldingBackup(VestaIdentity.generate(), "alice", "hunter2");
    const { promise, socket } = recover(stored, "alice", "hunter2");
    await promise;

    const channel = (await RelayBackup.derive("game", "alice", "hunter2", FAST)).channelId;
    assert.ok(socket().fetches.length > 0);
    assert.ok(socket().fetches.every((f) => f.channelId === channel));
});

test("recoverIdentity rejects when the relay cannot be reached", async () => {
    const stored = await relayHoldingBackup(VestaIdentity.generate(), "alice", "hunter2");

    await assert.rejects(
        recover(stored, "alice", "hunter2", { socket: { failOpen: true } }).promise,
        /Could not connect to the relay/,
    );
});

test("recoverIdentity needs an appId or an appConfig", async () => {
    await assert.rejects(
        VestaConnection.recoverIdentity({ username: "a", password: "b", connection: { serverUrl: "ws://x" } }),
        /appId/,
    );
});
