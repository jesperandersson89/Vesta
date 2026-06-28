// Mirrors VestaCore.Tests.Relay.ManifestSignerTests + VestaClient.Tests.RelayResolutionTests /
// RelayDirectoryTests. Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";

import {
    RelayDirectory,
    VestaIdentity,
    manifestChannelFor,
    resolveRelayCandidates,
    signManifest,
    verifyManifest,
} from "../dist/index.js";

function makeManifest(version = 1) {
    return {
        appId: "chess",
        version,
        issuedAt: "2026-01-01T00:00:00Z",
        relays: [
            { url: "wss://relay-a.example/ws", priority: 0, label: "primary" },
            { url: "wss://relay-b.example/ws", priority: 1, label: "backup" },
        ],
        escapeFallbacks: [
            { url: "wss://escape.example/ws", validFrom: "2026-06-01T00:00:00Z" },
        ],
        ownerPublicKey: "",
    };
}

// ── manifest channel ──────────────────────────────────────────────────────────

test("manifestChannelFor uses reserved relays channel", () => {
    assert.equal(manifestChannelFor("chess"), "chess/vesta/relays");
});

// ── sign / verify ─────────────────────────────────────────────────────────────

test("signManifest populates owner key and signature", () => {
    const owner = VestaIdentity.generate();
    const signed = signManifest(makeManifest(), owner);
    assert.ok(signed.signature);
    assert.equal(signed.ownerPublicKey, owner.publicKeyB64);
});

test("verifyManifest accepts a validly-signed manifest", () => {
    const owner = VestaIdentity.generate();
    const signed = signManifest(makeManifest(), owner);
    assert.ok(verifyManifest(signed, owner.publicKeyB64));
});

test("verifyManifest rejects the wrong key", () => {
    const owner = VestaIdentity.generate();
    const attacker = VestaIdentity.generate();
    const signed = signManifest(makeManifest(), owner);
    assert.equal(verifyManifest(signed, attacker.publicKeyB64), false);
});

test("verifyManifest rejects tampered relays", () => {
    const owner = VestaIdentity.generate();
    const signed = signManifest(makeManifest(), owner);
    const tampered = {
        ...signed,
        relays: [{ url: "wss://evil.example/ws", priority: 0 }],
    };
    assert.equal(verifyManifest(tampered, owner.publicKeyB64), false);
});

test("verifyManifest rejects tampered version", () => {
    const owner = VestaIdentity.generate();
    const signed = signManifest(makeManifest(1), owner);
    assert.equal(verifyManifest({ ...signed, version: 99 }, owner.publicKeyB64), false);
});

// ── resolver precedence ───────────────────────────────────────────────────────

test("resolveRelayCandidates orders override > manifest > defaults and dedups", () => {
    const result = resolveRelayCandidates(
        ["wss://default.example/ws"],
        "wss://override.example/ws",
        ["wss://m1.example/ws", "wss://default.example/ws"],
    );
    assert.deepEqual(result, [
        "wss://override.example/ws",
        "wss://m1.example/ws",
        "wss://default.example/ws",
    ]);
});

// ── RelayDirectory anti-rollback + resolution ─────────────────────────────────

test("RelayDirectory accepts newer and rejects older/equal versions", () => {
    const owner = VestaIdentity.generate();
    const config = {
        appId: "chess",
        ownerPublicKey: owner.publicKeyB64,
        defaultRelays: ["wss://default.example/ws"],
    };
    const directory = new RelayDirectory(config);

    const v3 = signManifest(
        { ...makeManifest(3), relays: [{ url: "wss://m3.example/ws", priority: 0 }], escapeFallbacks: [] },
        owner,
    );
    assert.ok(directory.tryApplyManifest(v3));
    assert.equal(directory.tryApplyManifest(v3), false); // equal version
    const v2 = signManifest(
        { ...makeManifest(2), relays: [{ url: "wss://m2.example/ws", priority: 0 }], escapeFallbacks: [] },
        owner,
    );
    assert.equal(directory.tryApplyManifest(v2), false); // older version
    assert.equal(directory.currentManifest.version, 3);
});

test("RelayDirectory rejects manifest signed by another key", () => {
    const owner = VestaIdentity.generate();
    const attacker = VestaIdentity.generate();
    const config = {
        appId: "chess",
        ownerPublicKey: owner.publicKeyB64,
        defaultRelays: ["wss://default.example/ws"],
    };
    const directory = new RelayDirectory(config);
    const forged = signManifest(
        { ...makeManifest(1), relays: [{ url: "wss://evil.example/ws", priority: 0 }], escapeFallbacks: [] },
        attacker,
    );
    assert.equal(directory.tryApplyManifest(forged), false);
    assert.equal(directory.currentManifest, null);
});

test("RelayDirectory resolves manifest relays above defaults", () => {
    const owner = VestaIdentity.generate();
    const config = {
        appId: "chess",
        ownerPublicKey: owner.publicKeyB64,
        defaultRelays: ["wss://default.example/ws"],
    };
    const directory = new RelayDirectory(config);
    directory.tryApplyManifest(
        signManifest(
            {
                ...makeManifest(1),
                relays: [
                    { url: "wss://m1.example/ws", priority: 0 },
                    { url: "wss://m2.example/ws", priority: 1 },
                ],
                escapeFallbacks: [],
            },
            owner,
        ),
    );
    const candidates = directory.resolveCandidates();
    assert.deepEqual(candidates, [
        "wss://m1.example/ws",
        "wss://m2.example/ws",
        "wss://default.example/ws",
    ]);
});
