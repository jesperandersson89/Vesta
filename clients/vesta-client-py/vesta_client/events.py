"""Helper for creating (and optionally signing) VestaEvent instances."""

from __future__ import annotations

from datetime import datetime, timezone
from typing import Any
from uuid import uuid4

from vesta_client.identity import VestaIdentity
from vesta_client.types import VestaEvent


def _utc_now_iso_seconds() -> str:
    """ISO-8601 UTC timestamp with second precision (matches C# server signing format)."""
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def create_event(
    channel_id: str,
    client_id: str,
    event_type: str,
    payload: Any,
    *,
    replace: bool = False,
    parent_id: str | None = None,
    metadata: dict | None = None,
    identity: VestaIdentity | None = None,
) -> VestaEvent:
    """
    Create a new ``VestaEvent`` with a UUID and current timestamp.

    If ``identity`` is provided the event is also signed (its ``signature``
    field is populated). The identity's ``client_id`` must match ``client_id``.
    """
    event = VestaEvent(
        id=str(uuid4()),
        channel_id=channel_id,
        timestamp=_utc_now_iso_seconds(),
        client_id=client_id,
        event_type=event_type,
        payload=payload,
        replace=replace,
        parent_id=parent_id,
        metadata=metadata,
    )
    if identity is not None:
        # Imported lazily to avoid a hard dependency cycle if signing module
        # gains additional optional deps in the future.
        from vesta_client.signing import sign_event
        event = sign_event(event, identity)
    return event
