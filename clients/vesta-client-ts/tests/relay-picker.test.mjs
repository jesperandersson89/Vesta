// Built-in relay picker tests: the rendered page and the loopback web server.
// Mirrors tests/VestaClient.Tests/WebRelayPickerTests.cs.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import http from "node:http";

import { InMemoryRelayOverrideStore, RelayDirectory, setRelayPickerEnabled } from "../dist/index.js";
import { NodeWebRelayPicker } from "../dist/node.js";
import { renderRelayPickerPage } from "../dist/relay-picker-page.js";

// Importing the Node entry registers the web picker; never open a browser from tests.
setRelayPickerEnabled(false);

const CONFIG = { appId: "chess", ownerPublicKey: "unused", defaultRelays: ["wss://dead.example/ws"] };

function snapshot(overrides = {}) {
    return {
        phase: "exhausted",
        triedRelays: [],
        choices: [],
        activeOverride: null,
        backgroundRetrying: true,
        failedPasses: 3,
        adopting: null,
        failureReason: null,
        failedRelay: null,
        failedOptions: null,
        ...overrides,
    };
}

// ── renderRelayPickerPage ────────────────────────────────────────────────────

test("renderRelayPickerPage: escapes relay URLs, reasons and namespace", () => {
    const html = renderRelayPickerPage(
        snapshot({
            triedRelays: [{ relay: "wss://<b>evil</b>/ws", reason: 'a "quoted" <script>' }],
            choices: [
                {
                    url: "wss://x.example/ws?a=1&b=2",
                    relayPublicKey: null,
                    hostsRequestedApp: true,
                    fromCache: false,
                    acceptsUnregisteredApps: null,
                },
            ],
            failedOptions: { appId: '"><script>x</script>', registerApp: false },
        }),
        CONFIG,
        "/tok",
        null,
    );
    assert.ok(!html.includes("<script>"));
    assert.ok(!html.includes("<b>evil"));
    assert.ok(html.includes("wss://x.example/ws?a=1&amp;b=2"));
});

test("renderRelayPickerPage: shows the namespace input prefilled and the register checkbox", () => {
    const html = renderRelayPickerPage(snapshot(), CONFIG, "/tok", null);
    assert.match(html, /name="appId" value="chess"/);
    assert.match(html, /name="register" value="1"/);
    assert.match(html, /action="\/tok\/adopt"/);
});

test("renderRelayPickerPage: flags relays that do not advertise the app and open relays", () => {
    const html = renderRelayPickerPage(
        snapshot({
            choices: [
                {
                    url: "wss://a.example/ws",
                    relayPublicKey: "k",
                    hostsRequestedApp: false,
                    fromCache: true,
                    acceptsUnregisteredApps: true,
                },
            ],
        }),
        CONFIG,
        "/tok",
        null,
    );
    assert.ok(html.includes("does not advertise this app"));
    assert.ok(html.includes("Open relay"));
    assert.ok(html.includes("remembered, not confirmed"));
});

test("renderRelayPickerPage: pre-fills the failed attempt's namespace and relay", () => {
    const html = renderRelayPickerPage(
        snapshot({
            phase: "failed",
            failureReason: "APP_NOT_ALLOWED: nope",
            failedRelay: "wss://retry.example/ws",
            failedOptions: { appId: "chess-2", registerApp: true },
        }),
        CONFIG,
        "/tok",
        null,
    );
    assert.match(html, /name="appId" value="chess-2"/);
    assert.ok(html.includes("wss://retry.example/ws"));
    assert.ok(html.includes("APP_NOT_ALLOWED"));
});

test("renderRelayPickerPage: connected page offers to switch back when an override is active", () => {
    const html = renderRelayPickerPage(
        snapshot({ phase: "healthy", activeOverride: { relay: "wss://mine.example/ws" } }),
        CONFIG,
        "/tok",
        "wss://mine.example/ws",
    );
    assert.ok(html.includes("Connected"));
    assert.ok(html.includes("/tok/clear"));
});

// ── NodeWebRelayPicker (loopback server) ─────────────────────────────────────

class FakeHost extends EventEmitter {
    constructor() {
        super();
        this.relayDirectory = new RelayDirectory(CONFIG, new InMemoryRelayOverrideStore());
        this.relays = ["wss://dead.example/ws"];
        this.activeRelay = "wss://dead.example/ws";
        this.autoReconnect = true;
        this.adopted = [];
    }
    async reconnect() {
        return false;
    }
    async adoptRelay(override) {
        this.adopted.push(override);
        return { connected: true, failureReason: null };
    }
    async clearRelayOverride() {
        return true;
    }
}

async function startPicker() {
    const host = new FakeHost();
    const launched = [];
    const picker = new NodeWebRelayPicker(host, CONFIG, (url) => {
        launched.push(url);
        return true;
    });
    await picker.show({ attempts: [{ relay: "wss://dead.example/ws", reason: "refused" }], passes: 3 });
    return { host, picker, launched };
}

function request(url, { method = "GET", headers = {}, body } = {}) {
    const target = new URL(url);
    return new Promise((resolve, reject) => {
        const req = http.request(
            {
                host: "127.0.0.1",
                port: target.port,
                path: target.pathname,
                method,
                headers: { ...headers, ...(body === undefined ? {} : { "Content-Length": Buffer.byteLength(body) }) },
            },
            (res) => {
                const chunks = [];
                res.on("data", (c) => chunks.push(c));
                res.on("end", () =>
                    resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks).toString("utf8") }),
                );
            },
        );
        req.on("error", reject);
        if (body !== undefined) req.write(body);
        req.end();
    });
}

const FORM = { "Content-Type": "application/x-www-form-urlencoded" };

test("NodeWebRelayPicker: serves the page on loopback under a secret token and launches once per outage", async () => {
    const { picker, launched } = await startPicker();
    try {
        assert.match(picker.url, /^http:\/\/127\.0\.0\.1:\d+\/[0-9a-f]{32}\/$/);
        assert.deepEqual(launched, [picker.url]);

        const page = await request(picker.url);
        assert.equal(page.status, 200);
        assert.ok(page.body.includes("<html"));
        assert.match(page.headers["content-security-policy"], /default-src 'none'/);

        // A second exhaustion in the same outage does not relaunch.
        await picker.show({ attempts: [], passes: 4 });
        assert.equal(launched.length, 1);
    } finally {
        picker.dispose();
    }
});

test("NodeWebRelayPicker: rejects a foreign Host header and a wrong token", async () => {
    const { picker } = await startPicker();
    try {
        const badHost = await request(picker.url, { headers: { Host: "evil.example" } });
        assert.equal(badHost.status, 400);

        const badToken = await request(picker.url.replace(/[0-9a-f]{32}/, "0".repeat(32)));
        assert.equal(badToken.status, 404);
    } finally {
        picker.dispose();
    }
});

test("NodeWebRelayPicker: actions require POST", async () => {
    const { picker } = await startPicker();
    try {
        const get = await request(`${picker.url}adopt`);
        assert.equal(get.status, 405);
        const post = await request(picker.url, { method: "POST", headers: FORM, body: "" });
        assert.equal(post.status, 405);
    } finally {
        picker.dispose();
    }
});

test("NodeWebRelayPicker: adopt form forwards the relay, namespace and register flag then redirects", async () => {
    const { host, picker } = await startPicker();
    try {
        const res = await request(`${picker.url}adopt`, {
            method: "POST",
            headers: FORM,
            body: "relay=manual&manual=wss%3A%2F%2Fnew.example%2Fws&appId=chess-2&register=1",
        });
        assert.equal(res.status, 303);
        assert.ok(res.headers.location.endsWith("/"));

        for (let i = 0; i < 50 && host.adopted.length === 0; i++) await new Promise((r) => setImmediate(r));
        assert.equal(host.adopted.length, 1);
        assert.equal(host.adopted[0].relay, "wss://new.example/ws");
        assert.equal(host.adopted[0].appId, "chess-2");
        assert.equal(host.adopted[0].registerApp, true);
    } finally {
        picker.dispose();
    }
});

test("NodeWebRelayPicker: rejects oversized and non-form bodies", async () => {
    const { host, picker } = await startPicker();
    try {
        const big = await request(`${picker.url}adopt`, {
            method: "POST",
            headers: FORM,
            body: "manual=" + "a".repeat(9 * 1024),
        });
        assert.equal(big.status, 400);

        const json = await request(`${picker.url}adopt`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: '{"relay":"wss://x.example/ws"}',
        });
        assert.equal(json.status, 400);
        assert.equal(host.adopted.length, 0);
    } finally {
        picker.dispose();
    }
});

test("NodeWebRelayPicker: dispose stops the server", async () => {
    const { picker } = await startPicker();
    const url = picker.url;
    picker.dispose();
    await assert.rejects(request(url));
    assert.equal(picker.url, null);
});
