"""
Event signing for the Vesta protocol.

Builds an RFC 8785 (JCS) canonical JSON representation of the signed fields
and signs it with Ed25519. Must produce identical bytes to the C# server's
``EventSigner.BuildSigningInput`` for cross-language verification.

Signed fields (sorted lexicographically):
    channelId, clientId, id, parentId, payload, timestamp, type

Notably NOT signed:
    signature, sequence, receivedAt, metadata, replace, volatile
"""

from __future__ import annotations

import json
import re
from datetime import datetime
from typing import Any

from vesta_client.identity import VestaIdentity, b64url_encode
from vesta_client.types import VestaEvent

_SIGNED_FIELDS = ("channelId", "clientId", "id", "parentId", "payload", "timestamp", "type")


def _canonicalize(value: Any) -> str:
    """
    Minimal RFC 8785 canonical JSON for the limited value types Vesta payloads use:
    dict, list, str, int, float, bool, None.

    Rules applied:
      * dict keys sorted lexicographically by UTF-16 code-unit order
      * no whitespace between tokens
      * strings use compact escapes (json.dumps with ensure_ascii=False)
      * integers as integers, floats as shortest round-trippable form
      * null included (not stripped)
    """
    if value is None:
        return "null"
    if value is True:
        return "true"
    if value is False:
        return "false"
    if isinstance(value, str):
        return json.dumps(value, ensure_ascii=False)
    if isinstance(value, int):
        return str(value)
    if isinstance(value, float):
        # JCS uses shortest round-trip form; Python's repr handles this.
        if value != value or value in (float("inf"), float("-inf")):
            raise ValueError("Cannot canonicalize NaN/Infinity")
        return _format_float(value)
    if isinstance(value, list):
        return "[" + ",".join(_canonicalize(v) for v in value) + "]"
    if isinstance(value, dict):
        # Sort keys by code-point order (Python str sort is code-point order)
        items = sorted(value.items(), key=lambda kv: kv[0])
        return "{" + ",".join(
            json.dumps(k, ensure_ascii=False) + ":" + _canonicalize(v) for k, v in items
        ) + "}"
    raise TypeError(f"Cannot canonicalize value of type {type(value).__name__}")


def _format_float(value: float) -> str:
    """Shortest round-trippable JSON number form."""
    if value.is_integer():
        return f"{int(value)}"
    return repr(value)


_TIMESTAMP_RE = re.compile(
    r"^(?P<date>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.\d+)?(?P<tz>Z|[+-]\d{2}:?\d{2})$"
)


def normalize_timestamp_for_signing(ts: str) -> str:
    """
    Reproduce the C# server's timestamp canonicalization for signing.

    The server's ``EventSigner.FormatTimestamp`` truncates to seconds and emits
    ``yyyy-MM-ddTHH:mm:ssZ`` (UTC). Clients must use this exact form in the
    signing input so signatures verify after the server round-trips through
    ``DateTimeOffset``.
    """
    m = _TIMESTAMP_RE.match(ts)
    if not m:
        # Fall back to datetime parsing for non-matching inputs.
        dt = datetime.fromisoformat(ts.replace("Z", "+00:00"))
        return dt.strftime("%Y-%m-%dT%H:%M:%SZ")
    # Drop sub-second part. If tz isn't Z, convert to UTC.
    if m.group("tz") == "Z":
        return f"{m.group('date')}Z"
    dt = datetime.fromisoformat(ts.replace("Z", "+00:00"))
    return dt.strftime("%Y-%m-%dT%H:%M:%SZ")


def build_signing_input(event: VestaEvent) -> bytes:
    """Construct the canonical JSON bytes that get signed."""
    fields: dict[str, Any] = {
        "channelId": event.channel_id,
        "clientId": event.client_id,
        "id": event.id,
        "parentId": event.parent_id,            # may be None → "null"
        "payload": event.payload,
        "timestamp": normalize_timestamp_for_signing(event.timestamp),
        "type": event.event_type,
    }
    # Only include known signed fields, but keep insertion-independent order
    # via the canonicalizer's sorting.
    signed = {k: fields[k] for k in _SIGNED_FIELDS}
    canonical = _canonicalize(signed)
    return canonical.encode("utf-8")


def sign_event(event: VestaEvent, identity: VestaIdentity) -> VestaEvent:
    """Return a copy of ``event`` with ``signature`` populated."""
    if event.client_id != identity.client_id:
        raise ValueError(
            f"Event clientId '{event.client_id}' does not match identity '{identity.client_id}'"
        )
    signing_input = build_signing_input(event)
    event.signature = identity.sign_b64(signing_input)
    return event
