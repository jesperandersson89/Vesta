// Vesta relay admin GUI. No build step; Ed25519 comes from the vendored noble module.
import * as ed from "./vendor/noble-ed25519-2.1.0.js";

// ── Helpers ──────────────────────────────────────────────────────────────
const $ = (id) => document.getElementById(id);

const ESCAPES = { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" };
const esc = (v) => String(v ?? "").replace(/[&<>"']/g, (c) => ESCAPES[c]);

const b64url = {
    encode(bytes) {
        return btoa(String.fromCharCode(...bytes))
            .replace(/\+/g, "-")
            .replace(/\//g, "_")
            .replace(/=+$/, "");
    },
    decode(s) {
        s = s.replace(/-/g, "+").replace(/_/g, "/");
        while (s.length % 4) s += "=";
        const bin = atob(s);
        const out = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
        return out;
    },
};

const fmtNum = (n) => (n == null ? "—" : Number(n).toLocaleString());
const fmtDate = (d) => (d ? new Date(d).toLocaleString() : "—");
const fmtDay = (d) => (d ? new Date(d).toLocaleDateString() : "—");

function fmtBytes(n) {
    if (n == null) return "—";
    const units = ["B", "KB", "MB", "GB", "TB"];
    let v = Number(n);
    let i = 0;
    while (v >= 1024 && i < units.length - 1) {
        v /= 1024;
        i++;
    }
    return (i === 0 ? v : v.toFixed(v >= 10 ? 1 : 2)) + " " + units[i];
}

function fmtDuration(seconds) {
    const days = seconds / 86400;
    if (days >= 1) return Math.round(days * 10) / 10 + (days === 1 ? " day" : " days");
    const hours = seconds / 3600;
    if (hours >= 1) return Math.round(hours * 10) / 10 + (hours === 1 ? " hour" : " hours");
    return Math.round(seconds / 60) + " min";
}

const shortKey = (s, n = 12) => (s.length > n ? s.slice(0, n) + "…" : s);

const appLink = (id) => `<a href="#/apps/${encodeURIComponent(id)}" class="mono">${esc(id)}</a>`;
const channelLink = (id) => `<a href="#/channels/${encodeURIComponent(id)}" class="mono">${esc(id)}</a>`;

const copyBtn = (text) =>
    `<button class="btn btn-secondary btn-sm" data-copy="${esc(text)}" title="Copy">Copy</button>`;

const skeleton = `<div class="skeleton"></div><div class="skeleton"></div><div class="skeleton"></div>`;

const METRIC_LABELS = { messages: "Messages / month", storage: "Storage", channels: "Channels" };

function fmtMetric(metric, value) {
    return metric === "storage" ? fmtBytes(value) : fmtNum(value);
}

// ── Auth state ───────────────────────────────────────────────────────────
let token = sessionStorage.getItem("vesta_admin_token");
let tokenExpires = sessionStorage.getItem("vesta_admin_token_expires");
let config = null;

function showSignedIn(signedIn) {
    $("loginView").hidden = signedIn;
    $("app").hidden = !signedIn;
    $("nav").hidden = !signedIn;
    $("logoutBtn").hidden = !signedIn;
    $("footerStatus").textContent = signedIn
        ? "Signed in · session expires " + new Date(tokenExpires).toLocaleTimeString()
        : "Not signed in";
}

function setToken(t, exp) {
    token = t;
    tokenExpires = exp;
    sessionStorage.setItem("vesta_admin_token", t);
    sessionStorage.setItem("vesta_admin_token_expires", exp);
}

function logout() {
    token = null;
    tokenExpires = null;
    config = null;
    sessionStorage.removeItem("vesta_admin_token");
    sessionStorage.removeItem("vesta_admin_token_expires");
    showSignedIn(false);
}

$("logoutBtn").onclick = logout;

$("loginBtn").onclick = async () => {
    const errEl = $("loginError");
    errEl.textContent = "";
    try {
        const seed = b64url.decode($("seedInput").value.trim());
        if (seed.length !== 32) throw new Error("Seed must be 32 bytes");
        const publicKey = await ed.getPublicKeyAsync(seed);

        const chResp = await fetch("/admin/auth/challenge", { method: "POST" });
        if (chResp.status === 429) throw new Error("Too many attempts; wait a minute and retry");
        if (!chResp.ok) throw new Error("Challenge failed: " + chResp.status);
        const challenge = await chResp.json();

        const signature = await ed.signAsync(b64url.decode(challenge.nonce), seed);
        const vfResp = await fetch("/admin/auth/verify", {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify({
                publicKey: b64url.encode(publicKey),
                nonce: challenge.nonce,
                signature: b64url.encode(signature),
            }),
        });
        if (vfResp.status === 401) throw new Error("Not authorized (public key not in admin allow-list?)");
        if (vfResp.status === 429) throw new Error("Too many attempts; wait a minute and retry");
        if (!vfResp.ok) throw new Error("Verify failed: " + vfResp.status);
        const tk = await vfResp.json();
        setToken(tk.token, tk.expiresAt);
        $("seedInput").value = "";
        await start();
    } catch (e) {
        errEl.textContent = e.message ?? String(e);
    }
};

async function api(path, opts = {}) {
    const resp = await fetch(path, {
        ...opts,
        headers: { ...(opts.headers ?? {}), authorization: "Bearer " + token },
    });
    if (resp.status === 401) {
        logout();
        throw new Error("Session expired");
    }
    if (!resp.ok) {
        let detail = "";
        try {
            detail = (await resp.json()).error ?? "";
        } catch {
            /* body is not JSON */
        }
        throw new Error(detail || `${path} → ${resp.status}`);
    }
    if (resp.status === 204) return null;
    return resp.json();
}

const sendJson = (path, method, body) =>
    api(path, {
        method,
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body),
    });

// ── Router ───────────────────────────────────────────────────────────────
const content = $("content");
let handlers = {};
let renderSeq = 0;

content.addEventListener("click", async (e) => {
    const copy = e.target.closest("[data-copy]");
    if (copy) {
        try {
            await navigator.clipboard.writeText(copy.dataset.copy);
            copy.textContent = "Copied";
            setTimeout(() => (copy.textContent = "Copy"), 1500);
        } catch {
            /* clipboard unavailable (insecure context) */
        }
        return;
    }
    const el = e.target.closest("[data-action]");
    if (!el || el.disabled) return;
    try {
        await handlers[el.dataset.action]?.(el);
    } catch (err) {
        showNotice(err.message ?? String(err), "danger");
    }
});

content.addEventListener("input", (e) => handlers.input?.(e));
content.addEventListener("change", (e) => handlers.change?.(e));

function showNotice(message, kind = "success") {
    const el = $("notice");
    if (!el) return;
    el.className = "alert-" + kind;
    el.textContent = message;
    el.hidden = false;
}

const views = {
    overview: viewOverview,
    apps: (arg) => (arg ? viewApp(arg) : viewApps()),
    channels: (arg) => (arg ? viewChannel(arg) : viewChannels()),
    alerts: viewAlerts,
    audit: viewAudit,
};

async function route() {
    if (!token) return;
    const [section = "overview", rawArg] = (location.hash.replace(/^#\/?/, "") || "overview").split("/");
    const view = views[section] ?? views.overview;
    const arg = rawArg ? decodeURIComponent(rawArg) : null;

    document.querySelectorAll("[data-section]").forEach((a) => {
        a.classList.toggle("active", a.dataset.section === (views[section] ? section : "overview"));
    });

    const seq = ++renderSeq;
    content.innerHTML = skeleton;
    handlers = {};
    refreshAlertBadge();
    try {
        const result = await view(arg);
        if (seq !== renderSeq) return;
        content.innerHTML = result.html;
        content.querySelectorAll("[data-pct]").forEach((el) => (el.style.width = el.dataset.pct + "%"));
        handlers = result.handlers ?? {};
    } catch (e) {
        if (seq !== renderSeq) return;
        content.innerHTML = `<div class="alert-danger">${esc(e.message ?? e)}</div>`;
    }
}

window.addEventListener("hashchange", route);

async function refreshAlertBadge() {
    try {
        const m = await api("/admin/metrics");
        $("alertBadge").textContent = m.activeAlerts;
        $("alertBadge").hidden = m.activeAlerts === 0;
    } catch {
        /* the view itself reports API errors */
    }
}

const pageTitle = (title, actionsHtml = "") =>
    `<div class="page-title"><h1>${title}</h1><div class="actions">${actionsHtml}</div></div>`;

const notice = `<div id="notice" hidden></div>`;

// ── Overview ─────────────────────────────────────────────────────────────
function tile(label, value, hint = "") {
    return `<div class="card stat-tile"><div class="stat-label">${esc(label)}</div>
        <div class="stat-value">${value}</div>${hint ? `<div class="stat-hint">${hint}</div>` : ""}</div>`;
}

async function viewOverview() {
    const [m, alerts] = await Promise.all([api("/admin/metrics"), api("/admin/alerts?active=true")]);
    const alertRows = alerts
        .slice(0, 5)
        .map(
            (a) => `<div class="alert-warning alert-row">
                <span>${appLink(a.appId)} · ${esc(METRIC_LABELS[a.metric] ?? a.metric)} at
                    ${a.thresholdPercent}% (${fmtMetric(a.metric, a.observed)} of ${fmtMetric(a.metric, a.limit)})</span>
                <a href="#/apps/${encodeURIComponent(a.appId)}">Open</a></div>`,
        )
        .join("");
    return {
        html: `${pageTitle("Overview")}
        <div class="stack">
            <div class="grid grid-4">
                ${tile("Active connections", fmtNum(m.activeConnections))}
                ${tile("Apps", fmtNum(m.totalApps), [m.pausedApps ? `${m.pausedApps} paused` : "", m.deletedApps ? `${m.deletedApps} pending purge` : ""].filter(Boolean).join(" · "))}
                ${tile("Live channels", fmtNum(m.totalChannels))}
                ${tile("Active alerts", `<a href="#/alerts">${fmtNum(m.activeAlerts)}</a>`)}
            </div>
            ${alertRows ? `<div class="stack">${alertRows}</div>` : ""}
        </div>`,
    };
}

// ── Apps ─────────────────────────────────────────────────────────────────
let showDeletedApps = false;

async function viewApps() {
    const rows = await api("/admin/apps" + (showDeletedApps ? "?includeDeleted=true" : ""));
    rows.sort((a, b) => a.id.localeCompare(b.id));

    const body = rows.length
        ? `<div class="card card-flush table-scroll"><table class="table">
            <thead><tr><th>App</th><th>Owner</th><th>Storage</th><th>Created</th><th>Status</th></tr></thead>
            <tbody>${rows
                .map(
                    (a) => `<tr>
                <td>${appLink(a.id)}</td>
                <td><code class="muted" title="${esc(a.ownerClientId)}">${esc(shortKey(a.ownerClientId, 16))}</code></td>
                <td>${a.storageBytes == null ? '<span class="muted">—</span>' : fmtBytes(a.storageBytes)}</td>
                <td class="muted">${fmtDay(a.createdAt)}</td>
                <td class="row">
                    ${a.deletedAt ? '<span class="badge badge-danger">deleted</span>' : ""}
                    ${a.pausedAt && !a.deletedAt ? '<span class="badge badge-warning">paused</span>' : ""}
                    ${a.throttlePerMinute ? `<span class="badge badge-neutral">throttled ${fmtNum(a.throttlePerMinute)}/min</span>` : ""}
                    ${a.activeAlerts ? `<span class="badge badge-warning">${a.activeAlerts} alert${a.activeAlerts > 1 ? "s" : ""}</span>` : ""}
                    ${a.discoverable ? '<span class="badge badge-neutral">discoverable</span>' : ""}
                </td></tr>`,
                )
                .join("")}</tbody></table></div>`
        : `<div class="empty-state">No apps registered yet.</div>`;

    return {
        html: `${pageTitle(
            "Apps",
            `<label class="row small muted"><input type="checkbox" class="checkbox" data-role="show-deleted" ${showDeletedApps ? "checked" : ""}/> Show deleted</label>`,
        )}${body}`,
        handlers: {
            change(e) {
                if (e.target.dataset.role !== "show-deleted") return;
                showDeletedApps = e.target.checked;
                route();
            },
        },
    };
}

function meter(label, value, limit, fmt, warnPct) {
    if (limit == null)
        return `<div class="meter"><div class="meter-head"><span>${esc(label)}</span>
            <span class="muted">${fmt(value)} · no limit</span></div></div>`;
    const pct = limit > 0 ? ((value ?? 0) / limit) * 100 : 0;
    const cls = pct >= 100 ? "is-over" : pct >= warnPct ? "is-warn" : "";
    return `<div class="meter"><div class="meter-head"><span>${esc(label)}</span>
        <span class="muted">${fmt(value ?? 0)} of ${fmt(limit)} (${Math.round(pct)}%)</span></div>
        <div class="progress"><div class="progress-bar ${cls}" data-pct="${Math.min(100, pct).toFixed(1)}"></div></div></div>`;
}

function usageChart(rows, limit) {
    if (!rows.length) return `<p class="muted small">No usage history recorded yet.</p>`;
    const W = 600, H = 150, padL = 4, padR = 4, padT = 14, padB = 18;
    const max = Math.max(1, ...rows.map((r) => r.messages));
    const showLimit = limit != null && limit > 0 && limit <= max * 1.25;
    const top = showLimit ? Math.max(max, limit) : max;
    const slot = (W - padL - padR) / rows.length;
    const bw = Math.min(40, slot - 6);
    const plotH = H - padT - padB;
    const y = (v) => padT + plotH - (v / top) * plotH;

    const bars = rows
        .map((r, i) => {
            const x = padL + i * slot + (slot - bw) / 2;
            const h = Math.max(1, (r.messages / top) * plotH);
            const cls = i === rows.length - 1 ? "bar bar-current" : "bar";
            return `<rect class="${cls}" x="${x.toFixed(1)}" y="${(padT + plotH - h).toFixed(1)}" width="${bw.toFixed(1)}" height="${h.toFixed(1)}" rx="3">
                <title>${esc(r.periodStart.slice(0, 7))}: ${fmtNum(r.messages)} messages</title></rect>
                <text x="${(x + bw / 2).toFixed(1)}" y="${H - 4}" text-anchor="middle">${esc(r.periodStart.slice(2, 7))}</text>`;
        })
        .join("");
    const limitLine = showLimit
        ? `<line class="limit" x1="${padL}" x2="${W - padR}" y1="${y(limit).toFixed(1)}" y2="${y(limit).toFixed(1)}"/>`
        : "";
    return `<svg class="chart" viewBox="0 0 ${W} ${H}" role="img" aria-label="Messages per month">
        <text x="${padL}" y="9">${fmtNum(top)}</text>
        <line class="axis" x1="${padL}" x2="${W - padR}" y1="${padT + plotH}" y2="${padT + plotH}"/>
        ${limitLine}${bars}</svg>`;
}

const QUOTA_FIELDS = [
    ["maxPayloadBytes", "Max payload (bytes)"],
    ["publishRatePerMinute", "Publish rate / min / client"],
    ["maxChannels", "Max channels"],
    ["maxEventsPerChannel", "Max events / channel"],
    ["retentionDays", "Retention (days)"],
    ["totalStorageBytes", "Total storage (bytes)"],
    ["maxMessagesPerMonth", "Max messages / month"],
];

async function viewApp(id) {
    const enc = encodeURIComponent(id);
    const [app, usage, history, channels, alerts] = await Promise.all([
        api(`/admin/apps/${enc}`),
        api(`/admin/apps/${enc}/usage`),
        api(`/admin/apps/${enc}/usage/history?months=12`),
        api(`/admin/apps/${enc}/channels`),
        api(`/admin/apps/${enc}/alerts?active=true`),
    ]);
    config ??= await api("/admin/config");
    const q = app.quotas ?? {};
    const warnPct = Math.min(...(config.alertThresholdsPercent?.length ? config.alertThresholdsPercent : [80]));
    const liveStorage = channels.reduce((sum, c) => sum + c.payloadBytes, 0);
    const storage = usage.storageBytes ?? liveStorage;
    const deleted = !!app.deletedAt;
    const paused = !!app.pausedAt && !deleted;

    let pausedBanner = "";
    if (paused) {
        pausedBanner = `<div class="alert-warning">Publishing is paused since ${esc(fmtDate(app.pausedAt))}. Clients are refused with
            <code>APP_PAUSED</code> and keep unsent events queued; reads and subscriptions still work.</div>`;
    }
    let deletedBanner = "";
    if (deleted) {
        const purgeAt = new Date(new Date(app.deletedAt).getTime() + config.appDeletionGracePeriodSeconds * 1000);
        deletedBanner = `<div class="alert-warning">Deleted ${esc(fmtDate(app.deletedAt))}.
            ${
                config.appDeletionPrunerEnabled
                    ? `All data is permanently purged after ${esc(fmtDuration(config.appDeletionGracePeriodSeconds))}
                       (about ${esc(fmtDate(purgeAt))}). Restore it before then to keep it.`
                    : "Automatic purge is disabled on this relay, so the data is retained until you re-enable it."
            }</div>`;
    }

    const alertRows = alerts
        .map(
            (a) => `<div class="${a.thresholdPercent >= 100 ? "alert-danger" : "alert-warning"} alert-row">
            <span>${esc(METRIC_LABELS[a.metric] ?? a.metric)} reached ${a.thresholdPercent}%
                (${fmtMetric(a.metric, a.observed)} of ${fmtMetric(a.metric, a.limit)}) · raised ${esc(fmtDate(a.raisedAt))}
                ${a.acknowledgedAt ? '<span class="badge badge-neutral">acknowledged</span>' : ""}</span>
            ${a.acknowledgedAt ? "" : `<button class="btn btn-secondary btn-sm" data-action="ack" data-id="${a.id}">Acknowledge</button>`}</div>`,
        )
        .join("");

    const topChannels = channels.slice(0, 10);
    const channelTable = topChannels.length
        ? `<div class="table-scroll"><table class="table"><thead><tr><th>Channel</th><th>Events</th><th>Payload</th><th>Latest seq</th><th></th></tr></thead>
            <tbody>${topChannels
                .map(
                    (c) => `<tr><td>${channelLink(c.id)}</td><td>${fmtNum(c.eventCount)}</td>
                    <td>${fmtBytes(c.payloadBytes)}</td><td>${fmtNum(c.latestSequence)}</td>
                    <td>${c.deletedAt ? '<span class="badge badge-danger">deleted</span>' : ""}
                        ${c.visibility === "private" ? '<span class="badge badge-neutral">private</span>' : ""}</td></tr>`,
                )
                .join("")}</tbody></table></div>`
        : `<div class="card-body muted small">No channels yet.</div>`;

    const quotaInputs = QUOTA_FIELDS.map(
        ([key, label]) => `<div class="field"><label class="label" for="q_${key}">${esc(label)}</label>
            <input class="input" id="q_${key}" data-quota="${key}" type="number" min="0" step="1" placeholder="no limit" value="${q[key] ?? ""}" ${deleted ? "disabled" : ""}/></div>`,
    ).join("");

    const html = `
    <p class="small"><a href="#/apps">← Apps</a></p>
    ${pageTitle(
        `<span class="mono">${esc(app.id)}</span> ${deleted ? '<span class="badge badge-danger">deleted</span>' : ""}${paused ? ' <span class="badge badge-warning">paused</span>' : ""}`,
        `<button class="btn btn-secondary" data-action="export">Export data</button>
         ${deleted ? '<button class="btn btn-primary" data-action="restore">Restore app</button>' : ""}
         ${!deleted && !paused ? '<button class="btn btn-secondary" data-action="pause">Pause publishing</button>' : ""}
         ${paused ? '<button class="btn btn-primary" data-action="resume">Resume publishing</button>' : ""}`,
    )}
    <div class="stack">
        ${notice}
        ${deletedBanner}
        ${pausedBanner}
        ${alertRows ? `<div class="stack">${alertRows}</div>` : ""}

        <div class="grid grid-4">
            ${tile("Active connections", fmtNum(app.activeConnections), "subscribed right now")}
            ${tile("Messages this month", fmtNum(usage.messages ?? 0), `since ${esc(usage.periodStart)}`)}
            ${tile("Storage", fmtBytes(storage), usage.storageBytes == null ? "live estimate" : "")}
            ${tile("Channels", fmtNum(usage.channelCount))}
        </div>

        <div class="card"><div class="card-header">Quota utilisation</div><div class="card-body">
            ${meter("Messages this month", usage.messages ?? 0, q.maxMessagesPerMonth, fmtNum, warnPct)}
            ${meter("Storage", storage, q.totalStorageBytes, fmtBytes, warnPct)}
            ${meter("Channels", usage.channelCount, q.maxChannels, fmtNum, warnPct)}
        </div></div>

        <div class="card"><div class="card-header">Messages per month</div>
            <div class="card-body">${usageChart(history, q.maxMessagesPerMonth)}</div></div>

        <div class="card card-flush"><div class="card-header">Top channels
            <span class="xs muted">${channels.length} total</span></div>${channelTable}</div>

        <div class="card"><div class="card-header">Limits</div><div class="card-body">
            <p class="hint mb-4">Leave a field empty for no limit.</p>
            <div class="grid grid-2">${quotaInputs}</div>
            <div class="row"><button class="btn btn-primary" data-action="save-quotas" ${deleted ? "disabled" : ""}>Save limits</button></div>
        </div></div>

        <div class="card"><div class="card-header">Traffic control</div><div class="card-body">
            <div class="field"><span class="label">Publishing</span>
                <div class="row">${paused ? '<span class="badge badge-warning">paused</span>' : '<span class="badge badge-success">running</span>'}
                    ${deleted ? "" : paused
                        ? '<button class="btn btn-primary btn-sm" data-action="resume">Resume</button>'
                        : '<button class="btn btn-secondary btn-sm" data-action="pause">Pause</button>'}</div>
                <span class="hint">Pausing refuses every publish and channel creation with <code>APP_PAUSED</code>. Reads and subscriptions keep working, and SDKs keep unsent events queued until you resume.</span></div>
            <div class="field"><label class="label" for="throttleInput">Throttle (publishes per minute, all clients combined)</label>
                <div class="row"><input class="input input-md" id="throttleInput" type="number" min="1" step="1" placeholder="not throttled" value="${app.throttlePerMinute ?? ""}" ${deleted ? "disabled" : ""}/>
                <button class="btn btn-secondary" data-action="save-throttle" ${deleted ? "disabled" : ""}>Apply</button>
                ${app.throttlePerMinute ? `<button class="btn btn-ghost" data-action="clear-throttle" ${deleted ? "disabled" : ""}>Remove throttle</button>` : ""}</div>
                <span class="hint">Excess publishes get <code>RATE_LIMITED</code>, which SDKs retry later. Independent of the per-client limit above.</span></div>
        </div></div>

        <div class="card"><div class="card-header">Ownership &amp; discovery</div><div class="card-body">
            <div class="field"><span class="label">Owner client id</span>
                <div class="row"><code>${esc(app.ownerClientId)}</code>${copyBtn(app.ownerClientId)}</div></div>
            <div class="field"><label class="label" for="newOwner">Rebind owner</label>
                <div class="row"><input class="input input-md" id="newOwner" placeholder="new owner client id" ${deleted ? "disabled" : ""}/>
                <button class="btn btn-secondary" data-action="rebind-owner" ${deleted ? "disabled" : ""}>Rebind</button></div>
                <span class="hint">Use for key rotation or lost identities. The new owner gains full control of the app.</span></div>
            <label class="row small"><input type="checkbox" class="checkbox" data-role="discoverable" ${app.discoverable ? "checked" : ""} ${deleted ? "disabled" : ""}/>
                Advertise this app to other relays (discovery)</label>
        </div></div>

        ${
            deleted
                ? ""
                : `<div class="card card-danger"><div class="card-header">Danger zone</div><div class="card-body">
            <p class="small mb-4">Deleting blocks all publishing and subscribing immediately and disconnects every client
                currently subscribed to this app.
                ${
                    config.appDeletionPrunerEnabled
                        ? `Data is kept for ${esc(fmtDuration(config.appDeletionGracePeriodSeconds))} and can be restored; after that it is purged permanently.`
                        : "Automatic purge is disabled on this relay, so the data is kept until purge is enabled."
                }
                Consider exporting first.</p>
            <div class="field"><label class="label" for="confirmDelete">Type <kbd class="kbd">${esc(app.id)}</kbd> to confirm</label>
                <input class="input input-md" id="confirmDelete" autocomplete="off"/></div>
            <button class="btn btn-danger" id="deleteBtn" data-action="delete-app" disabled>Delete app</button>
        </div></div>`
        }
    </div>`;

    const reload = () => route();
    return {
        html,
        handlers: {
            input(e) {
                if (e.target.id === "confirmDelete") $("deleteBtn").disabled = e.target.value !== app.id;
            },
            async change(e) {
                if (e.target.dataset.role !== "discoverable") return;
                try {
                    await sendJson(`/admin/apps/${enc}/discoverable`, "PATCH", { discoverable: e.target.checked });
                    showNotice("Discovery setting saved.");
                } catch (err) {
                    e.target.checked = !e.target.checked;
                    showNotice(err.message, "danger");
                }
            },
            async "save-quotas"() {
                const body = {};
                for (const input of content.querySelectorAll("[data-quota]")) {
                    const raw = input.value.trim();
                    if (raw === "") {
                        body[input.dataset.quota] = null;
                        continue;
                    }
                    const n = Number(raw);
                    if (!Number.isInteger(n) || n < 0) throw new Error(`${input.dataset.quota} must be a non-negative whole number`);
                    body[input.dataset.quota] = n;
                }
                await sendJson(`/admin/apps/${enc}/quotas`, "PATCH", body);
                showNotice("Limits saved. Alerts re-evaluate on the next quota sweep.");
            },
            async "rebind-owner"() {
                const next = $("newOwner").value.trim();
                if (!next) throw new Error("Enter the new owner client id");
                if (!confirm(`Rebind owner of '${app.id}' to ${next}?`)) return;
                await sendJson(`/admin/apps/${enc}/owner`, "PATCH", { ownerClientId: next });
                reload();
            },
            async ack(el) {
                await api(`/admin/alerts/${el.dataset.id}/ack`, { method: "POST" });
                reload();
            },
            async export(el) {
                el.disabled = true;
                try {
                    await downloadExport(app.id);
                } finally {
                    el.disabled = false;
                }
            },
            async "delete-app"() {
                await api(`/admin/apps/${enc}`, { method: "DELETE" });
                location.hash = "#/apps";
            },
            async restore() {
                await api(`/admin/apps/${enc}/restore`, { method: "POST" });
                reload();
            },
            async pause() {
                await api(`/admin/apps/${enc}/pause`, { method: "POST" });
                reload();
            },
            async resume() {
                await api(`/admin/apps/${enc}/resume`, { method: "POST" });
                reload();
            },
            async "save-throttle"() {
                const raw = $("throttleInput").value.trim();
                const n = Number(raw);
                if (raw === "" || !Number.isInteger(n) || n < 1) throw new Error("Enter a whole number of publishes per minute, or use Remove throttle");
                await sendJson(`/admin/apps/${enc}/throttle`, "PATCH", { perMinute: n });
                reload();
            },
            async "clear-throttle"() {
                await sendJson(`/admin/apps/${enc}/throttle`, "PATCH", { perMinute: null });
                reload();
            },
        },
    };
}

async function downloadExport(appId) {
    const resp = await fetch(`/admin/apps/${encodeURIComponent(appId)}/export`, {
        headers: { authorization: "Bearer " + token },
    });
    if (resp.status === 401) {
        logout();
        throw new Error("Session expired");
    }
    if (!resp.ok) throw new Error("Export failed: " + resp.status);
    const disposition = resp.headers.get("content-disposition") ?? "";
    const name = /filename\*?=(?:UTF-8'')?"?([^";]+)/i.exec(disposition)?.[1] ?? `${appId}.jsonl`;
    const url = URL.createObjectURL(await resp.blob());
    const a = document.createElement("a");
    a.href = url;
    a.download = decodeURIComponent(name);
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
    showNotice("Export downloaded: " + name);
}

// ── Channels ─────────────────────────────────────────────────────────────
async function viewChannels() {
    const rows = await api("/admin/channels?includeDeleted=true");
    rows.sort((a, b) => a.id.localeCompare(b.id));

    const paint = (filter) =>
        rows
            .filter((r) => !filter || r.id.includes(filter))
            .map(
                (r) => `<tr><td>${channelLink(r.id)}</td>
            <td><span class="badge ${r.visibility === "private" ? "badge-ember" : "badge-neutral"}">${esc(r.visibility)}</span></td>
            <td class="muted">${fmtDate(r.createdAt)}</td>
            <td>${r.deletedAt ? '<span class="badge badge-danger">deleted</span>' : '<span class="muted">live</span>'}</td></tr>`,
            )
            .join("");

    return {
        html: `${pageTitle(`Channels <span class="muted small">(${rows.length})</span>`)}
        <div class="field"><input class="input input-sm" id="channelFilter" placeholder="Filter by id…"/></div>
        ${
            rows.length
                ? `<div class="card card-flush table-scroll"><table class="table">
                <thead><tr><th>Channel</th><th>Visibility</th><th>Created</th><th>State</th></tr></thead>
                <tbody id="channelRows">${paint("")}</tbody></table></div>`
                : '<div class="empty-state">No channels yet.</div>'
        }`,
        handlers: {
            input(e) {
                if (e.target.id === "channelFilter") $("channelRows").innerHTML = paint(e.target.value.trim());
            },
        },
    };
}

async function viewChannel(id) {
    const c = await api("/admin/channels/" + encodeURIComponent(id));
    const members = c.members.length
        ? `<table class="table"><thead><tr><th>Client id</th><th>Role</th></tr></thead><tbody>${c.members
              .map((m) => `<tr><td><code>${esc(m.clientId)}</code></td><td>${esc(m.role)}</td></tr>`)
              .join("")}</tbody></table>`
        : `<div class="card-body muted small">No explicit grants.</div>`;
    const appId = c.id.split("/")[0];
    return {
        html: `<p class="small"><a href="#/channels">← Channels</a> · app ${appLink(appId)}</p>
        ${pageTitle(
            `<span class="mono">${esc(c.id)}</span> ${c.deletedAt ? '<span class="badge badge-danger">deleted</span>' : ""}`,
            c.deletedAt ? "" : '<button class="btn btn-danger" data-action="delete-channel">Delete channel</button>',
        )}
        <div class="stack">${notice}
            <div class="grid grid-4">
                ${tile("Events", fmtNum(c.eventCount))}
                ${tile("Payload", fmtBytes(c.payloadBytes))}
                ${tile("Latest sequence", fmtNum(c.latestSequence))}
                ${tile("Visibility", esc(c.visibility))}
            </div>
            <div class="card card-flush"><div class="card-header">Members (${c.members.length})</div>${members}</div>
        </div>`,
        handlers: {
            async "delete-channel"() {
                if (!confirm(`Soft-delete channel '${c.id}'?`)) return;
                await api("/admin/channels/" + encodeURIComponent(c.id), { method: "DELETE" });
                route();
            },
        },
    };
}

// ── Alerts ───────────────────────────────────────────────────────────────
let showResolvedAlerts = false;

async function viewAlerts() {
    const rows = await api("/admin/alerts?active=" + (showResolvedAlerts ? "false" : "true"));
    const body = rows.length
        ? `<div class="card card-flush table-scroll"><table class="table">
            <thead><tr><th>App</th><th>Metric</th><th>Threshold</th><th>Usage</th><th>Raised</th><th>Status</th><th></th></tr></thead>
            <tbody>${rows
                .map(
                    (a) => `<tr><td>${appLink(a.appId)}</td><td>${esc(METRIC_LABELS[a.metric] ?? a.metric)}</td>
                <td>${a.thresholdPercent}%</td>
                <td>${fmtMetric(a.metric, a.observed)} of ${fmtMetric(a.metric, a.limit)}</td>
                <td class="muted">${fmtDate(a.raisedAt)}</td>
                <td>${
                    a.resolvedAt
                        ? '<span class="badge badge-success">resolved</span>'
                        : a.acknowledgedAt
                          ? '<span class="badge badge-neutral">acknowledged</span>'
                          : '<span class="badge badge-warning">open</span>'
                }</td>
                <td class="actions">${
                    a.resolvedAt || a.acknowledgedAt
                        ? ""
                        : `<button class="btn btn-secondary btn-sm" data-action="ack" data-id="${a.id}">Acknowledge</button>`
                }</td></tr>`,
                )
                .join("")}</tbody></table></div>`
        : `<div class="empty-state">No ${showResolvedAlerts ? "" : "active "}alerts.</div>`;

    return {
        html: `${pageTitle(
            "Alerts",
            `<label class="row small muted"><input type="checkbox" class="checkbox" data-role="show-resolved" ${showResolvedAlerts ? "checked" : ""}/> Include resolved</label>`,
        )}<div class="stack">${notice}${body}</div>`,
        handlers: {
            change(e) {
                if (e.target.dataset.role !== "show-resolved") return;
                showResolvedAlerts = e.target.checked;
                route();
            },
            async ack(el) {
                await api(`/admin/alerts/${el.dataset.id}/ack`, { method: "POST" });
                route();
            },
        },
    };
}

// ── Audit ────────────────────────────────────────────────────────────────
async function viewAudit() {
    const rows = await api("/admin/audit?limit=200");
    const body = rows.length
        ? `<div class="card card-flush table-scroll"><table class="table">
            <thead><tr><th>When</th><th>Admin</th><th>Action</th><th>Target</th><th>Details</th></tr></thead>
            <tbody>${rows
                .map((r) => {
                    const target = !r.target
                        ? ""
                        : r.action.startsWith("app.")
                          ? appLink(r.target)
                          : r.action.startsWith("channel.")
                            ? channelLink(r.target)
                            : `<code>${esc(r.target)}</code>`;
                    const details = r.details ? JSON.stringify(r.details) : "";
                    return `<tr><td class="muted">${fmtDate(r.at)}</td>
                    <td><code title="${esc(r.adminPublicKey)}">${esc(shortKey(r.adminPublicKey, 10))}</code></td>
                    <td><span class="badge badge-neutral">${esc(r.action)}</span></td><td>${target}</td>
                    <td><code class="muted truncate cell-clip" title="${esc(details)}">${esc(details)}</code></td></tr>`;
                })
                .join("")}</tbody></table></div>`
        : `<div class="empty-state">No audited actions yet.</div>`;
    return { html: `${pageTitle("Audit log")}<p class="small muted mb-4">Most recent 200 admin actions.</p>${body}` };
}

// ── Bootstrap ────────────────────────────────────────────────────────────
async function start() {
    showSignedIn(true);
    config = null;
    try {
        config = await api("/admin/config");
    } catch {
        /* surfaced by the first view if the session is bad */
    }
    if (!location.hash) location.hash = "#/overview";
    else route();
}

if (token && tokenExpires && new Date(tokenExpires) > new Date()) start();
else logout();
