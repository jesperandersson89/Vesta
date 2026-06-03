"""Vesta protocol client library for Python."""

from vesta_client.connection import VestaConnection
from vesta_client.events import create_event
from vesta_client.identity import (
    VestaIdentity,
    load_identity_extra,
    load_or_create_identity,
    save_identity_extra,
)
from vesta_client.device_groups import (
    ANNOUNCE_EVENT_TYPE,
    DeviceGroup,
    DeviceGroupProjection,
    LINK_EVENT_TYPE,
    PROTOCOL_CHANNEL_PREFIX,
    PairingPayload,
    UNLINK_EVENT_TYPE,
    build_announce,
    build_link,
    build_unlink,
    device_group_channel,
    generate_group_id,
    is_protocol_channel,
)
from vesta_client.projections import (
    AppendOnlyLog,
    EventReducer,
    LwwMap,
    LwwMapUpdate,
    LwwRegister,
    ProjectionCheckpoint,
)
from vesta_client.signing import build_signing_input, sign_event
from vesta_client.storage import (
    ClientEventStore,
    InMemoryClientEventStore,
    OutboxEntry,
    OutboxStatus,
    SqliteClientEventStore,
)
from vesta_client.types import (
    AckMessage,
    ErrorMessage,
    EventMessage,
    EventsBatchMessage,
    SequencedEvent,
    ServerMessage,
    VestaEvent,
    WelcomeMessage,
)

__all__ = [
    "AckMessage",
    "ANNOUNCE_EVENT_TYPE",
    "AppendOnlyLog",
    "ClientEventStore",
    "DeviceGroup",
    "DeviceGroupProjection",
    "ErrorMessage",
    "EventMessage",
    "EventReducer",
    "EventsBatchMessage",
    "InMemoryClientEventStore",
    "LINK_EVENT_TYPE",
    "LwwMap",
    "LwwMapUpdate",
    "LwwRegister",
    "OutboxEntry",
    "OutboxStatus",
    "PROTOCOL_CHANNEL_PREFIX",
    "PairingPayload",
    "ProjectionCheckpoint",
    "SequencedEvent",
    "ServerMessage",
    "SqliteClientEventStore",
    "UNLINK_EVENT_TYPE",
    "VestaConnection",
    "VestaEvent",
    "VestaIdentity",
    "WelcomeMessage",
    "build_announce",
    "build_link",
    "build_signing_input",
    "build_unlink",
    "create_event",
    "device_group_channel",
    "generate_group_id",
    "is_protocol_channel",
    "load_identity_extra",
    "load_or_create_identity",
    "save_identity_extra",
    "sign_event",
]
