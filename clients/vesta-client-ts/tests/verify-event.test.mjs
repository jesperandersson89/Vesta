// Receive-side event verification: lets a client trust an event by signer instead of by relay.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";

import { createEvent, deriveClientId, VestaIdentity, verifyEvent } from "../dist/index.js";

test("verifyEvent: accepts an event signed by the matching key", () => {
    const shopkeeper = VestaIdentity.generate();
    const event = createEvent("game/shop", shopkeeper, "shop.purchase-confirmed", { item: "scarecrow" });

    assert.equal(verifyEvent(event, shopkeeper.publicKey), true);
    assert.equal(event.clientId, deriveClientId(shopkeeper.publicKey));
});

test("verifyEvent: rejects a different key, a tampered payload, and a missing or garbage signature", () => {
    const shopkeeper = VestaIdentity.generate();
    const impostor = VestaIdentity.generate();
    const event = createEvent("game/shop", shopkeeper, "shop.purchase-confirmed", { item: "scarecrow" });

    assert.equal(verifyEvent(event, impostor.publicKey), false);
    assert.equal(verifyEvent({ ...event, payload: { item: "golden-scarecrow" } }, shopkeeper.publicKey), false);
    assert.equal(verifyEvent({ ...event, signature: undefined }, shopkeeper.publicKey), false);
    assert.equal(verifyEvent({ ...event, signature: "not base64url!!" }, shopkeeper.publicKey), false);
});

test("verifyEvent: ignores unsigned transport fields such as metadata", () => {
    const shopkeeper = VestaIdentity.generate();
    const event = createEvent("game/shop", shopkeeper, "shop.price", { gold: 5 });

    assert.equal(verifyEvent({ ...event, metadata: { ttlSeconds: 1 } }, shopkeeper.publicKey), true);
});
