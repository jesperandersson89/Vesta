#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Vesta Collaborative Editor
───────────────────────────
A tkinter GUI where multiple users edit the same text document in real time.
Uses last-writer-wins on the full document text, with debounced publishing.

Event schema:
  Channel:   collab-edit/{room}
  Event:     app.collab.document-update
  Payload:   { "text": "...", "username": "...", "cursorPos": 42 }
  Replace:   true (server keeps only the latest version per user)

Conflict model:
  LWW on the full document. When a remote update arrives with a newer
  timestamp, we replace the local text and try to preserve cursor position.
  During active local typing (within the debounce window), remote updates
  are deferred to avoid fighting with the user's input.

Run:  python main.py [ws://host:port/ws] [room-name]
"""

import asyncio
import queue
import sys
import threading
import tkinter as tk
from datetime import datetime, timezone
from tkinter import scrolledtext

from vesta_client import VestaConnection, VestaIdentity, create_event, load_or_create_identity

# ── Configuration ─────────────────────────────────────────────────────────────
DEFAULT_SERVER = "ws://localhost:5150/ws"
DEFAULT_ROOM = "main"

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
class DocumentState:
    """Thread-safe LWW projection of the shared document."""

    def __init__(self):
        self._text = ""
        self._timestamp = ""
        self._last_author = ""
        self._lock = threading.Lock()

    @property
    def text(self) -> str:
        with self._lock:
            return self._text

    @property
    def timestamp(self) -> str:
        with self._lock:
            return self._timestamp

    @property
    def last_author(self) -> str:
        with self._lock:
            return self._last_author

    def apply(self, event: dict) -> bool:
        """Apply an event. Returns True if document changed."""
        if event.get("eventType") != "app.collab.document-update":
            return False
        ts = event.get("timestamp", "")
        payload = event.get("payload", {})
        text = payload.get("text", "")
        username = payload.get("username", "")

        with self._lock:
            if self._timestamp and self._timestamp >= ts:
                return False
            self._text = text
            self._timestamp = ts
            self._last_author = username
        return True


# ── GUI ───────────────────────────────────────────────────────────────────────
class App:
    def __init__(self, root: tk.Tk, username: str, identity: VestaIdentity, server_url: str, channel: str):
        self.root = root
        self.username = username
        self.identity = identity
        self.client_id = identity.client_id
        self.server_url = server_url
        self.channel = channel

        self.state = DocumentState()
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
        now_ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
        self.state.apply({
            "clientId": self.client_id,
            "eventType": "app.collab.document-update",
            "timestamp": now_ts,
            "payload": {"text": text, "username": self.username, "cursorPos": 0},
        })
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
    def _apply_remote_update(self, event: dict):
        """Apply a remote document update, preserving cursor position."""
        # Don't apply while user is actively typing
        elapsed = self._now_ms() - self._last_local_edit_ms
        if elapsed < DEFER_REMOTE_MS:
            # Re-check shortly
            self.root.after(DEFER_REMOTE_MS - elapsed + 10,
                            lambda: self._apply_remote_update(event))
            return

        if not self.state.apply(event):
            return

        new_text = self.state.text
        author = event.get("payload", {}).get("username", "?")
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
                    evt = item["event"]
                    if evt.get("clientId") != self.client_id:
                        self._apply_remote_update(evt)

                elif kind == "connected":
                    self._connected = True
                    self.status_var.set(f"●  Connected — {item['server_id']}")

                elif kind == "disconnected":
                    self._connected = False
                    self.publish_cb = None
                    self.status_var.set("○  Disconnected — retrying…")

        except queue.Empty:
            pass

        self.root.after(16, self._poll_queue)

    @staticmethod
    def _now_ms() -> int:
        return int(datetime.now(timezone.utc).timestamp() * 1000)


# ── WebSocket background task ─────────────────────────────────────────────────
async def vesta_loop(app: App, loop: asyncio.AbstractEventLoop):
    conn = VestaConnection(
        server_url=app.server_url,
        client_id=app.client_id,
        channels=[app.channel],
        public_key=app.identity.public_key_b64,
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
        evt = {
            "clientId": msg.event.client_id,
            "eventType": msg.event.event_type,
            "timestamp": msg.event.timestamp,
            "payload": msg.event.payload,
        }
        app.incoming.put({"kind": "event", "event": evt})

    def on_events_batch(msg):
        for se in msg.events:
            evt = {
                "clientId": se.event.client_id,
                "eventType": se.event.event_type,
                "timestamp": se.event.timestamp,
                "payload": se.event.payload,
            }
            app.incoming.put({"kind": "event", "event": evt})

    def on_disconnected(reason):
        app.incoming.put({"kind": "disconnected"})
        app.publish_cb = None

    conn.on_connected(on_connected)
    conn.on_event(on_event)
    conn.on_events_batch(on_events_batch)
    conn.on_disconnected(on_disconnected)

    await conn.connect()
    # Keep the loop running
    await asyncio.Event().wait()


def start_ws_thread(app: App):
    def run():
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        loop.run_until_complete(vesta_loop(app, loop))

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


# ── Entry point ───────────────────────────────────────────────────────────────
if __name__ == "__main__":
    server_url = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_SERVER
    room = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_ROOM
    channel = f"collab-edit/{room}"

    username = prompt_username()
    if not username:
        sys.exit(0)

    identity = load_or_create_identity(f"collab-edit-{room}-{username}")

    root = tk.Tk()
    app = App(root, username, identity, server_url, channel)
    start_ws_thread(app)
    root.mainloop()
