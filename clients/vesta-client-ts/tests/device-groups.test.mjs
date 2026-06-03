// Mirrors VestaCore.Tests.Identity.DeviceGroupProjectionTests / tests/test_device_groups.py.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";

import {
    DeviceGroupProjection,
    PairingPayload,
    VestaIdentity,
    buildAnnounce,
    buildLink,
    buildUnlink,
    deviceGroupChannel,
    generateGroupId,
    isProtocolChannel,
} from "../dist/index.js";

// ── generateGroupId / channel helpers ────────────────────────────────────────

test("generateGroupId returns 32 lowercase hex chars", () => {
    const gid = generateGroupId();
    assert.equal(gid.length, 32);
    assert.match(gid, /^[a-f0-9]{32}$/);
});

test("deviceGroupChannel uses reserved prefix", () => {
    const ch = deviceGroupChannel("abc123");
    assert.equal(ch, "vesta/identity/abc123");
    assert.ok(isProtocolChannel(ch));
});

// ── DeviceGroupProjection trust rules ────────────────────────────────────────

test("founder announce becomes trusted member", () => {
    const founder = VestaIdentity.generate();
    const gid = generateGroupId();
    const projection = new DeviceGroupProjection(gid);

    projection.applyLocal(buildAnnounce(founder, gid));

    const state = projection.state;
    assert.equal(Object.keys(state.members).length, 1);
    assert.ok(projection.isMember(founder.clientId));
});

test("link from trusted member adds target", () => {
    const founder = VestaIdentity.generate();
    const newDevice = VestaIdentity.generate();
    const gid = generateGroupId();
    const projection = new DeviceGroupProjection(gid);

    projection.applyLocal(buildAnnounce(founder, gid));
    projection.applyLocal(buildLink(founder, gid, newDevice.publicKey));

    assert.equal(Object.keys(projection.state.members).length, 2);
    assert.ok(projection.isMember(founder.clientId));
    assert.ok(projection.isMember(newDevice.clientId));
});

test("link from untrusted device is ignored", () => {
    const founder = VestaIdentity.generate();
    const outsider = VestaIdentity.generate();
    const newDevice = VestaIdentity.generate();
    const gid = generateGroupId();
    const projection = new DeviceGroupProjection(gid);

    projection.applyLocal(buildAnnounce(founder, gid));
    projection.applyLocal(buildLink(outsider, gid, newDevice.publicKey));

    assert.equal(Object.keys(projection.state.members).length, 1);
    assert.equal(projection.isMember(newDevice.clientId), false);
    assert.equal(projection.isMember(outsider.clientId), false);
});

test("transitive link propagates trust", () => {
    const founder = VestaIdentity.generate();
    const deviceB = VestaIdentity.generate();
    const deviceC = VestaIdentity.generate();
    const gid = generateGroupId();
    const projection = new DeviceGroupProjection(gid);

    projection.applyLocal(buildAnnounce(founder, gid));
    projection.applyLocal(buildLink(founder, gid, deviceB.publicKey));
    projection.applyLocal(buildLink(deviceB, gid, deviceC.publicKey));

    assert.equal(Object.keys(projection.state.members).length, 3);
    assert.ok(projection.isMember(deviceC.clientId));
});

test("unlink removes target", () => {
    const founder = VestaIdentity.generate();
    const deviceB = VestaIdentity.generate();
    const gid = generateGroupId();
    const projection = new DeviceGroupProjection(gid);

    projection.applyLocal(buildAnnounce(founder, gid));
    projection.applyLocal(buildLink(founder, gid, deviceB.publicKey));
    assert.ok(projection.isMember(deviceB.clientId));

    projection.applyLocal(buildUnlink(founder, gid, deviceB.publicKey));
    assert.equal(projection.isMember(deviceB.clientId), false);
});

// ── PairingPayload round-trip ────────────────────────────────────────────────

test("PairingPayload round-trips through base64", () => {
    const identity = VestaIdentity.generate();
    const original = {
        groupId: "abc123",
        publicKey: identity.publicKeyB64,
        serverUrl: "wss://vesta.example/ws",
    };

    const encoded = PairingPayload.toBase64(original);
    const decoded = PairingPayload.fromBase64(encoded);

    assert.equal(decoded.groupId, original.groupId);
    assert.equal(decoded.publicKey, original.publicKey);
    assert.equal(decoded.serverUrl, original.serverUrl);
});

test("PairingPayload round-trips without serverUrl", () => {
    const encoded = PairingPayload.toBase64({ groupId: "g", publicKey: "pk" });
    const decoded = PairingPayload.fromBase64(encoded);
    assert.equal(decoded.groupId, "g");
    assert.equal(decoded.publicKey, "pk");
    assert.equal(decoded.serverUrl, undefined);
});
