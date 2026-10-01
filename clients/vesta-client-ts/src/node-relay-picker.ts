// The built-in relay-recovery UI for Node: a loopback-only web page that opens in the user's browser
// when a connection has exhausted every relay. It is an integral part of VestaConnection, not
// something an app wires up. The page is served from 127.0.0.1 on a random port under a secret
// per-launch token, carries no scripts, and drives a RelayRecoverySession; it never adopts a relay
// without an explicit form submit. Node built-ins are imported lazily so this file is safe to
// bundle for browsers (where it is never used).

import type { IncomingMessage, Server, ServerResponse } from "node:http";
import type { VestaAppConfig, RelaysExhaustedInfo } from "./relay.js";
import { MANUAL_CHOICE, renderRelayPickerPage } from "./relay-picker-page.js";
import { RelayRecoverySession } from "./relay-recovery.js";
import type { RelayRecoveryHost, RelayRecoverySnapshot } from "./relay-recovery.js";

const MAX_BODY_BYTES = 8 * 1024;

/** Opens a URL for the user. Returns true if a browser was launched. */
export type PickerLauncher = (url: string) => Promise<boolean> | boolean;

// Indirect specifiers keep bundlers from trying to resolve Node built-ins.
async function nodeImport<T>(specifier: string): Promise<T> {
    return (await import(/* @vite-ignore */ specifier)) as T;
}

export class NodeWebRelayPicker {
    readonly session: RelayRecoverySession;
    private readonly host: RelayRecoveryHost;
    private readonly config: VestaAppConfig;
    private readonly launch: PickerLauncher;
    private readonly unsubscribe: () => void;
    private token = "";
    private server: Server | null = null;
    private port = 0;
    private starting: Promise<void> | null = null;
    private launchedForOutage = false;
    private disposed = false;

    constructor(
        host: RelayRecoveryHost,
        config: VestaAppConfig,
        launch?: PickerLauncher,
        session?: RelayRecoverySession,
    ) {
        this.host = host;
        this.config = config;
        this.launch = launch ?? openSystemBrowser;
        this.session = session ?? new RelayRecoverySession(host);
        this.unsubscribe = this.session.onChange((snapshot: RelayRecoverySnapshot) => {
            if (snapshot.phase === "healthy") this.launchedForOutage = false;
        });
    }

    /** The page's address once the server has started, otherwise null. */
    get url(): string | null {
        return this.server ? `http://127.0.0.1:${this.port}/${this.token}/` : null;
    }

    /**
     * Show the prompt for an exhausted outage: start the server if needed, open the browser once per
     * outage, and begin discovering relays. Safe to call repeatedly. Resolves true if a user can reach
     * the page (browser opened, or already open for this outage).
     */
    async show(info: RelaysExhaustedInfo): Promise<boolean> {
        if (this.disposed) return false;
        await this.ensureStarted();
        if (this.disposed) return false;

        if (this.launchedForOutage) return true;
        this.launchedForOutage = true;

        this.session.reportExhausted(info);
        void this.session.discover().catch(() => undefined);
        try {
            return await this.launch(this.url!);
        } catch {
            return false;
        }
    }

    dispose(): void {
        if (this.disposed) return;
        this.disposed = true;
        this.unsubscribe();
        this.server?.close();
        this.server?.closeAllConnections?.();
        this.server = null;
        this.session.dispose();
    }

    private ensureStarted(): Promise<void> {
        if (this.server) return Promise.resolve();
        this.starting ??= this.start();
        return this.starting;
    }

    private async start(): Promise<void> {
        const http = await nodeImport<typeof import("node:http")>("node:http");
        const crypto = await nodeImport<typeof import("node:crypto")>("node:crypto");
        this.token = crypto.randomBytes(16).toString("hex");

        const server: Server = http.createServer((req, res) => this.handle(req, res));
        await new Promise<void>((resolve, reject) => {
            server.once("error", reject);
            server.listen(0, "127.0.0.1", () => {
                server.off("error", reject);
                resolve();
            });
        });
        // Never keep the process alive just for the picker.
        server.unref();
        const address = server.address();
        this.port = typeof address === "object" && address ? address.port : 0;
        this.server = server;
    }

    private handle(req: IncomingMessage, res: ServerResponse): void {
        void this.handleAsync(req, res).catch(() => {
            if (!res.headersSent) respond(res, 500, "Internal error");
            else res.end();
        });
    }

    private async handleAsync(req: IncomingMessage, res: ServerResponse): Promise<void> {
        // DNS-rebinding guard: only the literal loopback origin is served.
        if (req.headers.host !== `127.0.0.1:${this.port}`) {
            respond(res, 400, "Bad host");
            return;
        }

        const basePath = `/${this.token}`;
        const path: string = (req.url ?? "/").split("?")[0]!;
        if (path !== basePath && !path.startsWith(`${basePath}/`)) {
            respond(res, 404, "Not found");
            return;
        }

        const action: string = path.slice(basePath.length).replace(/^\/+|\/+$/g, "");
        if (action === "") {
            if (req.method !== "GET" && req.method !== "HEAD") {
                respond(res, 405, "Method not allowed");
                return;
            }
            respond(
                res,
                200,
                renderRelayPickerPage(
                    this.session.snapshot,
                    this.config,
                    basePath,
                    this.host.activeRelay ?? null,
                ),
                "text/html; charset=utf-8",
            );
            return;
        }

        if (!["adopt", "discover", "retry", "clear", "dismiss"].includes(action)) {
            respond(res, 404, "Not found");
            return;
        }
        if (req.method !== "POST") {
            respond(res, 405, "Method not allowed");
            return;
        }

        const form: URLSearchParams | null = await readForm(req);
        if (form === null) {
            respond(res, 400, "Bad request");
            return;
        }

        switch (action) {
            case "adopt": {
                const choice: string = form.get("relay") ?? "";
                const relay: string = choice === MANUAL_CHOICE ? (form.get("manual") ?? "") : choice;
                void this.session.useManual(relay, {
                    appId: form.get("appId") ?? "",
                    registerApp: form.get("register") === "1",
                });
                break;
            }
            case "discover":
                void this.session.discover().catch(() => undefined);
                break;
            case "retry":
                void this.session.retry().catch(() => undefined);
                break;
            case "clear":
                void this.session.clearOverride().catch(() => undefined);
                break;
            case "dismiss":
                this.session.dismiss();
                break;
        }

        res.writeHead(303, { ...SECURITY_HEADERS, Location: `${basePath}/` });
        res.end();
    }
}

const SECURITY_HEADERS: Record<string, string> = {
    "Cache-Control": "no-store",
    "Referrer-Policy": "no-referrer",
    "X-Content-Type-Options": "nosniff",
    "X-Frame-Options": "DENY",
    "Content-Security-Policy":
        "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'",
};

function respond(res: ServerResponse, status: number, body: string, contentType = "text/plain; charset=utf-8"): void {
    res.writeHead(status, { ...SECURITY_HEADERS, "Content-Type": contentType });
    res.end(body);
}

/** Read a small urlencoded body. Returns null for an unsupported content type or an oversized body. */
async function readForm(req: IncomingMessage): Promise<URLSearchParams | null> {
    const length: number = Number(req.headers["content-length"] ?? 0);
    const type: string = (req.headers["content-type"] ?? "").toLowerCase();
    if (length > MAX_BODY_BYTES) return null;
    if (length > 0 && !type.startsWith("application/x-www-form-urlencoded")) return null;

    const chunks: Buffer[] = [];
    let total = 0;
    for await (const chunk of req as AsyncIterable<Buffer>) {
        total += chunk.length;
        if (total > MAX_BODY_BYTES) return null;
        chunks.push(chunk);
    }
    return new URLSearchParams(Buffer.concat(chunks).toString("utf8"));
}

/** Open a URL in the user's default browser; on failure print it so the user can open it by hand. */
export async function openSystemBrowser(url: string): Promise<boolean> {
    try {
        const cp = await nodeImport<typeof import("node:child_process")>("node:child_process");
        const platform: string = process.platform;
        const child =
            platform === "win32"
                ? cp.spawn("cmd", ["/c", "start", "", url.replace(/&/g, "^&")], { stdio: "ignore", detached: true })
                : cp.spawn(platform === "darwin" ? "open" : "xdg-open", [url], { stdio: "ignore", detached: true });
        const launched: boolean = await new Promise<boolean>((resolve) => {
            child.once("error", () => resolve(false));
            child.once("spawn", () => resolve(true));
        });
        child.unref();
        if (launched) return true;
    } catch {
        // fall through to the printed URL
    }
    console.error(`Vesta: could not open a browser. Open ${url} to choose another relay.`);
    return false;
}
