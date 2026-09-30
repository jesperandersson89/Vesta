"""
Console view over :class:`RelayRecoverySession`. Renders only from the session snapshot and
never adopts a relay without an explicit user action.
"""

from __future__ import annotations

from collections.abc import Awaitable, Callable

from vesta_client.relay_recovery import (
    ADOPTING,
    CHOICES,
    DEGRADED,
    DISCOVERING,
    EXHAUSTED,
    FAILED,
    HEALTHY,
    NOTHING_FOUND,
    RelayRecoverySession,
    RelayRecoverySnapshot,
)

UNVERIFIED_RELAY_WARNING = (
    "is not verified to host this app. It may not have your data, "
    "and a relay you don't trust can see what you send it."
)


def _title(snapshot: RelayRecoverySnapshot) -> str:
    return {
        DEGRADED: "Connection lost. Retrying...",
        EXHAUSTED: "Can't reach any relay for this app.",
        DISCOVERING: "Looking for other relays...",
        CHOICES: "Other relays found:",
        NOTHING_FOUND: "No other relays found.",
        ADOPTING: f"Connecting to {snapshot.adopting}...",
        FAILED: snapshot.failure_reason or "Failed.",
    }.get(snapshot.phase, "")


def _render(snapshot: RelayRecoverySnapshot, write: Callable[[str], None]) -> None:
    write(f"\n{_title(snapshot)}\n")
    for attempt in snapshot.tried_relays:
        write(f"  x {attempt.relay} - {attempt.reason}\n")
    for index, choice in enumerate(snapshot.choices):
        status: str = "hosts this app" if choice.hosts_requested_app else "unverified"
        remembered: str = ", remembered" if choice.from_cache else ""
        write(f"  {index + 1}. {choice.url} ({status}{remembered})\n")
    if snapshot.background_retrying:
        write("  (still retrying in the background)\n")
    clear: str = ", [c]lear saved relay" if snapshot.active_override else ""
    write(f"[r]etry, [d]iscover, [u]se <url>{clear}, [q]uit prompt, or a number > ")


async def run_console_relay_picker(
    session: RelayRecoverySession,
    read_line: Callable[[], Awaitable[str | None]],
    write: Callable[[str], None],
) -> bool:
    """
    Drive a text prompt until the connection recovers (True) or the user quits / input ends
    (False). ``read_line`` returns None at end of input.
    """
    while True:
        snapshot: RelayRecoverySnapshot = session.snapshot
        if snapshot.phase == HEALTHY:
            write("Connected.\n")
            return True
        _render(snapshot, write)

        line: str | None = await read_line()
        if line is None:
            return False
        line = line.strip()
        if not line:
            continue

        if line.isdigit() and 1 <= int(line) <= len(snapshot.choices):
            choice = snapshot.choices[int(line) - 1]
            if not choice.hosts_requested_app:
                write(f"{choice.url} {UNVERIFIED_RELAY_WARNING}\n")
                write("Connect anyway? [y/N] ")
                answer: str | None = await read_line()
                if answer is None or answer.strip().lower() != "y":
                    continue
            await session.adopt(choice)
            continue

        parts: list[str] = line.split(None, 1)
        verb: str = parts[0].lower()
        rest: str = parts[1] if len(parts) > 1 else ""
        if verb == "r":
            await session.retry()
        elif verb == "d":
            await session.discover()
        elif verb == "u":
            await session.use_manual(rest)
        elif verb == "c":
            await session.clear_override()
        elif verb == "q":
            session.dismiss()
            return False
        else:
            write("Unknown command.\n")
