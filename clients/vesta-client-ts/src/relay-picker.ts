/**
 * The browser view over {@link RelayRecoverySession}: a `<vesta-relay-picker>` custom element that
 * `VestaConnection` mounts automatically when every relay is unavailable (Node uses the loopback web
 * page in `node-relay-picker.ts` instead). It renders only from the session snapshot and never
 * adopts a relay without an explicit user action.
 */

import type { RelayAdoptOptions, RelayChoice, RelayRecoverySession, RelayRecoverySnapshot } from "./relay-recovery.js";

export const UNVERIFIED_RELAY_WARNING =
    "is not verified to host this app. It may not have your data, and a relay you don't trust can see what you send it.";

const PHASE_TITLES: Record<RelayRecoverySnapshot["phase"], string> = {
    healthy: "",
    degraded: "",
    exhausted: "Can't reach any relay for this app.",
    discovering: "Looking for other relays...",
    choices: "Other relays found:",
    nothingFound: "No other relays found.",
    adopting: "Connecting...",
    failed: "Failed.",
};

const STYLE = `
:host { display: block; font: 14px system-ui, sans-serif; }
:host([hidden]) { display: none; }
.panel { border: 1px solid #b45309; background: #fffbeb; color: #1c1917; border-radius: 8px; padding: 12px 16px; }
h3 { margin: 0 0 8px; font-size: 15px; }
ul { list-style: none; margin: 8px 0; padding: 0; }
li { display: flex; gap: 8px; align-items: center; margin: 4px 0; }
.url { font-family: ui-monospace, monospace; word-break: break-all; }
.tag { font-size: 12px; color: #57534e; }
.error { color: #b91c1c; }
.row { display: flex; gap: 8px; margin-top: 8px; flex-wrap: wrap; align-items: center; }
label { display: flex; gap: 6px; align-items: center; }
input[type=url], input[type=text] { flex: 1; min-width: 12em; padding: 4px 8px; }
button { padding: 4px 10px; cursor: pointer; }
`;

/**
 * `<vesta-relay-picker>` — set `.session` to a {@link RelayRecoverySession}. The element is
 * hidden while the connection is healthy (or the prompt was dismissed) and shown once the
 * session reports a recoverable outage. All relay-derived text is inserted via `textContent`.
 */
const ElementBase: typeof HTMLElement =
    typeof HTMLElement === "undefined" ? (class {} as unknown as typeof HTMLElement) : HTMLElement;

export class VestaRelayPickerElement extends ElementBase {
    private _session: RelayRecoverySession | null = null;
    private unsubscribe: (() => void) | null = null;
    private pendingConfirm: { choice: RelayChoice; options: RelayAdoptOptions } | null = null;
    private manualUrl = "";
    private appId: string | null = null;
    private registerApp = false;
    private root: ShadowRoot | null = null;

    get session(): RelayRecoverySession | null {
        return this._session;
    }

    set session(value: RelayRecoverySession | null) {
        this.unsubscribe?.();
        this.unsubscribe = null;
        this._session = value;
        this.pendingConfirm = null;
        if (value) this.unsubscribe = value.onChange(() => this.render());
        this.render();
    }

    connectedCallback(): void {
        this.root ??= this.attachShadow({ mode: "open" });
        this.render();
    }

    disconnectedCallback(): void {
        this.unsubscribe?.();
        this.unsubscribe = null;
    }

    private options(snapshot: RelayRecoverySnapshot, session: RelayRecoverySession): RelayAdoptOptions {
        const fallback: string =
            snapshot.failedOptions?.appId ?? snapshot.activeOverride?.appId ?? session.appId;
        return { appId: this.appId ?? fallback, registerApp: this.registerApp };
    }

    private render(): void {
        if (!this.root) return;
        const session: RelayRecoverySession | null = this._session;
        const snapshot: RelayRecoverySnapshot | null = session?.snapshot ?? null;
        const visible: boolean =
            snapshot !== null && snapshot.phase !== "healthy" && snapshot.phase !== "degraded";
        this.hidden = !visible;
        this.root.replaceChildren();
        if (!session || !snapshot || !visible) return;

        const style: HTMLStyleElement = document.createElement("style");
        style.textContent = STYLE;
        const panel: HTMLDivElement = document.createElement("div");
        panel.className = "panel";
        panel.setAttribute("role", "alert");

        const title: HTMLHeadingElement = document.createElement("h3");
        title.textContent =
            snapshot.phase === "adopting"
                ? `Connecting to ${snapshot.adopting ?? "relay"}...`
                : snapshot.phase === "failed"
                  ? (snapshot.failureReason ?? PHASE_TITLES.failed)
                  : PHASE_TITLES[snapshot.phase];
        if (snapshot.phase === "failed") title.className = "error";
        panel.append(title);

        if (snapshot.triedRelays.length > 0) {
            const tried: HTMLUListElement = document.createElement("ul");
            for (const attempt of snapshot.triedRelays) {
                const item: HTMLLIElement = document.createElement("li");
                item.textContent = `x ${attempt.relay} - ${attempt.reason}`;
                tried.append(item);
            }
            panel.append(tried);
        }

        if (this.pendingConfirm) {
            panel.append(this.renderConfirm(session, this.pendingConfirm.choice, this.pendingConfirm.options));
        } else {
            if (snapshot.choices.length > 0) panel.append(this.renderChoices(session, snapshot));
            panel.append(this.renderSettings(session, snapshot));
            panel.append(this.renderActions(session, snapshot));
        }

        if (snapshot.backgroundRetrying) {
            const note: HTMLParagraphElement = document.createElement("p");
            note.className = "tag";
            note.textContent = "Still retrying in the background.";
            panel.append(note);
        }
        this.root.append(style, panel);
    }

    private renderChoices(session: RelayRecoverySession, snapshot: RelayRecoverySnapshot): HTMLUListElement {
        const list: HTMLUListElement = document.createElement("ul");
        for (const choice of snapshot.choices) {
            const item: HTMLLIElement = document.createElement("li");
            const url: HTMLSpanElement = document.createElement("span");
            url.className = "url";
            url.textContent = choice.url;
            const tag: HTMLSpanElement = document.createElement("span");
            tag.className = "tag";
            const parts: string[] = [choice.hostsRequestedApp ? "hosts this app" : "unverified"];
            if (choice.acceptsUnregisteredApps === true) parts.push("open relay");
            else if (choice.acceptsUnregisteredApps === false) parts.push("registered apps only");
            if (choice.fromCache) parts.push("remembered");
            tag.textContent = parts.join(", ");
            const use: HTMLButtonElement = this.button("Use", () => this.choose(session, snapshot, choice));
            item.append(url, tag, use);
            list.append(item);
        }
        return list;
    }

    private choose(session: RelayRecoverySession, snapshot: RelayRecoverySnapshot, choice: RelayChoice): void {
        const options: RelayAdoptOptions = this.options(snapshot, session);
        if (choice.hostsRequestedApp) {
            void session.adopt(choice, options);
            return;
        }
        this.pendingConfirm = { choice, options };
        this.render();
    }

    private renderConfirm(
        session: RelayRecoverySession,
        choice: RelayChoice,
        options: RelayAdoptOptions,
    ): HTMLElement {
        const box: HTMLDivElement = document.createElement("div");
        const text: HTMLParagraphElement = document.createElement("p");
        text.textContent = `${choice.url} ${UNVERIFIED_RELAY_WARNING}`;
        const row: HTMLDivElement = document.createElement("div");
        row.className = "row";
        row.append(
            this.button("Connect anyway", () => {
                this.pendingConfirm = null;
                void session.adopt(choice, options);
            }),
            this.button("Cancel", () => {
                this.pendingConfirm = null;
                this.render();
            }),
        );
        box.append(text, row);
        return box;
    }

    private renderSettings(session: RelayRecoverySession, snapshot: RelayRecoverySnapshot): HTMLElement {
        const busy: boolean = snapshot.phase === "adopting" || snapshot.phase === "discovering";
        const wrap: HTMLDivElement = document.createElement("div");
        wrap.className = "row";

        const nsLabel: HTMLLabelElement = document.createElement("label");
        nsLabel.append("App namespace");
        const ns: HTMLInputElement = document.createElement("input");
        ns.type = "text";
        ns.value = this.options(snapshot, session).appId ?? "";
        ns.disabled = busy;
        ns.addEventListener("input", () => {
            this.appId = ns.value;
        });
        nsLabel.append(ns);

        const regLabel: HTMLLabelElement = document.createElement("label");
        const reg: HTMLInputElement = document.createElement("input");
        reg.type = "checkbox";
        reg.checked = this.registerApp;
        reg.disabled = busy;
        reg.addEventListener("change", () => {
            this.registerApp = reg.checked;
        });
        regLabel.append(reg, "Register app on connect");

        wrap.append(nsLabel, regLabel);
        return wrap;
    }

    private renderActions(session: RelayRecoverySession, snapshot: RelayRecoverySnapshot): HTMLElement {
        const busy: boolean = snapshot.phase === "adopting" || snapshot.phase === "discovering";
        const wrap: HTMLDivElement = document.createElement("div");

        const row: HTMLDivElement = document.createElement("div");
        row.className = "row";
        const retry: HTMLButtonElement = this.button("Retry", () => void session.retry());
        const discover: HTMLButtonElement = this.button("Find other relays", () => void session.discover());
        retry.disabled = busy;
        discover.disabled = busy;
        row.append(retry, discover);
        if (snapshot.activeOverride) {
            row.append(this.button("Clear saved relay", () => void session.clearOverride()));
        }
        row.append(this.button("Dismiss", () => session.dismiss()));

        const manual: HTMLDivElement = document.createElement("div");
        manual.className = "row";
        const input: HTMLInputElement = document.createElement("input");
        input.type = "url";
        input.placeholder = "wss://relay.example.com";
        input.value = this.manualUrl || (snapshot.failedRelay ?? "");
        input.disabled = busy;
        input.addEventListener("input", () => {
            this.manualUrl = input.value;
        });
        const use: HTMLButtonElement = this.button("Use this relay", () => {
            const url: string = this.manualUrl || (snapshot.failedRelay ?? "");
            this.manualUrl = "";
            void session.useManual(url, this.options(snapshot, session));
        });
        use.disabled = busy;
        manual.append(input, use);

        wrap.append(row, manual);
        return wrap;
    }

    private button(label: string, onClick: () => void): HTMLButtonElement {
        const button: HTMLButtonElement = document.createElement("button");
        button.type = "button";
        button.textContent = label;
        button.addEventListener("click", onClick);
        return button;
    }
}

/** Register `<vesta-relay-picker>` (idempotent). Returns the element class. No-op outside browsers. */
export function defineRelayPicker(tagName = "vesta-relay-picker"): typeof VestaRelayPickerElement {
    if (typeof customElements !== "undefined" && !customElements.get(tagName)) {
        customElements.define(tagName, VestaRelayPickerElement);
    }
    return VestaRelayPickerElement;
}

/**
 * Mount a `<vesta-relay-picker>` bound to `session` as a fixed overlay at the top of the page.
 * Returns a function that removes it, or null when there is no DOM (e.g. Node).
 */
export function mountRelayPickerOverlay(session: RelayRecoverySession): (() => void) | null {
    if (typeof document === "undefined" || typeof customElements === "undefined" || !document.body) return null;
    defineRelayPicker();
    const element: VestaRelayPickerElement = document.createElement("vesta-relay-picker") as VestaRelayPickerElement;
    element.style.cssText =
        "position:fixed;top:12px;left:50%;transform:translateX(-50%);z-index:2147483647;max-width:min(40rem,95vw);box-shadow:0 4px 24px rgba(0,0,0,.25)";
    document.body.append(element);
    element.session = session;
    return () => {
        element.session = null;
        element.remove();
    };
}
