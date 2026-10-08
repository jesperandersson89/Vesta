"""
Client-side classification of server ``ErrorMessage`` codes — the Python mirror of the C#
``VestaClient.VestaLimitNotice`` / ``VestaErrorCodes``. Centralizes "is this a limit the app
should hear about?" and "can the offending event ever succeed on retry, or should it be
dead-lettered?" so ``VestaConnection`` stays declarative.
"""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class VestaLimitNotice:
    """A structured "your app is being limited" signal raised via the ``on_limited`` callback."""

    code: str
    message: str
    channel_id: str | None
    event_id: str | None
    is_transient: bool


class VestaErrorCodes:
    RATE_LIMITED = "RATE_LIMITED"
    QUOTA_EXCEEDED = "QUOTA_EXCEEDED"
    MESSAGE_QUOTA_EXCEEDED = "MESSAGE_QUOTA_EXCEEDED"
    UNKNOWN_APP = "UNKNOWN_APP"
    ACCESS_DENIED = "ACCESS_DENIED"
    APP_NOT_ALLOWED = "APP_NOT_ALLOWED"
    # The operator paused the app: publishes are refused until it is resumed.
    APP_PAUSED = "APP_PAUSED"


@dataclass(frozen=True)
class ErrorClassification:
    """The outcome of classifying a server error code."""

    is_limit: bool
    is_transient: bool
    is_event_fatal: bool


_FATAL_LIMIT_CODES = (
    VestaErrorCodes.QUOTA_EXCEEDED,
    VestaErrorCodes.MESSAGE_QUOTA_EXCEEDED,
    VestaErrorCodes.UNKNOWN_APP,
    VestaErrorCodes.ACCESS_DENIED,
    VestaErrorCodes.APP_NOT_ALLOWED,
)

_FATAL_PROTOCOL_CODES = (
    "INVALID_CHANNEL",
    "INVALID_SIGNATURE",
    "SIGNATURE_REQUIRED",
    "CLIENT_ID_MISMATCH",
    "PROTOCOL_NAMESPACE_RESERVED",
    "CHANNEL_DELETED",
)


def classify_error_code(code: str) -> ErrorClassification:
    """Classify a server error code into limit / retry semantics."""
    if code in (VestaErrorCodes.RATE_LIMITED, VestaErrorCodes.APP_PAUSED):
        return ErrorClassification(is_limit=True, is_transient=True, is_event_fatal=False)
    if code in _FATAL_LIMIT_CODES:
        return ErrorClassification(is_limit=True, is_transient=False, is_event_fatal=True)
    if code in _FATAL_PROTOCOL_CODES:
        # Doomed events that are client/protocol errors, not "limits": still dead-letter so
        # the offline outbox doesn't re-send them on every reconnect.
        return ErrorClassification(is_limit=False, is_transient=False, is_event_fatal=True)
    # Unknown codes: be conservative — don't dead-letter, don't claim it's a limit.
    return ErrorClassification(is_limit=False, is_transient=True, is_event_fatal=False)
