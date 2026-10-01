"""Built-in relay picker tests: the rendered page and the loopback web server.

Mirrors tests/VestaClient.Tests/WebRelayPickerTests.cs and
clients/vesta-client-ts/tests/relay-picker.test.mjs.
"""

from __future__ import annotations

import http.client
import re
import unittest
from collections.abc import Callable

from vesta_client import (
    InMemoryRelayOverrideStore,
    RelayAdoptResult,
    RelayAttempt,
    RelayChoice,
    RelayDirectory,
    RelaysExhaustedInfo,
    VestaAppConfig,
    set_relay_picker_enabled,
)
from vesta_client.relay_picker import WebRelayPicker
from vesta_client.relay_picker_page import render_relay_picker_page
from vesta_client.relay_recovery import RelayAdoptOptions, RelayRecoverySnapshot

# Never open a browser from tests.
set_relay_picker_enabled(False)

CONFIG = VestaAppConfig("chess", "unused", ["wss://dead.example/ws"])


def _snapshot(**overrides: object) -> RelayRecoverySnapshot:
    base: dict[str, object] = {
        "phase": "exhausted",
        "tried_relays": [],
        "choices": [],
        "active_override": None,
        "background_retrying": True,
        "failed_passes": 3,
        "adopting": None,
        "failure_reason": None,
        "failed_relay": None,
        "failed_options": None,
    }
    base.update(overrides)
    return RelayRecoverySnapshot(**base)


# ── render_relay_picker_page ─────────────────────────────────────────────────


class RenderRelayPickerPageTests(unittest.TestCase):
    def test_escapes_relay_urls_reasons_and_namespace(self) -> None:
        html = render_relay_picker_page(
            _snapshot(
                tried_relays=[RelayAttempt("wss://<b>evil</b>/ws", 'a "quoted" <script>')],
                choices=[
                    RelayChoice(
                        url="wss://x.example/ws?a=1&b=2",
                        relay_public_key=None,
                        hosts_requested_app=True,
                        from_cache=False,
                        accepts_unregistered_apps=None,
                    )
                ],
                failed_options=RelayAdoptOptions('"><script>x</script>', False),
            ),
            CONFIG,
            "/tok",
            None,
        )
        self.assertNotIn("<script>", html)
        self.assertNotIn("<b>evil", html)
        self.assertIn("wss://x.example/ws?a=1&amp;b=2", html)

    def test_shows_namespace_input_prefilled_and_register_checkbox(self) -> None:
        html = render_relay_picker_page(_snapshot(), CONFIG, "/tok", None)
        self.assertRegex(html, r'name="appId" value="chess"')
        self.assertRegex(html, r'name="register" value="1"')
        self.assertRegex(html, r'action="/tok/adopt"')

    def test_flags_relays_that_do_not_advertise_app_and_open_relays(self) -> None:
        html = render_relay_picker_page(
            _snapshot(
                choices=[
                    RelayChoice(
                        url="wss://a.example/ws",
                        relay_public_key="k",
                        hosts_requested_app=False,
                        from_cache=True,
                        accepts_unregistered_apps=True,
                    )
                ]
            ),
            CONFIG,
            "/tok",
            None,
        )
        self.assertIn("does not advertise this app", html)
        self.assertIn("Open relay", html)
        self.assertIn("remembered, not confirmed", html)

    def test_prefills_failed_attempts_namespace_and_relay(self) -> None:
        html = render_relay_picker_page(
            _snapshot(
                phase="failed",
                failure_reason="APP_NOT_ALLOWED: nope",
                failed_relay="wss://retry.example/ws",
                failed_options=RelayAdoptOptions("chess-2", True),
            ),
            CONFIG,
            "/tok",
            None,
        )
        self.assertRegex(html, r'name="appId" value="chess-2"')
        self.assertIn("wss://retry.example/ws", html)
        self.assertIn("APP_NOT_ALLOWED", html)

    def test_connected_page_offers_switch_back_when_override_active(self) -> None:
        from vesta_client import RelayOverride

        html = render_relay_picker_page(
            _snapshot(phase="healthy", active_override=RelayOverride(relay="wss://mine.example/ws")),
            CONFIG,
            "/tok",
            "wss://mine.example/ws",
        )
        self.assertIn("Connected", html)
        self.assertIn("/tok/clear", html)


# ── WebRelayPicker (loopback server) ─────────────────────────────────────────


class FakeHost:
    def __init__(self) -> None:
        self.relay_directory = RelayDirectory(CONFIG, InMemoryRelayOverrideStore())
        self.relays = ["wss://dead.example/ws"]
        self.auto_reconnect = True
        self.adopted: list[object] = []
        self._listeners: dict[str, list[Callable[..., None]]] = {}

    @property
    def active_relay(self) -> str:
        return "wss://dead.example/ws"

    def add_listener(self, event: str, listener: Callable[..., None]) -> Callable[[], None]:
        self._listeners.setdefault(event, []).append(listener)
        return lambda: self._listeners[event].remove(listener)

    async def reconnect(self) -> bool:
        return False

    async def adopt_relay(self, override: object) -> RelayAdoptResult:
        self.adopted.append(override)
        return RelayAdoptResult(connected=True, failure_reason=None)

    async def clear_relay_override(self) -> bool:
        return True


async def _start_picker() -> tuple[FakeHost, WebRelayPicker, list[str]]:
    host = FakeHost()
    launched: list[str] = []

    def launch(url: str) -> bool:
        launched.append(url)
        return True

    picker = WebRelayPicker(host, CONFIG, launch)
    await picker.show(RelaysExhaustedInfo([RelayAttempt("wss://dead.example/ws", "refused")], 3))
    return host, picker, launched


def _request(
    url: str,
    method: str = "GET",
    headers: dict[str, str] | None = None,
    body: str | None = None,
) -> tuple[int, dict[str, str], str]:
    from urllib.parse import urlsplit

    target = urlsplit(url)
    conn = http.client.HTTPConnection(target.hostname, target.port)
    try:
        send_headers: dict[str, str] = dict(headers or {})
        if body is not None:
            send_headers.setdefault("Content-Length", str(len(body.encode("utf-8"))))
        conn.request(method, target.path or "/", body=body, headers=send_headers)
        res = conn.getresponse()
        data = res.read().decode("utf-8")
        resp_headers = {k.lower(): v for k, v in res.getheaders()}
        return res.status, resp_headers, data
    finally:
        conn.close()


FORM = {"Content-Type": "application/x-www-form-urlencoded"}


class WebRelayPickerTests(unittest.IsolatedAsyncioTestCase):
    async def test_serves_page_on_loopback_under_secret_token_and_launches_once_per_outage(self) -> None:
        host, picker, launched = await _start_picker()
        try:
            self.assertRegex(picker.url, r"^http://127\.0\.0\.1:\d+/[0-9a-f]{32}/$")
            self.assertEqual(launched, [picker.url])

            status, headers, body = _request(picker.url)
            self.assertEqual(status, 200)
            self.assertIn("<html", body)
            self.assertRegex(headers["content-security-policy"], r"default-src 'none'")

            # A second exhaustion in the same outage does not relaunch.
            await picker.show(RelaysExhaustedInfo([], 4))
            self.assertEqual(len(launched), 1)
        finally:
            picker.dispose()

    async def test_rejects_foreign_host_header_and_wrong_token(self) -> None:
        host, picker, _launched = await _start_picker()
        try:
            status, _headers, _body = _request(picker.url, headers={"Host": "evil.example"})
            self.assertEqual(status, 400)

            bad_token_url = re.sub(r"[0-9a-f]{32}", "0" * 32, picker.url)
            status, _headers, _body = _request(bad_token_url)
            self.assertEqual(status, 404)
        finally:
            picker.dispose()

    async def test_actions_require_post(self) -> None:
        host, picker, _launched = await _start_picker()
        try:
            status, _headers, _body = _request(f"{picker.url}adopt")
            self.assertEqual(status, 405)
            status, _headers, _body = _request(picker.url, method="POST", headers=FORM, body="")
            self.assertEqual(status, 405)
        finally:
            picker.dispose()

    async def test_adopt_form_forwards_relay_namespace_and_register_flag_then_redirects(self) -> None:
        host, picker, _launched = await _start_picker()
        try:
            status, headers, _body = _request(
                f"{picker.url}adopt",
                method="POST",
                headers=FORM,
                body="relay=manual&manual=wss%3A%2F%2Fnew.example%2Fws&appId=chess-2&register=1",
            )
            self.assertEqual(status, 303)
            self.assertTrue(headers["location"].endswith("/"))

            for _ in range(50):
                if host.adopted:
                    break
                import asyncio

                await asyncio.sleep(0.02)
            self.assertEqual(len(host.adopted), 1)
            self.assertEqual(host.adopted[0].relay, "wss://new.example/ws")
            self.assertEqual(host.adopted[0].app_id, "chess-2")
            self.assertEqual(host.adopted[0].register_app, True)
        finally:
            picker.dispose()

    async def test_rejects_oversized_and_non_form_bodies(self) -> None:
        host, picker, _launched = await _start_picker()
        try:
            status, _headers, _body = _request(
                f"{picker.url}adopt",
                method="POST",
                headers=FORM,
                body="manual=" + "a" * (9 * 1024),
            )
            self.assertEqual(status, 400)

            status, _headers, _body = _request(
                f"{picker.url}adopt",
                method="POST",
                headers={"Content-Type": "application/json"},
                body='{"relay":"wss://x.example/ws"}',
            )
            self.assertEqual(status, 400)
            self.assertEqual(host.adopted, [])
        finally:
            picker.dispose()

    async def test_dispose_stops_server(self) -> None:
        _host, picker, _launched = await _start_picker()
        url = picker.url
        picker.dispose()
        with self.assertRaises(OSError):
            _request(url)
        self.assertIsNone(picker.url)


if __name__ == "__main__":
    unittest.main()
