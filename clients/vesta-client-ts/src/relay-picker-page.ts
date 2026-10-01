// Renders a RelayRecoverySnapshot as the self-contained relay-picker web page (no scripts, no
// external assets). Mirrors the C# `RelayPickerPage`. Pure string rendering, safe in any runtime.

import type { VestaAppConfig } from "./relay.js";
import type { RelayChoice, RelayRecoverySnapshot } from "./relay-recovery.js";

export const MANUAL_CHOICE = "manual";

const STYLE: string =
    "body{font:15px/1.5 system-ui,sans-serif;max-width:42rem;margin:2rem auto;padding:0 1rem;color:#1d1d1f;background:#fafafa}" +
    "h1{font-size:1.4rem;margin-bottom:.25rem}h2{font-size:1.05rem;margin-top:2rem}" +
    ".muted{color:#6b6b70}.alert{padding:.6rem .8rem;border-radius:6px;margin:1rem 0}" +
    ".err{background:#fdecea;border:1px solid #f5c2bd}.ok{background:#e8f5e9;border:1px solid #b7dfb9}.warn{background:#fff6e0;border:1px solid #f0d99b}" +
    ".relay{display:block;border:1px solid #d6d6db;border-radius:6px;padding:.6rem .8rem;margin:.4rem 0;background:#fff}" +
    ".badge{display:inline-block;font-size:.75rem;border-radius:999px;padding:0 .5rem;margin-left:.3rem;background:#ececf0}" +
    ".badge.good{background:#dff3e1}.badge.open{background:#dbeafe}.badge.bad{background:#fde7d7}" +
    "input[type=text]{width:100%;box-sizing:border-box;padding:.4rem;margin:.25rem 0}" +
    "button{font:inherit;padding:.45rem .9rem;margin:.25rem .25rem .25rem 0;cursor:pointer}" +
    "code{background:#ececf0;padding:0 .25rem;border-radius:3px}ul{padding-left:1.2rem}";

export function escapeHtml(value: string): string {
    return value
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#39;");
}

export function renderRelayPickerPage(
    snapshot: RelayRecoverySnapshot,
    config: VestaAppConfig,
    basePath: string,
    activeRelay: string | null,
): string {
    const transient: boolean = snapshot.phase === "discovering" || snapshot.phase === "adopting";
    let html = '<!doctype html><html lang="en"><head><meta charset="utf-8">';
    html += '<meta name="viewport" content="width=device-width, initial-scale=1">';
    html += "<title>Vesta - relay unavailable</title>";
    if (transient) html += '<meta http-equiv="refresh" content="2">';
    html += `<style>${STYLE}</style></head><body>`;
    html += `<p class="muted">Vesta &middot; app <code>${escapeHtml(config.appId)}</code></p>`;

    switch (snapshot.phase) {
        case "healthy":
            html += renderConnected(snapshot, basePath, activeRelay);
            break;
        case "discovering":
            html +=
                '<h1>Looking for relays&hellip;</h1><p class="muted">Asking known relays which servers host this app.</p>';
            break;
        case "adopting":
            html +=
                `<h1>Connecting&hellip;</h1><p class="muted">Trying <code>${escapeHtml(snapshot.adopting ?? "the relay")}</code>. ` +
                "The choice is only kept once the relay accepts the app.</p>";
            break;
        default:
            html += renderPrompt(snapshot, config, basePath);
            break;
    }

    return html + "</body></html>";
}

function renderConnected(snapshot: RelayRecoverySnapshot, basePath: string, activeRelay: string | null): string {
    let html = '<h1>Connected</h1><div class="alert ok">The app is connected';
    if (activeRelay) html += ` to <code>${escapeHtml(activeRelay)}</code>`;
    html += ". You can close this tab.</div>";
    if (snapshot.activeOverride) {
        html +=
            '<p class="muted">You are using a relay you chose yourself. The app owner\'s own relay list is ignored until you switch back.</p>';
        html += postButton(basePath, "clear", "Switch back to the app's own relays");
    }
    return html;
}

function renderPrompt(snapshot: RelayRecoverySnapshot, config: VestaAppConfig, basePath: string): string {
    const dismissed: boolean = snapshot.phase === "degraded";
    let html: string = dismissed
        ? '<h1>Connection lost</h1><p class="muted">Retrying in the background. You can also pick another relay below.</p>'
        : "<h1>Can't reach any relay</h1><p class=\"muted\">Every relay this app knows about is unavailable. Vesta keeps retrying in the background.</p>";

    if (snapshot.failureReason) {
        html += `<div class="alert err">${escapeHtml(snapshot.failureReason)}</div>`;
    }

    if (snapshot.triedRelays.length > 0) {
        html += "<h2>Tried</h2><ul>";
        for (const attempt of snapshot.triedRelays) {
            html += `<li><code>${escapeHtml(attempt.relay)}</code> <span class="muted">${escapeHtml(attempt.reason)}</span></li>`;
        }
        html += "</ul>";
    }

    html += "<h2>Use another relay</h2>";
    if (snapshot.phase === "nothingFound") {
        html += '<p class="muted">No other relays were found. You can still enter one by hand.</p>';
    }

    const appId: string = snapshot.failedOptions?.appId ?? snapshot.activeOverride?.appId ?? config.appId;
    const register: boolean = snapshot.failedOptions?.registerApp ?? snapshot.activeOverride?.registerApp ?? false;
    const manual: string = snapshot.failedRelay ?? "";
    const manualChecked: boolean = snapshot.failedRelay !== null || snapshot.choices.length === 0;

    html += `<form method="post" action="${escapeHtml(basePath)}/adopt">`;
    snapshot.choices.forEach((choice: RelayChoice, i: number) => {
        const isChecked: boolean = !manualChecked && i === 0;
        html +=
            `<label class="relay"><input type="radio" name="relay" value="${escapeHtml(choice.url)}"${isChecked ? " checked" : ""}> ` +
            `<code>${escapeHtml(choice.url)}</code>${badges(choice)}</label>`;
    });

    html +=
        `<label class="relay"><input type="radio" name="relay" value="${MANUAL_CHOICE}"${manualChecked ? " checked" : ""}> Enter a relay address` +
        `<input type="text" name="manual" placeholder="wss://relay.example.com" value="${escapeHtml(manual)}"></label>`;

    html += "<h2>App settings on that relay</h2>";
    html += `<label>App namespace<input type="text" name="appId" value="${escapeHtml(appId)}"></label>`;
    html +=
        `<p class="muted">Channels are stored under this namespace on the relay. Keep <code>${escapeHtml(config.appId)}</code> ` +
        "unless the relay already uses a different name for this app.</p>";
    html += `<label><input type="checkbox" name="register" value="1"${register ? " checked" : ""}> Register app on connect</label>`;
    html +=
        '<p class="muted">Needed when the relay does not accept unregistered apps and has not seen this namespace yet. ' +
        "A relay that does not advertise this app may not hold your data; nothing is changed on the original relay.</p>";
    html += '<button type="submit">Use this relay</button></form>';

    html += "<h2>Other options</h2>";
    html += postButton(basePath, "discover", "Find relays");
    html += postButton(basePath, "retry", "Retry now");
    if (snapshot.activeOverride) {
        html += postButton(basePath, "clear", "Switch back to the app's own relays");
    }
    if (!dismissed) {
        html += postButton(basePath, "dismiss", "Keep retrying in the background");
    }
    return html;
}

function badges(choice: RelayChoice): string {
    let html: string = choice.hostsRequestedApp
        ? '<span class="badge good">hosts this app</span>'
        : '<span class="badge bad">does not advertise this app</span>';
    if (choice.acceptsUnregisteredApps === true) {
        html += '<span class="badge open">Open relay</span>';
    } else if (choice.acceptsUnregisteredApps === false) {
        html += '<span class="badge">registered apps only</span>';
    }
    if (choice.fromCache) html += '<span class="badge">remembered, not confirmed</span>';
    return html;
}

function postButton(basePath: string, action: string, label: string): string {
    return (
        `<form method="post" action="${escapeHtml(basePath)}/${action}" style="display:inline">` +
        `<button type="submit">${escapeHtml(label)}</button></form>`
    );
}
