#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Vesta Color Wheel
─────────────────
A tkinter GUI where each user picks a color from a wheel and sees
all other connected users' current colors update in real time.

Event schema
  Channel:   colorwheel/{room}
  Event:     app.colorwheel.update
  Payload:   { "color": "#rrggbb", "username": "..." }
  Volatile:  true  (no need to store colour in db, just relay to current subscribers)
  Replace:   true  (server keeps only the latest per user)

Run:  python main.py [ws://host:port/ws] [room-name]
"""

import asyncio
import colorsys
import math
import queue
import threading
import tkinter as tk
from datetime import datetime, timezone

from vesta_client import VestaConnection, create_event, load_or_create_identity

# ── Configuration ─────────────────────────────────────────────────────────────
DEFAULT_SERVER = "ws://localhost:5150/ws"
DEFAULT_ROOM   = "main"

# ── Theme ─────────────────────────────────────────────────────────────────────
BG       = "#1e1e1e"
BG_CARD  = "#252526"
BG_ENTRY = "#2d2d2d"
FG       = "#cccccc"
FG_DIM   = "#666666"
ACCENT   = "#0e639c"

WHEEL_SIZE = 240          # Canvas width/height for the color wheel
WHEEL_R    = WHEEL_SIZE // 2 - 6


# ── State (LWW projection) ────────────────────────────────────────────────────
class ColorWheelState:
    """Thread-safe last-writer-wins projection of user colors."""

    def __init__(self):
        self._users: dict[str, dict] = {}  # clientId → {clientId, username, color, timestamp}
        self._lock = threading.Lock()

    def apply(self, event: dict) -> bool:
        """Apply an event. Returns True if state changed."""
        if event.get("eventType") != "app.colorwheel.update":
            return False
        client_id = event.get("clientId", "")
        ts        = event.get("timestamp", "")
        payload   = event.get("payload", {})
        color     = payload.get("color", "#ffffff")
        username  = payload.get("username", client_id[:8])

        with self._lock:
            existing = self._users.get(client_id)
            if existing and existing["timestamp"] >= ts:
                return False  # Older than what we have — ignore
            self._users[client_id] = {
                "clientId": client_id,
                "username": username,
                "color": color,
                "timestamp": ts,
            }
        return True

    def users(self) -> list[dict]:
        with self._lock:
            return sorted(self._users.values(), key=lambda u: u["username"].lower())


# ── Color wheel math ──────────────────────────────────────────────────────────
def pick_color(x: int, y: int, cx: int, cy: int) -> str | None:
    """Convert a canvas coordinate to an HSV-derived hex color, or None if outside wheel."""
    dx, dy = x - cx, y - cy
    dist = math.hypot(dx, dy)
    if dist > WHEEL_R:
        return None
    h = (math.atan2(-dy, dx) / (2 * math.pi)) % 1.0
    s = min(dist / WHEEL_R, 1.0)
    r, g, b = colorsys.hsv_to_rgb(h, s, 1.0)
    return f"#{int(r * 255):02x}{int(g * 255):02x}{int(b * 255):02x}"


def render_wheel(size: int, radius: int, bg: str) -> tk.PhotoImage:
    """Pre-render the HSV color wheel into a PhotoImage. Called once on startup."""
    cx = cy = size // 2
    img = tk.PhotoImage(width=size, height=size)
    rows = []
    for y in range(size):
        row = []
        for x in range(size):
            dx, dy = x - cx, y - cy
            dist = math.hypot(dx, dy)
            if dist <= radius:
                h = (math.atan2(-dy, dx) / (2 * math.pi)) % 1.0
                s = dist / radius
                rv, gv, bv = colorsys.hsv_to_rgb(h, s, 1.0)
                row.append(f"#{int(rv * 255):02x}{int(gv * 255):02x}{int(bv * 255):02x}")
            else:
                row.append(bg)
        rows.append("{" + " ".join(row) + "}")
    img.put(" ".join(rows))
    return img


# ── GUI ───────────────────────────────────────────────────────────────────────
class App:
    def __init__(self, root: tk.Tk, username: str, client_id: str, server_url: str, channel: str):
        self.root       = root
        self.username   = username
        self.client_id  = client_id
        self.server_url = server_url
        self.channel    = channel

        self.current_color = "#ff4444"
        self.state         = ColorWheelState()
        self.incoming: queue.Queue = queue.Queue()
        self.publish_cb    = None   # set by the WS thread once connected
        self._connected    = False
        self._user_widgets: dict[str, tuple[tk.Frame, tk.Canvas, int, tk.Label]] = {}  # clientId → (row, dot_canvas, oval_id, label)

        root.title(f"Vesta Color Wheel — {username}")
        root.configure(bg=BG)
        root.resizable(False, False)

        self._build_ui()
        self._render_wheel()
        self._apply_local(self.current_color)  # Show self immediately
        self._poll_queue()

    # ── Build UI ──────────────────────────────────────────────────────────────
    def _build_ui(self):
        outer = tk.Frame(self.root, bg=BG)
        outer.pack(fill="both", expand=True, padx=14, pady=14)

        # ── Left: wheel + preview + status ───────────────────────────────────
        left = tk.Frame(outer, bg=BG)
        left.pack(side="left", anchor="n", padx=(0, 14))

        tk.Label(left, text="Pick your color", bg=BG, fg=FG_DIM,
                 font=("Segoe UI", 9)).pack(anchor="w", pady=(0, 4))

        self.wheel_canvas = tk.Canvas(
            left, width=WHEEL_SIZE, height=WHEEL_SIZE,
            bg=BG, highlightthickness=0, cursor="crosshair",
        )
        self.wheel_canvas.pack()
        self.wheel_canvas.bind("<Button-1>",        self._on_drag)
        self.wheel_canvas.bind("<B1-Motion>",       self._on_drag)
        self.wheel_canvas.bind("<ButtonRelease-1>", self._on_release)

        self.preview = tk.Canvas(
            left, width=WHEEL_SIZE, height=44,
            bg=self.current_color, highlightthickness=1, highlightbackground="#444",
        )
        self.preview.pack(pady=(6, 0))

        self.status_var = tk.StringVar(value="○  Connecting…")
        tk.Label(left, textvariable=self.status_var, bg=BG, fg=FG_DIM,
                 font=("Segoe UI", 8)).pack(pady=(6, 0), anchor="w")

        # ── Right: user list ──────────────────────────────────────────────────
        right = tk.Frame(outer, bg=BG_CARD, width=230)
        right.pack(side="left", fill="y")
        right.pack_propagate(False)

        tk.Label(right, text="Who's here", bg=BG_CARD, fg=FG,
                 font=("Segoe UI", 10, "bold"), pady=10).pack()

        sep = tk.Frame(right, bg="#333333", height=1)
        sep.pack(fill="x", padx=8, pady=(0, 8))

        self.users_frame = tk.Frame(right, bg=BG_CARD)
        self.users_frame.pack(fill="both", expand=True, padx=8)

    # ── Color wheel rendering ─────────────────────────────────────────────────
    def _render_wheel(self):
        cx = cy = WHEEL_SIZE // 2
        self._wheel_img = render_wheel(WHEEL_SIZE, WHEEL_R, BG)
        self.wheel_canvas.create_image(cx, cy, image=self._wheel_img, anchor="center")
        # Cursor ring showing current pick position
        self._cursor = self.wheel_canvas.create_oval(
            cx - 7, cy - 7, cx + 7, cy + 7,
            outline="white", width=2, fill="",
        )

    # ── Wheel interactions ────────────────────────────────────────────────────
    def _on_drag(self, event):
        cx = cy = WHEEL_SIZE // 2
        color = pick_color(event.x, event.y, cx, cy)
        if color:
            self.wheel_canvas.coords(self._cursor,
                event.x - 7, event.y - 7, event.x + 7, event.y + 7)
            self.preview.configure(bg=color)
            self.current_color = color
            self._apply_local(color)
            if self.publish_cb:
                self.publish_cb(color)

    def _on_release(self, event):
        pass  # Drag already publishes on every move

    def _apply_local(self, color: str):
        """Optimistically apply our own color update to local state."""
        fake_event = {
            "clientId": self.client_id,
            "eventType": "app.colorwheel.update",
            "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z",
            "payload": {"color": color, "username": self.username},
        }
        if self.state.apply(fake_event):
            self._redraw_users()

    # ── Message queue polling ─────────────────────────────────────────────────
    def _poll_queue(self):
        try:
            while True:
                item = self.incoming.get_nowait()
                kind = item["kind"]

                if kind == "event":
                    if self.state.apply(item["event"]):
                        self._redraw_users()

                elif kind == "connected":
                    self._connected = True
                    self.status_var.set(f"●  {item['server_id']}")

                elif kind == "disconnected":
                    self._connected = False
                    self.publish_cb = None
                    self.status_var.set("○  Disconnected — retrying…")

        except queue.Empty:
            pass

        self.root.after(16, self._poll_queue)

    # ── User list display ─────────────────────────────────────────────────────
    def _redraw_users(self):
        users = self.state.users()
        current_ids = {u["clientId"] for u in users}

        # Remove widgets for users no longer present
        for cid in list(self._user_widgets.keys()):
            if cid not in current_ids:
                row, _, _, _ = self._user_widgets.pop(cid)
                row.destroy()

        # Update existing or create new widgets
        for user in users:
            cid = user["clientId"]
            is_self = cid == self.client_id
            name = user["username"] + (" (you)" if is_self else "")
            label_color = FG if not is_self else "#ffffff"

            if cid in self._user_widgets:
                # Update in place — no flicker
                _, dot, oval_id, label = self._user_widgets[cid]
                dot.itemconfigure(oval_id, fill=user["color"])
                label.configure(text=name, fg=label_color)
            else:
                # Create new row
                row = tk.Frame(self.users_frame, bg=BG_CARD)
                row.pack(fill="x", pady=4)

                dot = tk.Canvas(row, width=24, height=24, bg=BG_CARD, highlightthickness=0)
                dot.pack(side="left", padx=(0, 10))
                oval_id = dot.create_oval(2, 2, 22, 22, fill=user["color"], outline="")

                label = tk.Label(row, text=name, bg=BG_CARD, fg=label_color,
                                 font=("Segoe UI", 9))
                label.pack(side="left", anchor="w")

                self._user_widgets[cid] = (row, dot, oval_id, label)


# ── WebSocket background task ─────────────────────────────────────────────────
async def vesta_loop(app: App, loop: asyncio.AbstractEventLoop):
    conn = VestaConnection(
        server_url=app.server_url,
        client_id=app.client_id,
        channels=[app.channel],
    )

    def on_connected(welcome):
        app.incoming.put({"kind": "connected", "server_id": welcome.server_id})

        # Wire up publish callback (called from tkinter thread)
        async def _publish(color: str):
            event = create_event(
                channel_id=app.channel,
                client_id=app.client_id,
                event_type="app.colorwheel.update",
                payload={"color": color, "username": app.username},
                replace=True,
            )
            await conn.publish(event)

        app.publish_cb = lambda color: asyncio.run_coroutine_threadsafe(
            _publish(color), loop
        )

        # Announce our initial color
        asyncio.run_coroutine_threadsafe(_publish(app.current_color), loop)

    def on_event(msg):
        if msg.event.client_id != app.client_id:
            evt = {
                "clientId": msg.event.client_id,
                "eventType": msg.event.event_type,
                "timestamp": msg.event.timestamp,
                "payload": msg.event.payload,
            }
            app.incoming.put({"kind": "event", "event": evt})

    def on_events_batch(msg):
        for se in msg.events:
            if se.event.client_id != app.client_id:
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
    dlg.title("Vesta Color Wheel")
    dlg.configure(bg=BG)
    dlg.resizable(False, False)

    tk.Label(dlg, text="Color Wheel", bg=BG, fg=FG,
             font=("Segoe UI", 14, "bold")).pack(padx=32, pady=(24, 2))
    tk.Label(dlg, text="Enter your display name to join", bg=BG, fg=FG_DIM,
             font=("Segoe UI", 9)).pack(padx=32, pady=(0, 12))

    name_var = tk.StringVar()
    entry = tk.Entry(dlg, textvariable=name_var, font=("Segoe UI", 11),
                     bg=BG_ENTRY, fg=FG, insertbackground=FG,
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
    import sys

    server_url = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_SERVER
    room       = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_ROOM
    channel    = f"colorwheel/{room}"

    username = prompt_username()
    if not username:
        sys.exit(0)

    client_id = load_or_create_identity(f"colorwheel-{room}-{username}")

    root = tk.Tk()
    app  = App(root, username, client_id, server_url, channel)
    start_ws_thread(app)
    root.mainloop()
