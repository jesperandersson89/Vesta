#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Vesta Collaborative Editor
───────────────────────────
A tkinter GUI where multiple users edit the same text document in real time.
Uses last-writer-wins on the full document text, with debounced publishing.

Event schema:
  Channel:   {appId}/{room}   (appId defaults to "collab-edit")
  Event:     app.collab.document-update
  Payload:   { "text": "...", "username": "...", "cursorPos": 42 }
  Replace:   true (server keeps only the latest version per user)

Conflict model:
  LWW on the full document. When a remote update arrives with a newer
  timestamp, we replace the local text and try to preserve cursor position.
  During active local typing (within the debounce window), remote updates
  are deferred to avoid fighting with the user's input.

Run:  python main.py [ws://host:port/ws] [room-name]
Env:  VESTA_RELAY_URL, VESTA_APP_ID, VESTA_IDENTITY_FILE (for Atrium-managed relays)

Local cache: ~/.vesta/collab-edit-{room}-{username}-cache.db (offline outbox + event cache)
Snapshot:    ~/.vesta/collab-edit-{room}-{username}-snapshots.db (LwwRegister projection snapshot)
"""

import asyncio
import queue
import sys
import threading
import tkinter as tk
from datetime import datetime, timezone
from pathlib import Path
from tkinter import scrolledtext

from vesta_client import (
    LwwRegister,
    SequencedEvent,
    VestaConnection,
    VestaEvent,
    VestaIdentity,
    VestaLimitNotice,
    create_event,
    load_or_create_identity,
)
from vesta_client.projection_store import SqliteProjectionStore, restore_projection, save_projection
from vesta_client.storage import SqliteClientEventStore

# ── Configuration ────────────────────────────────────────────────────────────
DEFAULT_SERVER = "ws://localhost:5150/ws"
DEFAULT_ROOM = "main"
VESTA_DIR = Path.home() / ".vesta"

DEBOUNCE_MS = 150          # Publish after 150ms of no typing
DEFER_REMOTE_MS = 300      # Ignore remote updates while actively typing

# ── Theme ─────────────────────────────────────────────────────────────────────
BG = "#1e1e1e"
BG_CARD = "#252526"
BG_EDITOR = "#1e1e1e"
FG = "#d4d4d4"
FG_DIM = "#666666"
ACCENT = "#0e639c"
CURSOR_COLOR = "#aeafad"


# ── Document State ────────────────────────────────────────────────────────────
def document_projector(event: VestaEvent) -> dict | None:
    """Single shared document: last-writer-wins on the full text (see LwwRegister)."""
    if event.event_type != "app.collab.document-update":
        return None
    payload = event.payload or {}
    return {
        "text": payload.get("text", ""),
        "username": payload.get("username", ""),
        "cursorPos": payload.get("cursorPos", 0),
    }


# ── GUI ───────────────────────────────────────────────────────────────────────
class App:
    def __init__(
        self,
        root: tk.Tk,
        username: str,
        identity: VestaIdentity,
        server_url: str,
        channel: str,
        state: "LwwRegister[dict]",
    ):
        self.root = root
        self.username = username
        self.identity = identity
        self.client_id = identity.client_id
        self.server_url = server_url
        self.channel = channel

        self.state = state
        self.incoming: queue.Queue = queue.Queue()
        self.publish_cb = None
        self._connected = False
        self._debounce_id = None
        self._last_local_edit_ms = 0
        self._applying_remote = False  # Guard against recursive change events

        root.title(f"Vesta Collab Edit — {username}")
        root.configure(bg=BG)
        root.geometry("700x500")
        root.minsize(500, 300)

        self._build_ui()
        restored = self.state.state
        if restored and restored.get("text"):
            self.editor.insert("1.0", restored["text"])
            self.editor.edit_modified(False)
            self.chars_var.set(f"{len(restored['text'])} chars")
            self.author_var.set(f"Last edit: {restored.get('username', '?')}")
        self._poll_queue()

    def _build_ui(self):
        # ── Header ────────────────────────────────────────────────────────────
        header = tk.Frame(self.root, bg=BG)
        header.pack(fill="x", padx=12, pady=(10, 0))

        tk.Label(header, text="Collaborative Editor", bg=BG, fg=FG,
                 font=("Segoe UI", 12, "bold")).pack(side="left")

        self.status_var = tk.StringVar(value="○  Connecting…")
        tk.Label(header, textvariable=self.status_var, bg=BG, fg=FG_DIM,
                 font=("Segoe UI", 8)).pack(side="right")

        # ── Info bar ──────────────────────────────────────────────────────────
        info_bar = tk.Frame(self.root, bg=BG)
        info_bar.pack(fill="x", padx=12, pady=(4, 0))

        self.author_var = tk.StringVar(value="")
        tk.Label(info_bar, textvariable=self.author_var, bg=BG, fg=FG_DIM,
                 font=("Segoe UI", 8)).pack(side="left")

        self.chars_var = tk.StringVar(value="0 chars")
        tk.Label(info_bar, textvariable=self.chars_var, bg=BG, fg=FG_DIM,
                 font=("Segoe UI", 8)).pack(side="right")

        self.limit_var = tk.StringVar(value="")
        tk.Label(info_bar, textvariable=self.limit_var, bg=BG, fg="#e66",
                 font=("Segoe UI", 8)).pack(side="right", padx=(0, 10))

        # ── Editor ────────────────────────────────────────────────────────────
        editor_frame = tk.Frame(self.root, bg="#333333", bd=0)
        editor_frame.pack(fill="both", expand=True, padx=12, pady=8)

        self.editor = scrolledtext.ScrolledText(
            editor_frame,
            wrap="word",
            font=("Consolas", 11),
            bg=BG_EDITOR,
            fg=FG,
            insertbackground=CURSOR_COLOR,
            selectbackground=ACCENT,
            selectforeground="white",
            relief="flat",
            bd=8,
            undo=True,
        )
        self.editor.pack(fill="both", expand=True)
        self.editor.bind("<<Modified>>", self._on_modified)
        self.editor.bind("<Key>", self._on_keypress)

    # ── Edit handling ─────────────────────────────────────────────────────────
    def _on_keypress(self, event):
        """Track that the user is actively typing."""
        # Ignore modifier-only keys
        if event.keysym in ("Shift_L", "Shift_R", "Control_L", "Control_R",
                            "Alt_L", "Alt_R", "Caps_Lock"):
            return
        self._last_local_edit_ms = self._now_ms()

    def _on_modified(self, event=None):
        """Called when the Text widget content changes."""
        if self._applying_remote:
            return
        if not self.editor.edit_modified():
            return
        self.editor.edit_modified(False)

        # Update char count
        text = self.editor.get("1.0", "end-1c")
        self.chars_var.set(f"{len(text)} chars")

        # Debounce: schedule publish after DEBOUNCE_MS of inactivity
        if self._debounce_id is not None:
            self.root.after_cancel(self._debounce_id)
        self._debounce_id = self.root.after(DEBOUNCE_MS, self._publish_local)

    def _publish_local(self):
        """Publish the current document text to the server."""
        self._debounce_id = None
        text = self.editor.get("1.0", "end-1c")

        # Update local state so we don't accept older remote versions
        evt = create_event(
            self.channel,
            self.client_id,
            "app.collab.document-update",
            {"text": text, "username": self.username, "cursorPos": 0},
            replace=True,
        )
        self.state.apply_local(evt)
        self.author_var.set(f"Last edit: {self.username} (you)")

        if self.publish_cb:
            cursor_pos = self._get_cursor_offset()
            self.publish_cb(text, cursor_pos)

    def _get_cursor_offset(self) -> int:
        """Get cursor position as character offset from start."""
        pos = self.editor.index("insert")
        line, col = pos.split(".")
        # Count characters up to cursor
        text_before = self.editor.get("1.0", pos)
        return len(text_before)

    # ── Remote update application ─────────────────────────────────────────────
    def _apply_remote_update(self, sequenced: SequencedEvent):
        """Apply a remote document update, preserving cursor position."""
        # Don't apply while user is actively typing
        elapsed = self._now_ms() - self._last_local_edit_ms
        if elapsed < DEFER_REMOTE_MS:
            # Re-check shortly
            self.root.after(DEFER_REMOTE_MS - elapsed + 10,
                            lambda: self._apply_remote_update(sequenced))
            return

        before = self.state.state
        self.state.apply(sequenced)
        after = self.state.state
        if after is before:
            return  # stale update, ignored by LwwRegister

        new_text = after["text"] if after else ""
        author = after["username"] if after else "?"
        self.author_var.set(f"Last edit: {author}")

        # Save cursor position
        current_text = self.editor.get("1.0", "end-1c")
        if current_text == new_text:
            return  # No visual change needed

        cursor_pos = self.editor.index("insert")

        # Replace text without triggering our own change handler
        self._applying_remote = True
        self.editor.delete("1.0", "end")
        self.editor.insert("1.0", new_text)
        self.editor.edit_modified(False)
        self._applying_remote = False

        # Restore cursor (best effort)
        try:
            self.editor.mark_set("insert", cursor_pos)
            self.editor.see("insert")
        except tk.TclError:
            pass

        self.chars_var.set(f"{len(new_text)} chars")

    # ── Queue polling ─────────────────────────────────────────────────────────
    def _poll_queue(self):
        try:
            while True:
                item = self.incoming.get_nowait()
                kind = item["kind"]

                if kind == "event":
                    sequenced: SequencedEvent = item["event"]
                    if sequenced.event.client_id != self.client_id:
                        self._apply_remote_update(sequenced)

                elif kind == "connected":
                    self._connected = True
                    self.status_var.set(f"●  Connected — {item['server_id']}")

                elif kind == "disconnected":
                    self._connected = False
                    self.publish_cb = None
                    self.status_var.set("○  Disconnected — retrying…")

                elif kind == "limited":
                    notice: VestaLimitNotice = item["notice"]
                    kind_label = "transient" if notice.is_transient else "permanent"
                    self.limit_var.set(f"[LIMITED] {notice.code}: {notice.message} ({kind_label})")

        except queue.Empty:
            pass

        self.root.after(16, self._poll_queue)

    @staticmethod
    def _now_ms() -> int:
        return int(datetime.now(timezone.utc).timestamp() * 1000)


# ── WebSocket background task ─────────────────────────────────────────────────
async def vesta_loop(app: App, loop: asyncio.AbstractEventLoop, local_store: SqliteClientEventStore):
    conn = VestaConnection(
        server_url=app.server_url,
        client_id=app.client_id,
        channels=[app.channel],
        public_key=app.identity.public_key_b64,
        local_store=local_store,
    )

    def on_connected(welcome):
        app.incoming.put({"kind": "connected", "server_id": welcome.server_id})

        # Wire up publish callback
        async def _publish(text: str, cursor_pos: int):
            event = create_event(
                channel_id=app.channel,
                client_id=app.client_id,
                event_type="app.collab.document-update",
                payload={"text": text, "username": app.username, "cursorPos": cursor_pos},
                replace=True,
                identity=app.identity,
            )
            await conn.publish(event)

        app.publish_cb = lambda text, cursor_pos: asyncio.run_coroutine_threadsafe(
            _publish(text, cursor_pos), loop
        )

        # Publish current document state on connect
        current_text = app.editor.get("1.0", "end-1c")
        if current_text.strip():
            asyncio.run_coroutine_threadsafe(_publish(current_text, 0), loop)

    def on_event(msg):
        app.incoming.put({
            "kind": "event",
            "event": SequencedEvent(event=msg.event, sequence=msg.sequence, received_at=msg.received_at),
        })

    def on_events_batch(msg):
        for se in msg.events:
            app.incoming.put({"kind": "event", "event": se})

    def on_disconnected(reason):
        app.incoming.put({"kind": "disconnected"})
        app.publish_cb = None

    def on_limited(notice: VestaLimitNotice):
        app.incoming.put({"kind": "limited", "notice": notice})

    conn.on_connected(on_connected)
    conn.on_event(on_event)
    conn.on_events_batch(on_events_batch)
    conn.on_disconnected(on_disconnected)
    conn.on_limited(on_limited)

    await conn.connect()
    # Keep the loop running
    await asyncio.Event().wait()


def start_ws_thread(app: App, local_store: SqliteClientEventStore):
    def run():
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        loop.run_until_complete(vesta_loop(app, loop, local_store))

    threading.Thread(target=run, daemon=True).start()


# ── Username prompt ───────────────────────────────────────────────────────────
def prompt_username() -> str | None:
    result = [None]

    dlg = tk.Tk()
    dlg.title("Vesta Collab Edit")
    dlg.configure(bg=BG)
    dlg.resizable(False, False)

    tk.Label(dlg, text="Collaborative Editor", bg=BG, fg=FG,
             font=("Segoe UI", 14, "bold")).pack(padx=32, pady=(24, 2))
    tk.Label(dlg, text="Enter your display name to join", bg=BG, fg=FG_DIM,
             font=("Segoe UI", 9)).pack(padx=32, pady=(0, 12))

    name_var = tk.StringVar()
    entry = tk.Entry(dlg, textvariable=name_var, font=("Segoe UI", 11),
                     bg="#2d2d2d", fg=FG, insertbackground=FG,
                     relief="flat", bd=6, width=22)
    entry.pack(padx=32, pady=(0, 10), ipadx=4, ipady=5)
    entry.focus()

    def on_ok(event=None):
        name = name_var.get().strip()
        if name:
            result[0] = name
            dlg.destroy()

    entry.bind("<Return>", on_ok)
    tk.Button(dlg, text="Join", command=on_ok,
              bg=ACCENT, fg="white", font=("Segoe UI", 10, "bold"),
              relief="flat", bd=0, padx=20, pady=7,
              activebackground="#1177bb", activeforeground="white",
              cursor="hand2").pack(padx=32, pady=(0, 24))

    dlg.mainloop()
    return result[0]


def resolve_identity(prefix: str) -> VestaIdentity:
    """Load an identity from VESTA_IDENTITY_FILE (e.g. the ``{appId}.identity.json``
    downloaded from Atrium) when set, otherwise use a local persistent identity."""
    import base64
    import json
    import os

    path = os.environ.get("VESTA_IDENTITY_FILE")
    if path:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        raw = data["privateKey"]
        raw += "=" * (-len(raw) % 4)  # restore base64url padding
        return VestaIdentity.from_private_key(base64.urlsafe_b64decode(raw))
    return load_or_create_identity(prefix)


# ── Entry point ───────────────────────────────────────────────────────────────
if __name__ == "__main__":
    import os

    # Relay URL: VESTA_RELAY_URL (e.g. your Atrium-managed relay) > positional arg > default.
    server_url = os.environ.get("VESTA_RELAY_URL") or (
        sys.argv[1] if len(sys.argv) > 1 else DEFAULT_SERVER
    )
    room = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_ROOM
    # App namespace = first channel segment. Set VESTA_APP_ID to the app id you
    # provisioned in Atrium so the channel is scoped under it. Defaults to "collab-edit".
    app_id = os.environ.get("VESTA_APP_ID", "collab-edit")
    channel = f"{app_id}/{room}"

    username = prompt_username()
    if not username:
        sys.exit(0)

    identity = resolve_identity(f"collab-edit-{room}-{username}")

    VESTA_DIR.mkdir(parents=True, exist_ok=True)
    prefix = f"collab-edit-{room}-{username}"
    local_store = SqliteClientEventStore(str(VESTA_DIR / f"{prefix}-cache.db"))
    projection_store = SqliteProjectionStore(str(VESTA_DIR / f"{prefix}-snapshots.db"))

    state = LwwRegister(document_projector)
    asyncio.run(restore_projection(projection_store, channel, "document", state))

    root = tk.Tk()
    app = App(root, username, identity, server_url, channel, state)

    def on_close():
        asyncio.run(save_projection(projection_store, channel, "document", state))
        root.destroy()

    root.protocol("WM_DELETE_WINDOW", on_close)
    start_ws_thread(app, local_store)
    root.mainloop()
