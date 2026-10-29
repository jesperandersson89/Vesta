// Shared #33 phase-B vectors; identical to ChannelCryptoTests.cs and test_channel_crypto.py.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";

import {
    ChannelCryptoError,
    ChannelKeyPair,
    decryptPayload,
    encryptPayload,
    generateChannelKey,
    isEnvelope,
    openKey,
    readEpoch,
    sealKey,
    VestaIdentity,
} from "../dist/index.js";
import { base64UrlToBytes, bytesToBase64Url } from "../dist/signing.js";

const range = (start, count) => Uint8Array.from({ length: count }, (_, i) => start + i);
const SEED_A = range(0, 32);
const SEED_B = range(32, 32);
const X25519_PRIV_A = "bWmf93nEz6Ln3iFyUFjrQzEo5HTi4hcLCL4rNXcQgSY";
const X25519_PUB_A = "FOghQ_QoWARIegjdmUQMvxDlo52xNSuxywtywgqy-BU";
const X25519_PRIV_B = "0lw_0gknxoNrVg9GMMaRk4nNGGtsc0at7L-sn1cUXI4";
const X25519_PUB_B = "N1z9KAjpCNOuEulV1jAnYyChQxyuXMaA5ILsIluV7ms";
const CLIENT_ID_B = "JPbtasv-EAnAMNfKVnwzyk";

const KEY = { epoch: 1, key: new Uint8Array(32).fill(0x42) };
const CHANNEL_ID = "notes/secret";
const EVENT_ID = "0190f7e2-0000-7000-8000-000000000001";
const EVENT_TYPE = "app.note";
const NONCE = range(0, 12);
const EXPECTED_CT = "fvz__4sd3inpYzxn8-d5U1fxogWCNBPNbm8s9i1AGezwveYgVlM";

const EPH_PRIV = new Uint8Array(32).fill(0x07);
const EPH_PUB = "E75P6uryBMf9M1j8nAByGIHRdCeBKCJ-xnTzf3_pe20";
const SEAL_NONCE = range(16, 12);
const SEALED_CT = "9unGHrePRVJRMfa0kInth8NBHJHdikR2Bs9so3BpuVmbUrQja7pEObBBpmmLyB96";

test("fromSeed derives the shared X25519 vectors", () => {
    const a = ChannelKeyPair.fromSeed(SEED_A);
    const b = ChannelKeyPair.fromSeed(SEED_B);
    assert.equal(bytesToBase64Url(a.exportPrivateKey()), X25519_PRIV_A);
    assert.equal(bytesToBase64Url(a.publicKey), X25519_PUB_A);
    assert.equal(bytesToBase64Url(b.exportPrivateKey()), X25519_PRIV_B);
    assert.equal(bytesToBase64Url(b.publicKey), X25519_PUB_B);
});

test("fromIdentity matches fromSeed and the clientId vector", () => {
    const identity = VestaIdentity.fromPrivateKey(SEED_B);
    assert.equal(identity.clientId, CLIENT_ID_B);
    assert.equal(bytesToBase64Url(ChannelKeyPair.fromIdentity(identity).publicKey), X25519_PUB_B);
});

test("encryptPayload with a fixed nonce matches the vector and canonicalises the JSON", async () => {
    const envelope = await encryptPayload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, { text: "hello", n: 1 }, NONCE);
    assert.equal(isEnvelope(envelope), true);
    assert.deepEqual(envelope, { enc: 1, kid: 1, n: "AAECAwQFBgcICQoL", ct: EXPECTED_CT });
});

test("decryptPayload round-trips and rejects any AAD change", async () => {
    const envelope = await encryptPayload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, { text: "hello", n: 1 }, NONCE);
    assert.deepEqual(await decryptPayload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, envelope), { n: 1, text: "hello" });

    const cases = [
        [KEY, "notes/other", EVENT_ID, EVENT_TYPE],
        [KEY, CHANNEL_ID, "0190f7e2-0000-7000-8000-000000000002", EVENT_TYPE],
        [KEY, CHANNEL_ID, EVENT_ID, "app.other"],
        [{ epoch: 1, key: new Uint8Array(32) }, CHANNEL_ID, EVENT_ID, EVENT_TYPE],
        [{ ...KEY, epoch: 2 }, CHANNEL_ID, EVENT_ID, EVENT_TYPE],
    ];
    for (const args of cases) {
        await assert.rejects(decryptPayload(...args, envelope), ChannelCryptoError);
    }
});

test("event id case is normalised in the AAD", async () => {
    const envelope = await encryptPayload(KEY, CHANNEL_ID, EVENT_ID.toUpperCase(), EVENT_TYPE, { a: 1 }, NONCE);
    assert.deepEqual(await decryptPayload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, envelope), { a: 1 });
});

test("random-nonce envelopes differ and both decrypt", async () => {
    const first = await encryptPayload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, { x: true });
    const second = await encryptPayload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, { x: true });
    assert.notEqual(first.n, second.n);
    assert.deepEqual(await decryptPayload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, first), { x: true });
    assert.deepEqual(await decryptPayload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, second), { x: true });
});

test("scalar and array payloads round-trip", async () => {
    for (const payload of [1, "text", true, null, [3, 1, 2]]) {
        const envelope = await encryptPayload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, payload);
        assert.deepEqual(await decryptPayload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, envelope), payload);
    }
});

test("readEpoch and isEnvelope", async () => {
    const envelope = await encryptPayload({ ...KEY, epoch: 7 }, CHANNEL_ID, EVENT_ID, EVENT_TYPE, { a: 1 });
    assert.equal(readEpoch(envelope), 7);
    assert.equal(isEnvelope({ text: "hi" }), false);
    assert.equal(isEnvelope({ ...envelope, extra: 1 }), false);
    assert.equal(isEnvelope({ ...envelope, enc: "1" }), false);
    assert.throws(() => readEpoch({ text: "hi" }), ChannelCryptoError);
});

test("sealKey with a fixed ephemeral key and nonce matches the vector", async () => {
    const ephemeral = ChannelKeyPair.fromPrivateKey(EPH_PRIV);
    const sealed = await sealKey(KEY, CHANNEL_ID, CLIENT_ID_B, base64UrlToBytes(X25519_PUB_B), ephemeral, SEAL_NONCE);
    assert.deepEqual(sealed, { epoch: 1, epk: EPH_PUB, n: "EBESExQVFhcYGRob", ct: SEALED_CT });
});

test("openKey recovers the channel key only for the recipient", async () => {
    const sealed = { epoch: 1, epk: EPH_PUB, n: "EBESExQVFhcYGRob", ct: SEALED_CT };
    const a = ChannelKeyPair.fromSeed(SEED_A);
    const b = ChannelKeyPair.fromSeed(SEED_B);

    const opened = await openKey(sealed, CHANNEL_ID, CLIENT_ID_B, b);
    assert.equal(opened.epoch, 1);
    assert.deepEqual(opened.key, KEY.key);

    await assert.rejects(openKey(sealed, CHANNEL_ID, CLIENT_ID_B, a), ChannelCryptoError);
    await assert.rejects(openKey(sealed, "notes/other", CLIENT_ID_B, b), ChannelCryptoError);
    await assert.rejects(openKey(sealed, CHANNEL_ID, "someone-else", b), ChannelCryptoError);
    await assert.rejects(openKey({ ...sealed, epoch: 2 }, CHANNEL_ID, CLIENT_ID_B, b), ChannelCryptoError);
});

test("sealKey with a random ephemeral key round-trips through JSON", async () => {
    const recipient = ChannelKeyPair.generate();
    const key = generateChannelKey(3);
    const sealed = JSON.parse(JSON.stringify(await sealKey(key, CHANNEL_ID, "rcpt", recipient.publicKey)));
    const opened = await openKey(sealed, CHANNEL_ID, "rcpt", recipient);
    assert.equal(opened.epoch, 3);
    assert.deepEqual(opened.key, key.key);
});
