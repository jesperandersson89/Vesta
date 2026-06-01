"""Helper for creating VestaEvent instances."""

from datetime import datetime, timezone
from uuid import uuid4

from vesta_client.types import VestaEvent


def create_event(
    channel_id: str,
    client_id: str,
    event_type: str,
    payload: object,
    *,
    replace: bool = False,
    parent_id: str | None = None,
    metadata: dict | None = None,
) -> VestaEvent:
    """Create a new VestaEvent with a UUID and current timestamp."""
    return VestaEvent(
        id=str(uuid4()),
        channel_id=channel_id,
        timestamp=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z",
        client_id=client_id,
        event_type=event_type,
        payload=payload,
        replace=replace,
        parent_id=parent_id,
        metadata=metadata,
    )
