"""
Renders a :class:`RelayRecoverySnapshot` as the self-contained relay-picker web page (no scripts,
no external assets). Mirrors the C# ``RelayPickerPage`` and the TypeScript ``relay-picker-page.ts``.
"""

from __future__ import annotations

from vesta_client.relay import VestaAppConfig
from vesta_client.relay_recovery import (
    ADOPTING,
    DEGRADED,
    DISCOVERING,
    HEALTHY,
    NOTHING_FOUND,
    RelayChoice,
    RelayRecoverySnapshot,
)

MANUAL_CHOICE = "manual"

_STYLE: str = (
    "body{font:15px/1.5 system-ui,sans-serif;max-width:42rem;margin:2rem auto;padding:0 1rem;color:#1d1d1f;background:#fafafa}"
    "h1{font-size:1.4rem;margin-bottom:.25rem}h2{font-size:1.05rem;margin-top:2rem}"
    ".muted{color:#6b6b70}.alert{padding:.6rem .8rem;border-radius:6px;margin:1rem 0}"
    ".err{background:#fdecea;border:1px solid #f5c2bd}.ok{background:#e8f5e9;border:1px solid #b7dfb9}.warn{background:#fff6e0;border:1px solid #f0d99b}"
    ".relay{display:block;border:1px solid #d6d6db;border-radius:6px;padding:.6rem .8rem;margin:.4rem 0;background:#fff}"
    ".badge{display:inline-block;font-size:.75rem;border-radius:999px;padding:0 .5rem;margin-left:.3rem;background:#ececf0}"
    ".badge.good{background:#dff3e1}.badge.open{background:#dbeafe}.badge.bad{background:#fde7d7}"
    "input[type=text]{width:100%;box-sizing:border-box;padding:.4rem;margin:.25rem 0}"
    "button{font:inherit;padding:.45rem .9rem;margin:.25rem .25rem .25rem 0;cursor:pointer}"
    "code{background:#ececf0;padding:0 .25rem;border-radius:3px}ul{padding-left:1.2rem}"
)


def escape_html(value: str) -> str:
    return (
        value.replace("&", "&amp;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
        .replace('"', "&quot;")
        .replace("'", "&#39;")
    )


def render_relay_picker_page(
    snapshot: RelayRecoverySnapshot,
    config: VestaAppConfig,
    base_path: str,
    active_relay: str | None,
) -> str:
    transient: bool = snapshot.phase in (DISCOVERING, ADOPTING)
    html: str = '<!doctype html><html lang="en"><head><meta charset="utf-8">'
    html += '<meta name="viewport" content="width=device-width, initial-scale=1">'
    html += "<title>Vesta - relay unavailable</title>"
    if transient:
        html += '<meta http-equiv="refresh" content="2">'
    html += f"<style>{_STYLE}</style></head><body>"
    html += f'<p class="muted">Vesta &middot; app <code>{escape_html(config.app_id)}</code></p>'

    if snapshot.phase == HEALTHY:
        html += _render_connected(snapshot, base_path, active_relay)
    elif snapshot.phase == DISCOVERING:
        html += (
            '<h1>Looking for relays&hellip;</h1><p class="muted">Asking known relays which servers host this app.</p>'
        )
    elif snapshot.phase == ADOPTING:
        html += (
            f'<h1>Connecting&hellip;</h1><p class="muted">Trying <code>{escape_html(snapshot.adopting or "the relay")}</code>. '
            "The choice is only kept once the relay accepts the app.</p>"
        )
    else:
        html += _render_prompt(snapshot, config, base_path)
    return html + "</body></html>"


def _render_connected(snapshot: RelayRecoverySnapshot, base_path: str, active_relay: str | None) -> str:
    html: str = '<h1>Connected</h1><div class="alert ok">The app is connected'
    if active_relay:
        html += f" to <code>{escape_html(active_relay)}</code>"
    html += ". You can close this tab.</div>"
    if snapshot.active_override is not None:
        html += (
            '<p class="muted">You are using a relay you chose yourself. The app owner\'s own relay list is '
            "ignored until you switch back.</p>"
        )
        html += _post_button(base_path, "clear", "Switch back to the app's own relays")
    return html


def _render_prompt(snapshot: RelayRecoverySnapshot, config: VestaAppConfig, base_path: str) -> str:
    dismissed: bool = snapshot.phase == DEGRADED
    if dismissed:
        html: str = (
            '<h1>Connection lost</h1><p class="muted">Retrying in the background. '
            "You can also pick another relay below.</p>"
        )
    else:
        html = (
            "<h1>Can't reach any relay</h1><p class=\"muted\">Every relay this app knows about is unavailable. "
            "Vesta keeps retrying in the background.</p>"
        )

    if snapshot.failure_reason:
        html += f'<div class="alert err">{escape_html(snapshot.failure_reason)}</div>'

    if snapshot.tried_relays:
        html += "<h2>Tried</h2><ul>"
        for attempt in snapshot.tried_relays:
            html += (
                f'<li><code>{escape_html(attempt.relay)}</code> '
                f'<span class="muted">{escape_html(attempt.reason)}</span></li>'
            )
        html += "</ul>"

    html += "<h2>Use another relay</h2>"
    if snapshot.phase == NOTHING_FOUND:
        html += '<p class="muted">No other relays were found. You can still enter one by hand.</p>'

    app_id: str = (
        snapshot.failed_options.app_id
        if snapshot.failed_options is not None and snapshot.failed_options.app_id is not None
        else snapshot.active_override.app_id
        if snapshot.active_override is not None and snapshot.active_override.app_id is not None
        else config.app_id
    )
    register: bool = (
        snapshot.failed_options.register_app
        if snapshot.failed_options is not None
        else snapshot.active_override.register_app
        if snapshot.active_override is not None
        else False
    )
    manual: str = snapshot.failed_relay or ""
    manual_checked: bool = snapshot.failed_relay is not None or not snapshot.choices

    html += f'<form method="post" action="{escape_html(base_path)}/adopt">'
    for i, choice in enumerate(snapshot.choices):
        is_checked: bool = not manual_checked and i == 0
        html += (
            f'<label class="relay"><input type="radio" name="relay" value="{escape_html(choice.url)}"'
            f'{" checked" if is_checked else ""}> '
            f"<code>{escape_html(choice.url)}</code>{_badges(choice)}</label>"
        )

    html += (
        f'<label class="relay"><input type="radio" name="relay" value="{MANUAL_CHOICE}"'
        f'{" checked" if manual_checked else ""}> Enter a relay address'
        f'<input type="text" name="manual" placeholder="wss://relay.example.com" value="{escape_html(manual)}"></label>'
    )

    html += "<h2>App settings on that relay</h2>"
    html += f'<label>App namespace<input type="text" name="appId" value="{escape_html(app_id)}"></label>'
    html += (
        f'<p class="muted">Channels are stored under this namespace on the relay. Keep '
        f"<code>{escape_html(config.app_id)}</code> "
        "unless the relay already uses a different name for this app.</p>"
    )
    html += (
        f'<label><input type="checkbox" name="register" value="1"{" checked" if register else ""}> '
        "Register app on connect</label>"
    )
    html += (
        '<p class="muted">Needed when the relay does not accept unregistered apps and has not seen this namespace yet. '
        "A relay that does not advertise this app may not hold your data; nothing is changed on the original relay.</p>"
    )
    html += '<button type="submit">Use this relay</button></form>'

    html += "<h2>Other options</h2>"
    html += _post_button(base_path, "discover", "Find relays")
    html += _post_button(base_path, "retry", "Retry now")
    if snapshot.active_override is not None:
        html += _post_button(base_path, "clear", "Switch back to the app's own relays")
    if not dismissed:
        html += _post_button(base_path, "dismiss", "Keep retrying in the background")
    return html


def _badges(choice: RelayChoice) -> str:
    html: str = (
        '<span class="badge good">hosts this app</span>'
        if choice.hosts_requested_app
        else '<span class="badge bad">does not advertise this app</span>'
    )
    if choice.accepts_unregistered_apps is True:
        html += '<span class="badge open">Open relay</span>'
    elif choice.accepts_unregistered_apps is False:
        html += '<span class="badge">registered apps only</span>'
    if choice.from_cache:
        html += '<span class="badge">remembered, not confirmed</span>'
    return html


def _post_button(base_path: str, action: str, label: str) -> str:
    return (
        f'<form method="post" action="{escape_html(base_path)}/{action}" style="display:inline">'
        f'<button type="submit">{escape_html(label)}</button></form>'
    )
