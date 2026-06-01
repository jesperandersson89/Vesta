"""Vesta protocol type definitions."""

from dataclasses import dataclass, field
from typing import Any


@dataclass
class VestaEvent:
    """A client-authored immutable event."""

    id: str
    channel_id: str
    timestamp: str
    client_id: str
    event_type: str
    payload: Any
    parent_id: str | None = None
    signature: str | None = None
    replace: bool = False
    metadata: dict[str, Any] | None = None

    def to_dict(self) -> dict[str, Any]:
        d: dict[str, Any] = {
            "id": self.id,
            "channelId": self.channel_id,
            "timestamp": self.timestamp,
            "clientId": self.client_id,
            "eventType": self.event_type,
            "payload": self.payload,
        }
        if self.parent_id is not None:
            d["parentId"] = self.parent_id
        if self.signature is not None:
            d["signature"] = self.signature
        if self.replace:
            d["replace"] = True
        if self.metadata is not None:
            d["metadata"] = self.metadata
        return d

    @staticmethod
    def from_dict(data: dict[str, Any]) -> "VestaEvent":
        return VestaEvent(
            id=data["id"],
            channel_id=data["channelId"],
            timestamp=data["timestamp"],
            client_id=data["clientId"],
            event_type=data["eventType"],
            payload=data.get("payload"),
            parent_id=data.get("parentId"),
            signature=data.get("signature"),
            replace=data.get("replace", False),
            metadata=data.get("metadata"),
        )


@dataclass
class SequencedEvent:
    """A VestaEvent with server-assigned sequence metadata."""

    event: VestaEvent
    sequence: int
    received_at: str

    @staticmethod
    def from_dict(data: dict[str, Any]) -> "SequencedEvent":
        return SequencedEvent(
            event=VestaEvent.from_dict(data["event"]),
            sequence=data["sequence"],
            received_at=data["receivedAt"],
        )


# ── Server messages ───────────────────────────────────────────────────────────


@dataclass
class WelcomeMessage:
    server_id: str
    channels: list[str]


@dataclass
class EventMessage:
    channel_id: str
    event: VestaEvent
    sequence: int
    received_at: str


@dataclass
class EventsBatchMessage:
    channel_id: str
    events: list[SequencedEvent]


@dataclass
class AckMessage:
    channel_id: str
    event_id: str
    sequence: int


@dataclass
class ErrorMessage:
    code: str
    message: str


ServerMessage = WelcomeMessage | EventMessage | EventsBatchMessage | AckMessage | ErrorMessage


def parse_server_message(data: dict[str, Any]) -> ServerMessage:
    """Parse a raw JSON dict into a typed server message."""
    msg_type = data.get("type")
    match msg_type:
        case "WELCOME":
            return WelcomeMessage(
                server_id=data["serverId"],
                channels=data["channels"],
            )
        case "EVENT":
            return EventMessage(
                channel_id=data["channelId"],
                event=VestaEvent.from_dict(data["event"]),
                sequence=data["sequence"],
                received_at=data["receivedAt"],
            )
        case "EVENTS_BATCH":
            return EventsBatchMessage(
                channel_id=data["channelId"],
                events=[SequencedEvent.from_dict(se) for se in data.get("events", [])],
            )
        case "ACK":
            return AckMessage(
                channel_id=data["channelId"],
                event_id=data["eventId"],
                sequence=data["sequence"],
            )
        case "ERROR":
            return ErrorMessage(
                code=data["code"],
                message=data["message"],
            )
        case _:
            raise ValueError(f"Unknown message type: {msg_type}")
