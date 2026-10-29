// Encrypted channel glue (phase C): admin creates, member requests keys and reads history + live events,
// rotation after removal locks the member out. Runs through a tiny in-process fake relay that sequences
// PUBLISH into EVENT for every connected socket. Mirrors the C# EncryptedChannelFlowTests.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";

import {
    EncryptedChannel,
    InMemoryChannelKeyStore,
    isEnvelope,
    tryParseKeyGrant,
    tryParseKeyRequest,
    VestaConnection,
    VestaIdentity,
} from "../dist/index.js";

const NIL = "00000000-0000-0000-0000-000000000000";

class FakeRelay {
    constructor() {
        this.sockets = new Set();
        this.sequences = new Map();
        this.stored = new Map(); // channelId -> SequencedEvent[]
        this.members = new Map(); // channelId -> Set<clientId>  (null = public)
        this.encrypted = new Set();
    }
    socket(clientId) {
        const relay = this;
        const sock = {
            readyState: 0,
            clientId,
            subscriptions: new Set(),
            _listeners: new Map(),
            addEventListener(event, l) {
                if (!this._listeners.has(event)) this._listeners.set(event, new Set());
                this._listeners.get(event).add(l);
            },
            removeEventListener(event, l) {
                this._listeners.get(event)?.delete(l);
            },
            send(data) {
                relay.handle(this, JSON.parse(data));
            },
            close() {
                this.readyState = 3;
                relay.sockets.delete(this);
            },
            open() {
                this.readyState = 1;
                for (const l of this._listeners.get("open") ?? []) l();
            },
            inject(msg) {
                for (const l of this._listeners.get("message") ?? []) l({ data: JSON.stringify(msg) });
            },
        };
        this.sockets.add(sock);
        return sock;
    }
    handle(sock, msg) {
        switch (msg.type) {
            case "HELLO":
                sock.inject({ type: "WELCOME", serverId: "fake", channels: [], features: ["encrypted-channels"] });
                break;
            case "CREATE_CHANNEL":
                if (this.members.has(msg.channelId)) {
                    sock.inject({ type: "ERROR", code: "CHANNEL_EXISTS", message: "exists", channelId: msg.channelId });
                    return;
                }
                this.members.set(msg.channelId, new Set([sock.clientId, ...msg.initialMembers]));
                if (msg.encrypted) this.encrypted.add(msg.channelId);
                sock.subscriptions.add(msg.channelId);
                sock.inject({ type: "ACK", channelId: msg.channelId, eventId: NIL, sequence: 0 });
                break;
            case "GRANT_ACCESS":
                this.members.get(msg.channelId)?.add(msg.clientId);
                sock.inject({ type: "ACK", channelId: msg.channelId, eventId: NIL, sequence: 0 });
                break;
            case "REVOKE_ACCESS":
                this.members.get(msg.channelId)?.delete(msg.clientId);
                sock.inject({ type: "ACK", channelId: msg.channelId, eventId: NIL, sequence: 0 });
                break;
            case "SUBSCRIBE": {
                const members = this.members.get(msg.channelId);
                if (members && !members.has(sock.clientId)) {
                    sock.inject({ type: "ERROR", code: "ACCESS_DENIED", message: "nope", channelId: msg.channelId });
                    return;
                }
                sock.subscriptions.add(msg.channelId);
                const history = (this.stored.get(msg.channelId) ?? []).filter((e) => e.sequence >= (msg.fromSequence ?? 0));
                if (history.length) sock.inject({ type: "EVENTS_BATCH", channelId: msg.channelId, events: history });
                break;
            }
            case "PUBLISH": {
                const { channelId, event } = msg;
                const members = this.members.get(channelId);
                if (members && !members.has(sock.clientId)) {
                    sock.inject({ type: "ERROR", code: "ACCESS_DENIED", message: "nope", eventId: event.id, channelId });
                    return;
                }
                if (this.encrypted.has(channelId) && !event.eventType.startsWith("vesta.") && !isEnvelope(event.payload)) {
                    sock.inject({ type: "ERROR", code: "ENCRYPTION_REQUIRED", message: "plaintext", eventId: event.id, channelId });
                    return;
                }
                const sequence = (this.sequences.get(channelId) ?? 0) + 1;
                this.sequences.set(channelId, sequence);
                const receivedAt = new Date().toISOString();
                const stored = this.stored.get(channelId) ?? [];
                stored.push({ event, sequence, receivedAt });
                this.stored.set(channelId, stored);
                sock.inject({ type: "ACK", channelId, eventId: event.id, sequence });
                for (const s of this.sockets) {
                    if (s.subscriptions.has(channelId)) s.inject({ type: "EVENT", channelId, event, sequence, receivedAt });
                }
                break;
            }
        }
    }
}

function connect(relay, identity) {
    let socket;
    const conn = new VestaConnection({
        serverUrl: "ws://fake",
        identity,
        channels: [],
        autoReconnect: false,
        createSocket: () => (socket = relay.socket(identity.clientId)),
    });
    conn.connect();
    socket.open();
    return conn;
}

const tick = (ms = 30) => new Promise((r) => setTimeout(r, ms));
async function until(condition, timeoutMs = 5000) {
    const deadline = Date.now() + timeoutMs;
    while (!condition()) {
        if (Date.now() > deadline) throw new Error("condition not met in time");
        await tick(10);
    }
}

test("admin creates, member joins and reads history + live, removal + rotation locks the member out", async () => {
    const relay = new FakeRelay();
    const adminId = VestaIdentity.generate();
    const memberId = VestaIdentity.generate();
    const admin = connect(relay, adminId);
    const member = connect(relay, memberId);
    const channelId = "notes/room";

    const adminChannel = await admin.createEncryptedChannel(channelId, { members: [memberId.clientId] });
    assert.equal(adminChannel.isAdmin, true);
    assert.equal(adminChannel.currentEpoch, 1);

    const adminSeen = [];
    adminChannel.on("event", (e) => adminSeen.push(e));
    await adminChannel.publish("app.note", { text: "first" });

    // The relay only ever stored an envelope.
    assert.equal(isEnvelope(relay.stored.get(channelId)[0].event.payload), true);

    const memberSeen = [];
    const memberChannel = await member.openEncryptedChannel(channelId, { adminPublicKeys: [adminId.publicKey] });
    memberChannel.on("event", (e) => memberSeen.push(e));
    await memberChannel.waitForKey();
    assert.equal(memberChannel.currentEpoch, 1);

    await until(() => memberSeen.length >= 1);
    assert.deepEqual(memberSeen[0].event.event.payload, { text: "first" });
    assert.equal(isEnvelope(memberSeen[0].sealed.event.payload), true);

    await memberChannel.publish("app.note", { text: "from member" });
    await until(() => adminSeen.some((e) => e.event.event.payload.text === "from member"));

    // Only one grant was published for the member (history request answered once).
    const grants = relay.stored.get(channelId).filter((e) => e.event.eventType === "vesta.channel-key");
    assert.equal(grants.length, 1);
    assert.equal(tryParseKeyGrant(grants[0].event, [adminId.publicKey]).recipientClientId, memberId.clientId);

    await adminChannel.removeMember(memberId.clientId);
    assert.equal(adminChannel.currentEpoch, 2);
    await adminChannel.publish("app.note", { text: "after rotation" });
    await tick(100);
    assert.equal(memberSeen.some((e) => e.event.event.payload.text === "after rotation"), false);
    assert.equal(memberChannel.currentEpoch, 1);

    const errors = [];
    member.on("error", (e) => errors.push(e));
    await memberChannel.publish("app.note", { text: "still here?" });
    await until(() => errors.some((e) => e.code === "ACCESS_DENIED"));

    memberChannel.close();
    adminChannel.close();
});

test("a member opening later receives every epoch and reads old history in order", async () => {
    const relay = new FakeRelay();
    const adminId = VestaIdentity.generate();
    const memberId = VestaIdentity.generate();
    const admin = connect(relay, adminId);
    const channelId = "notes/late";

    const adminChannel = await admin.createEncryptedChannel(channelId);
    await adminChannel.publish("app.note", { n: 1 });
    await adminChannel.rotateKey();
    await adminChannel.publish("app.note", { n: 2 });
    admin.grantAccess(channelId, memberId.clientId);

    const member = connect(relay, memberId);
    const seen = [];
    const memberChannel = await member.openEncryptedChannel(channelId, { adminPublicKeys: [adminId.publicKey] });
    memberChannel.on("event", (e) => seen.push(e.event.event.payload.n));
    await memberChannel.waitForKey();
    await until(() => seen.length === 2);
    assert.deepEqual(seen, [1, 2]);
    assert.equal(memberChannel.currentEpoch, 2);
});

test("key grants from an untrusted signer are ignored; an admin re-opening does not re-answer old requests", async () => {
    const relay = new FakeRelay();
    const adminId = VestaIdentity.generate();
    const impostorId = VestaIdentity.generate();
    const memberId = VestaIdentity.generate();
    const admin = connect(relay, adminId);
    const member = connect(relay, memberId);
    const channelId = "notes/trust";
    const store = new InMemoryChannelKeyStore();

    const adminChannel = await admin.createEncryptedChannel(channelId, { members: [memberId.clientId, impostorId.clientId], keyStore: store });
    const memberChannel = await member.openEncryptedChannel(channelId, { adminPublicKeys: [adminId.publicKey] });
    await memberChannel.waitForKey();

    // An impostor member publishes a well-formed grant: the member must not accept it as epoch 9.
    const impostor = connect(relay, impostorId);
    const impostorChannel = new EncryptedChannel(impostor, impostorId, channelId, { isAdmin: true, keyStore: new InMemoryChannelKeyStore() });
    await impostorChannel.createInitialKey();
    await impostorChannel.initialize();
    await tick(50);
    assert.equal(memberChannel.currentEpoch, 1);

    // Admin re-opens with the same store: the member's historical request is already granted → no new grant.
    adminChannel.close();
    const reopened = await admin.openEncryptedChannel(channelId, { isAdmin: true, keyStore: store });
    await tick(100);
    const grants = relay.stored.get(channelId).filter((e) => e.event.eventType === "vesta.channel-key" && e.event.clientId === adminId.clientId);
    assert.equal(grants.length, 1);
    assert.equal(reopened.currentEpoch, 1);
    assert.ok(tryParseKeyRequest(relay.stored.get(channelId).find((e) => e.event.eventType === "vesta.channel-key-request").event));
});

test("createEncryptedChannel rejects when the relay refuses", async () => {
    const relay = new FakeRelay();
    const adminId = VestaIdentity.generate();
    const admin = connect(relay, adminId);
    await admin.createEncryptedChannel("notes/dup");
    await assert.rejects(admin.createEncryptedChannel("notes/dup"), /CHANNEL_EXISTS/);
});
