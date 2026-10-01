"""
Built-in relay-recovery UI: a loopback-only web page that opens in the user's browser when a
connection has exhausted every relay. It is an integral part of ``VestaConnection``, not something
an app wires up. The page is served from 127.0.0.1 on a random port under a secret per-launch
token, carries no scripts, and drives a :class:`RelayRecoverySession`; it never adopts a relay
without an explicit form submit.
"""

from __future__ import annotations

import asyncio
import secrets
import sys
import threading
import webbrowser
from collections.abc import Callable
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any
from urllib.parse import parse_qs, urlsplit

from vesta_client.relay import RelaysExhaustedInfo, VestaAppConfig
from vesta_client.relay_picker_page import MANUAL_CHOICE, render_relay_picker_page
from vesta_client.relay_recovery import (
    HEALTHY,
    RelayAdoptOptions,
    RelayRecoveryHost,
    RelayRecoverySession,
    RelayRecoverySnapshot,
)

_MAX_BODY_BYTES: int = 8 * 1024
_ACTIONS: tuple[str, ...] = ("adopt", "discover", "retry", "clear", "dismiss")

SECURITY_HEADERS: dict[str, str] = {
    "Cache-Control": "no-store",
    "Referrer-Policy": "no-referrer",
    "X-Content-Type-Options": "nosniff",
    "X-Frame-Options": "DENY",
    "Content-Security-Policy": (
        "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'"
    ),
}

PickerLauncher = Callable[[str], bool]


def open_system_browser(url: str) -> bool:
    """Open a URL in the user's default browser; on failure print it so the user can open it by hand."""
    try:
        if webbrowser.open(url):
            return True
    except Exception:
        pass
    print(f"Vesta: could not open a browser. Open {url} to choose another relay.", file=sys.stderr)
    return False


def _fire_and_forget(loop: asyncio.AbstractEventLoop, coro: Any) -> None:
    async def _runner() -> None:
        try:
            await coro
        except Exception:
            pass

    asyncio.run_coroutine_threadsafe(_runner(), loop)


class WebRelayPicker:
    """Starts a loopback HTTP server on demand and opens it in the user's browser, once per outage."""

    def __init__(
        self,
        host: RelayRecoveryHost,
        config: VestaAppConfig,
        launch: PickerLauncher | None = None,
        session: RelayRecoverySession | None = None,
    ) -> None:
        self._host: RelayRecoveryHost = host
        self._config: VestaAppConfig = config
        self._launch: PickerLauncher = launch or open_system_browser
        self.session: RelayRecoverySession = session or RelayRecoverySession(host)
        self._unsubscribe: Callable[[], None] = self.session.on_change(self._on_snapshot)
        self._token: str = ""
        self._server: ThreadingHTTPServer | None = None
        self._port: int = 0
        self._launched_for_outage: bool = False
        self._disposed: bool = False
        self._loop: asyncio.AbstractEventLoop | None = None
        self._start_lock = threading.Lock()

    def _on_snapshot(self, snapshot: RelayRecoverySnapshot) -> None:
        if snapshot.phase == HEALTHY:
            self._launched_for_outage = False

    @property
    def url(self) -> str | None:
        """The page's address once the server has started, otherwise None."""
        return f"http://127.0.0.1:{self._port}/{self._token}/" if self._server else None

    async def show(self, info: RelaysExhaustedInfo) -> bool:
        """
        Show the prompt for an exhausted outage: start the server if needed, open the browser once
        per outage, and begin discovering relays. Safe to call repeatedly. Returns True if a user
        can reach the page (browser opened, or already open for this outage).
        """
        if self._disposed:
            return False
        self._loop = asyncio.get_running_loop()
        self._ensure_started()
        if self._disposed:
            return False

        if self._launched_for_outage:
            return True
        self._launched_for_outage = True

        self.session.report_exhausted(info)
        discover_task: asyncio.Task = asyncio.create_task(self.session.discover())
        discover_task.add_done_callback(lambda t: t.exception())
        try:
            return self._launch(self.url)  # type: ignore[arg-type]
        except Exception:
            return False

    def dispose(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        self._unsubscribe()
        if self._server is not None:
            self._server.shutdown()
            self._server.server_close()
            self._server = None
        self.session.dispose()

    def _ensure_started(self) -> None:
        with self._start_lock:
            if self._server is not None:
                return
            self._token = secrets.token_hex(16)
            server = ThreadingHTTPServer(("127.0.0.1", 0), _make_handler(self))
            server.daemon_threads = True
            self._port = server.server_address[1]
            self._server = server
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()


def _make_handler(picker: WebRelayPicker) -> type[BaseHTTPRequestHandler]:
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, format: str, *args: Any) -> None:  # noqa: A002
            pass

        def _respond(
            self, status: int, body: str, content_type: str = "text/plain; charset=utf-8"
        ) -> None:
            data: bytes = body.encode("utf-8")
            self.send_response(status)
            for key, value in SECURITY_HEADERS.items():
                self.send_header(key, value)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def _redirect(self, location: str) -> None:
            self.send_response(303)
            for key, value in SECURITY_HEADERS.items():
                self.send_header(key, value)
            self.send_header("Location", location)
            self.send_header("Content-Length", "0")
            self.end_headers()

        def _read_form(self) -> dict[str, str] | None:
            try:
                length: int = int(self.headers.get("Content-Length") or "0")
            except ValueError:
                return None
            content_type: str = (self.headers.get("Content-Type") or "").lower()
            if length > _MAX_BODY_BYTES:
                return None
            if length > 0 and not content_type.startswith("application/x-www-form-urlencoded"):
                return None
            body: bytes = self.rfile.read(length) if length > 0 else b""
            parsed: dict[str, list[str]] = parse_qs(body.decode("utf-8"), keep_blank_values=True)
            return {key: values[0] for key, values in parsed.items()}

        def _dispatch_action(self, action: str, form: dict[str, str]) -> None:
            loop: asyncio.AbstractEventLoop | None = picker._loop
            if loop is None:
                return
            if action == "adopt":
                choice: str = form.get("relay", "")
                relay: str = form.get("manual", "") if choice == MANUAL_CHOICE else choice
                options = RelayAdoptOptions(
                    app_id=form.get("appId", ""), register_app=form.get("register") == "1"
                )
                _fire_and_forget(loop, picker.session.use_manual(relay, options))
            elif action == "discover":
                _fire_and_forget(loop, picker.session.discover())
            elif action == "retry":
                _fire_and_forget(loop, picker.session.retry())
            elif action == "clear":
                _fire_and_forget(loop, picker.session.clear_override())
            elif action == "dismiss":
                loop.call_soon_threadsafe(picker.session.dismiss)

        def _handle(self, method: str) -> None:
            # DNS-rebinding guard: only the literal loopback origin is served.
            if self.headers.get("Host") != f"127.0.0.1:{picker._port}":
                self._respond(400, "Bad host")
                return

            base_path: str = f"/{picker._token}"
            path: str = urlsplit(self.path).path
            if path != base_path and not path.startswith(f"{base_path}/"):
                self._respond(404, "Not found")
                return

            action: str = path[len(base_path):].strip("/")
            if action == "":
                if method not in ("GET", "HEAD"):
                    self._respond(405, "Method not allowed")
                    return
                html: str = render_relay_picker_page(
                    picker.session.snapshot, picker._config, base_path, picker._host.active_relay
                )
                self._respond(200, html, "text/html; charset=utf-8")
                return

            if action not in _ACTIONS:
                self._respond(404, "Not found")
                return
            if method != "POST":
                self._respond(405, "Method not allowed")
                return

            form: dict[str, str] | None = self._read_form()
            if form is None:
                self._respond(400, "Bad request")
                return

            self._dispatch_action(action, form)
            self._redirect(f"{base_path}/")

        def do_GET(self) -> None:  # noqa: N802
            self._handle("GET")

        def do_HEAD(self) -> None:  # noqa: N802
            self._handle("HEAD")

        def do_POST(self) -> None:  # noqa: N802
            self._handle("POST")

    return Handler
