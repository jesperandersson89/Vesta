/**
 * Views over {@link RelayRecoverySession}: a `<vesta-relay-picker>` custom element for browsers
 * and a transport-agnostic console prompt for CLI apps. Both render only from the session
 * snapshot and never adopt a relay without an explicit user action.
 */

import type { RelayChoice, RelayRecoverySession, RelayRecoverySnapshot } from "./relay-recovery.js";

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

// ─── Browser element ─────────────────────────────────────────────────────────

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
.row { display: flex; gap: 8px; margin-top: 8px; flex-wrap: wrap; }
input { flex: 1; min-width: 12em; padding: 4px 8px; }
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
    private pendingConfirm: RelayChoice | null = null;
    private manualUrl = "";
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
            panel.append(this.renderConfirm(session, this.pendingConfirm));
        } else {
            if (snapshot.choices.length > 0) panel.append(this.renderChoices(snapshot.choices));
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

    private renderChoices(choices: RelayChoice[]): HTMLUListElement {
        const list: HTMLUListElement = document.createElement("ul");
        for (const choice of choices) {
            const item: HTMLLIElement = document.createElement("li");
            const url: HTMLSpanElement = document.createElement("span");
            url.className = "url";
            url.textContent = choice.url;
            const tag: HTMLSpanElement = document.createElement("span");
            tag.className = "tag";
            tag.textContent = `${choice.hostsRequestedApp ? "hosts this app" : "unverified"}${
                choice.fromCache ? ", remembered" : ""
            }`;
            const use: HTMLButtonElement = this.button("Use", () => this.choose(choice));
            item.append(url, tag, use);
            list.append(item);
        }
        return list;
    }

    private choose(choice: RelayChoice): void {
        if (choice.hostsRequestedApp) {
            void this._session?.adopt(choice);
            return;
        }
        this.pendingConfirm = choice;
        this.render();
    }

    private renderConfirm(session: RelayRecoverySession, choice: RelayChoice): HTMLElement {
        const box: HTMLDivElement = document.createElement("div");
        const text: HTMLParagraphElement = document.createElement("p");
        text.textContent = `${choice.url} ${UNVERIFIED_RELAY_WARNING}`;
        const row: HTMLDivElement = document.createElement("div");
        row.className = "row";
        row.append(
            this.button("Connect anyway", () => {
                this.pendingConfirm = null;
                void session.adopt(choice);
            }),
            this.button("Cancel", () => {
                this.pendingConfirm = null;
                this.render();
            }),
        );
        box.append(text, row);
        return box;
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
        input.value = this.manualUrl;
        input.disabled = busy;
        input.addEventListener("input", () => {
            this.manualUrl = input.value;
        });
        const use: HTMLButtonElement = this.button("Use this relay", () => {
            const url: string = this.manualUrl;
            this.manualUrl = "";
            void session.useManual(url);
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

// ─── Console prompt ──────────────────────────────────────────────────────────

/** Line-oriented I/O for {@link runConsoleRelayPicker}; wire it to `readline`, a TTY, or a test script. */
export interface ConsoleRelayPickerIO {
    /** Resolve the next input line, or null at end of input. */
    readLine(): Promise<string | null>;
    write(text: string): void;
}

/**
 * A plain-text prompt loop over a {@link RelayRecoverySession}. Resolves true once the
 * connection is healthy again, false if the user quits the prompt or input ends.
 */
export async function runConsoleRelayPicker(
    session: RelayRecoverySession,
    io: ConsoleRelayPickerIO,
): Promise<boolean> {
    for (;;) {
        const snapshot: RelayRecoverySnapshot = session.snapshot;
        if (snapshot.phase === "healthy") {
            io.write("Connected.\n");
            return true;
        }

        renderConsole(snapshot, io);

        const line: string | null = await io.readLine();
        if (line === null) return false;
        const command: string = line.trim();
        if (command.length === 0) continue;

        const number: number = Number(command);
        if (Number.isInteger(number) && number >= 1 && number <= snapshot.choices.length) {
            const choice: RelayChoice = snapshot.choices[number - 1]!;
            if (!choice.hostsRequestedApp && !(await confirmConsole(io, choice))) continue;
            await session.adopt(choice);
            continue;
        }

        const [verb = "", rest = ""] = splitOnce(command);
        switch (verb.toLowerCase()) {
            case "r":
                await session.retry();
                break;
            case "d":
                await session.discover();
                break;
            case "u":
                await session.useManual(rest);
                break;
            case "c":
                await session.clearOverride();
                break;
            case "q":
                session.dismiss();
                return false;
            default:
                io.write("Unknown command.\n");
        }
    }
}

function splitOnce(command: string): [string, string] {
    const index: number = command.search(/\s/);
    return index < 0 ? [command, ""] : [command.slice(0, index), command.slice(index + 1).trim()];
}

async function confirmConsole(io: ConsoleRelayPickerIO, choice: RelayChoice): Promise<boolean> {
    io.write(`${choice.url} ${UNVERIFIED_RELAY_WARNING}\n`);
    io.write("Connect anyway? [y/N] ");
    const answer: string | null = await io.readLine();
    return answer?.trim().toLowerCase() === "y";
}

function renderConsole(snapshot: RelayRecoverySnapshot, io: ConsoleRelayPickerIO): void {
    const title: string =
        snapshot.phase === "degraded"
            ? "Connection lost. Retrying..."
            : snapshot.phase === "adopting"
              ? `Connecting to ${snapshot.adopting}...`
              : snapshot.phase === "failed"
                ? (snapshot.failureReason ?? "Failed.")
                : PHASE_TITLES[snapshot.phase];
    io.write(`\n${title}\n`);

    for (const attempt of snapshot.triedRelays) {
        io.write(`  x ${attempt.relay} - ${attempt.reason}\n`);
    }
    snapshot.choices.forEach((choice, i) => {
        const tag: string = choice.hostsRequestedApp ? "hosts this app" : "unverified";
        const cached: string = choice.fromCache ? ", remembered" : "";
        io.write(`  ${i + 1}. ${choice.url} (${tag}${cached})\n`);
    });

    if (snapshot.backgroundRetrying) io.write("  (still retrying in the background)\n");
    const clear: string = snapshot.activeOverride === null ? "" : ", [c]lear saved relay";
    io.write(`[r]etry, [d]iscover, [u]se <url>${clear}, [q]uit prompt, or a number > `);
}
