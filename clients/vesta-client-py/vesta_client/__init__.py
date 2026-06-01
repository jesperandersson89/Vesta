"""Vesta protocol client library for Python."""

from vesta_client.connection import VestaConnection
from vesta_client.events import create_event
from vesta_client.identity import (
    VestaIdentity,
    load_identity_extra,
    load_or_create_identity,
    save_identity_extra,
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
    "AppendOnlyLog",
    "ErrorMessage",
    "EventMessage",
    "EventReducer",
    "EventsBatchMessage",
    "LwwMap",
    "LwwMapUpdate",
    "LwwRegister",
    "ProjectionCheckpoint",
    "SequencedEvent",
    "ServerMessage",
    "VestaConnection",
    "VestaEvent",
    "VestaIdentity",
    "WelcomeMessage",
    "build_signing_input",
    "create_event",
    "load_identity_extra",
    "load_or_create_identity",
    "save_identity_extra",
    "sign_event",
]
