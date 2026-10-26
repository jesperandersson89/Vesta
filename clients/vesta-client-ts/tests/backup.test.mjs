// Identity backup, recovery phrase and relay-stored backup. Mirrors tests/VestaCore.Tests/IdentityBackupTests.cs
// and shares its vectors. Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";

import {
    IdentityBackupError,
    RecoveryPhraseError,
    RelayBackup,
    recoveryPhraseFromSeed,
    seedFromRecoveryPhrase,
    VestaIdentity,
    verifyEvent,
} from "../dist/index.js";

const FAST = { memoryKiB: 256, iterations: 1 };
const repeat = (word, count) => Array(count).fill(word).join(" ");

// ── Recovery phrase ─────────────────────────────────────────────────────────

test("recovery phrase: matches the BIP-39 vectors", () => {
    assert.equal(recoveryPhraseFromSeed(new Uint8Array(32)), repeat("abandon", 23) + " art");
    assert.equal(recoveryPhraseFromSeed(new Uint8Array(32).fill(0xff)), repeat("zoo", 23) + " vote");
});

test("recovery phrase: round-trips, ignores case and whitespace, rejects bad input", () => {
    const identity = VestaIdentity.generate();
    const phrase = identity.toRecoveryPhrase();

    assert.equal(phrase.split(" ").length, 24);
    assert.equal(VestaIdentity.fromRecoveryPhrase(phrase).clientId, identity.clientId);
    assert.deepEqual(seedFromRecoveryPhrase("  " + phrase.toUpperCase().replaceAll(" ", "\n ")), identity.privateKey);
    assert.throws(() => seedFromRecoveryPhrase(repeat("abandon", 12)), RecoveryPhraseError);
    assert.throws(() => seedFromRecoveryPhrase(repeat("abandon", 23) + " notaword"), RecoveryPhraseError);
    assert.throws(() => seedFromRecoveryPhrase(repeat("abandon", 24)), RecoveryPhraseError);
});

// ── Encrypted export ────────────────────────────────────────────────────────

test("exportEncrypted: round-trips and fails on a wrong password or a tampered blob", async () => {
    const identity = VestaIdentity.generate();
    const other = VestaIdentity.generate();
    const blob = await identity.exportEncrypted("right", FAST);

    const restored = await VestaIdentity.importEncrypted(blob, "right");
    assert.equal(restored.clientId, identity.clientId);
    assert.deepEqual(restored.privateKey, identity.privateKey);
    await assert.rejects(VestaIdentity.importEncrypted(blob, "wrong"), IdentityBackupError);
    await assert.rejects(VestaIdentity.importEncrypted(blob.replace(identity.clientId, other.clientId), "right"), IdentityBackupError);
    for (const bad of ["not json", "{}", JSON.stringify({ format: "x", version: 1 })]) {
        await assert.rejects(VestaIdentity.importEncrypted(bad, "right"), IdentityBackupError);
    }
});

test("exportEncrypted: unicode password forms agree and abusive KDF settings are refused", async () => {
    const identity = VestaIdentity.generate();
    const restored = await VestaIdentity.importEncrypted(await identity.exportEncrypted("caf\u00e9", FAST), "cafe\u0301");
    assert.equal(restored.clientId, identity.clientId);

    const abusive = JSON.parse(await identity.exportEncrypted("pw", FAST));
    abusive.kdf.memoryKiB = 4_000_000;
    await assert.rejects(VestaIdentity.importEncrypted(JSON.stringify(abusive), "pw"), IdentityBackupError);
    await assert.rejects(identity.exportEncrypted("pw", { memoryKiB: 1, iterations: 1 }), IdentityBackupError);
});

test("importEncrypted: opens a blob made by the Python SDK (cross-language vector)", async () => {
    const blob =
        '{"format":"vesta-identity-backup","version":1,"clientId":"E545QOZLVJFyIIjZoNdBYo","kdf":{"name":"argon2id","memoryKiB":256,"iterations":1,"parallelism":1,"salt":"fLzab7CqHEJ0INUixMow7Q"},"cipher":{"name":"aes-256-gcm","nonce":"wy1fc8r883p6G1jK"},"ciphertext":"ZodfWVd8VFyzpk827tteUJIGiultKBZBoPzSYyD0un4MRmLQSiWSxHQBfrIGtaIa"}';

    const restored = await VestaIdentity.importEncrypted(blob, "correct horse battery staple");

    assert.equal(restored.clientId, "E545QOZLVJFyIIjZoNdBYo");
    assert.deepEqual(restored.privateKey, new Uint8Array(32));
});

// ── Relay-stored backup ─────────────────────────────────────────────────────

test("RelayBackup.derive: channel matches the cross-language vector and depends on every input", async () => {
    const channel = (await RelayBackup.derive("game", "alice", "pw", FAST)).channelId;

    assert.equal(channel, "vesta/identity-backup/884a1d60332e7f820ac1039b7631223e");
    assert.equal((await RelayBackup.derive("game", "  Alice ", "pw", FAST)).channelId, channel);
    for (const [app, user, pw] of [["other", "alice", "pw"], ["game", "bob", "pw"], ["game", "alice", "other"]]) {
        assert.notEqual((await RelayBackup.derive(app, user, pw, FAST)).channelId, channel);
    }
});

test("RelayBackup: authSecret matches the cross-language vector and exportBlob/importBlob are account-bound", async () => {
    const backup = await RelayBackup.derive("game", "alice", "pw", FAST);
    assert.equal(backup.authSecret, "CLrdKDSaRfFotq1kGlEBfAy4qInZPjtKNYE0hzlVjcQ");
    assert.notEqual((await RelayBackup.derive("game", "alice", "other", FAST)).authSecret, backup.authSecret);

    const identity = VestaIdentity.generate();
    const blob = await backup.exportBlob(identity);
    assert.equal((await (await RelayBackup.derive("game", "alice", "pw", FAST)).importBlob(blob)).clientId, identity.clientId);
    for (const [app, user, pw] of [["game", "alice", "wrong"], ["game", "bob", "pw"]]) {
        await assert.rejects((await RelayBackup.derive(app, user, pw, FAST)).importBlob(blob), IdentityBackupError);
    }
    await assert.rejects(backup.importBlob(await identity.exportEncrypted("pw", FAST)), IdentityBackupError);
});

test("RelayBackup: the event is signed + replace, restores, and junk or wrong passwords are skipped", async () => {
    const identity = VestaIdentity.generate();
    const attacker = VestaIdentity.generate();
    const backup = await RelayBackup.derive("game", "alice", "pw", FAST);
    const real = await backup.createEvent(identity);

    assert.equal(real.replace, true);
    assert.equal(verifyEvent(real, identity.publicKey), true);
    const junk = { ...(await (await RelayBackup.derive("game", "alice", "attacker-pw", FAST)).createEvent(attacker)), channelId: backup.channelId };
    const garbage = { ...real, id: crypto.randomUUID(), payload: { hello: "world" } };
    assert.equal((await backup.restore([junk, garbage, real])).clientId, identity.clientId);
    assert.equal(await (await RelayBackup.derive("game", "alice", "wrong", FAST)).restore([real]), null);
    assert.equal(await (await RelayBackup.derive("game", "alice", "pw", { memoryKiB: 512, iterations: 1 })).restore([real]), null);
});
