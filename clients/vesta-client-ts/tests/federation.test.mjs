// Federation (server-to-server discovery) tests: descriptor signing/verification and
// FederationClient owner-mismatch spoof rejection.
// Mirrors tests/VestaCore.Tests/Relay/DescriptorSignerTests.cs and
// tests/VestaClient.Tests/FederationClientTests.cs.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";
import * as ed from "@noble/ed25519";

import {
    buildDescriptorSigningInput,
    bytesToBase64Url,
    deriveClientId,
    FederationClient,
    isDescriptorExpired,
    verifyDescriptor,
    VestaIdentity,
} from "../dist/index.js";

function signDescriptor(descriptor, identity) {
    const withKey = { ...descriptor, relayPublicKey: identity.publicKeyB64, signature: undefined };
    const sig = ed.sign(buildDescriptorSigningInput(withKey), identity.privateKey);
    return { ...withKey, signature: bytesToBase64Url(sig) };
}

function makeDescriptor(apps = [], issuedAt = new Date().toISOString()) {
    return {
        relayPublicKey: "",
        urls: ["wss://relay-a.example/ws"],
        apps,
        issuedAt,
        ttlSeconds: 300,
    };
}

// ── sign / verify ──────────────────────────────────────────────────────────────

test("signDescriptor + verifyDescriptor: valid self-signature verifies", () => {
    const relay = VestaIdentity.generate();
    const signed = signDescriptor(makeDescriptor(), relay);
    assert.ok(verifyDescriptor(signed));
});

test("verifyDescriptor rejects tampered urls", () => {
    const relay = VestaIdentity.generate();
    const signed = signDescriptor(makeDescriptor(), relay);
    const tampered = { ...signed, urls: ["wss://evil.example/ws"] };
    assert.equal(verifyDescriptor(tampered), false);
});

test("verifyDescriptor rejects a signature from a different key", () => {
    const relay = VestaIdentity.generate();
    const attacker = VestaIdentity.generate();
    const signed = signDescriptor(makeDescriptor(), relay);
    const spoofed = { ...signed, relayPublicKey: attacker.publicKeyB64 };
    assert.equal(verifyDescriptor(spoofed), false);
});

test("isDescriptorExpired: true once issuedAt + ttlSeconds has passed", () => {
    const descriptor = makeDescriptor([], "2020-01-01T00:00:00Z");
    assert.equal(isDescriptorExpired(descriptor, new Date("2020-01-01T00:10:00Z")), true);
    assert.equal(isDescriptorExpired(descriptor, new Date("2020-01-01T00:00:01Z")), false);
});

// ── FederationClient ───────────────────────────────────────────────────────────

function fakeFetch(responses) {
    return async (url) => {
        const key = url.toString();
        const body = responses[key] ?? [];
        return {
            ok: true,
            json: async () => body,
        };
    };
}

test("FederationClient.discoverRelaysForApp keeps only verified, owner-matching relays", async () => {
    const owner = VestaIdentity.generate();
    const relay = VestaIdentity.generate();
    const spoofer = VestaIdentity.generate();
    const appConfig = { appId: "chess", ownerPublicKey: owner.publicKeyB64, defaultRelays: [] };
    const expectedOwnerClientId = deriveClientId(owner.publicKey);

    const genuine = signDescriptor(
        makeDescriptor([{ appId: "chess", ownerClientId: expectedOwnerClientId }]),
        relay,
    );
    const spoofed = signDescriptor(
        makeDescriptor([{ appId: "chess", ownerClientId: deriveClientId(spoofer.publicKey) }]),
        spoofer,
    );

    const client = new FederationClient(
        appConfig,
        fakeFetch({
            "https://relay-a.example/federation/apps/chess": [genuine, spoofed],
        }),
    );

    const discovered = await client.discoverRelaysForApp("https://relay-a.example");
    assert.equal(discovered.length, 1);
    assert.equal(discovered[0].relayPublicKey, relay.publicKeyB64);
    assert.equal(discovered[0].hostsRequestedApp, true);
});

test("FederationClient.listAllRelays flags app-hosting vs. non-hosting relays", async () => {
    const owner = VestaIdentity.generate();
    const hostingRelay = VestaIdentity.generate();
    const otherRelay = VestaIdentity.generate();
    const appConfig = { appId: "chess", ownerPublicKey: owner.publicKeyB64, defaultRelays: [] };
    const expectedOwnerClientId = deriveClientId(owner.publicKey);

    const hosting = signDescriptor(
        makeDescriptor([{ appId: "chess", ownerClientId: expectedOwnerClientId }]),
        hostingRelay,
    );
    const other = signDescriptor(makeDescriptor([]), otherRelay);

    const client = new FederationClient(
        appConfig,
        fakeFetch({ "https://relay-a.example/federation/peers": [hosting, other] }),
    );

    const all = await client.listAllRelays("https://relay-a.example");
    assert.equal(all.length, 2);
    const hostingEntry = all.find((r) => r.relayPublicKey === hostingRelay.publicKeyB64);
    const otherEntry = all.find((r) => r.relayPublicKey === otherRelay.publicKeyB64);
    assert.equal(hostingEntry.hostsRequestedApp, true);
    assert.equal(otherEntry.hostsRequestedApp, false);
});

test("FederationClient discovery yields nothing when the fetch fails", async () => {
    const owner = VestaIdentity.generate();
    const appConfig = { appId: "chess", ownerPublicKey: owner.publicKeyB64, defaultRelays: [] };
    const client = new FederationClient(appConfig, async () => {
        throw new Error("network down");
    });
    const discovered = await client.discoverRelaysForApp("https://unreachable.example");
    assert.deepEqual(discovered, []);
});

// ── toFederationBaseUrl ────────────────────────────────────────────────────────

test("FederationClient.toFederationBaseUrl maps ws/wss to http/https and strips path", () => {
    assert.equal(FederationClient.toFederationBaseUrl("ws://relay.example:8080/ws"), "http://relay.example:8080/");
    assert.equal(FederationClient.toFederationBaseUrl("wss://relay.example/ws"), "https://relay.example/");
    assert.equal(FederationClient.toFederationBaseUrl("not a url"), null);
});
