"""
The two control events that distribute channel keys on an encrypted channel (PLANNING #33 phase C).
Both are ordinary signed events with ``vesta.*`` types, so the relay lets them through the envelope
check and they ride the normal catch-up. Mirrors the C# ``ChannelKeyEvents``.

* ``vesta.channel-key-request`` - payload ``{publicKey, x25519PublicKey}`` (both base64url).
  Self-authenticating: the Ed25519 key must derive the author's ``client_id`` and the signature must verify.
* ``vesta.channel-key`` - payload ``{recipient, keys: [{epoch, epk, n, ct}]}``. Accepted only when signed
  by one of the admin keys the app trusts.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any
from uuid import uuid4

from vesta_client.channel_crypto import ChannelKeyPair, SealedChannelKey
from vesta_client.identity import VestaIdentity, b64url_decode, b64url_encode, derive_client_id
from vesta_client.signing import sign_event, verify_event
from vesta_client.types import VestaEvent

KEY_REQUEST_EVENT_TYPE = "vesta.channel-key-request"
KEY_GRANT_EVENT_TYPE = "vesta.channel-key"


@dataclass(frozen=True)
class ChannelKeyRequest:
    """A verified key request: a member announcing its X25519 public key."""

    client_id: str
    public_key: bytes
    x25519_public_key: bytes


@dataclass(frozen=True)
class ChannelKeyGrant:
    """A verified key grant: an admin handing one recipient one or more sealed epochs."""

    admin_client_id: str
    recipient_client_id: str
    keys: list[SealedChannelKey]


def _new_event(identity: VestaIdentity, channel_id: str, event_type: str, payload: Any) -> VestaEvent:
    event = VestaEvent(
        id=str(uuid4()),
        channel_id=channel_id,
        timestamp=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z",
        client_id=identity.client_id,
        event_type=event_type,
        payload=payload,
    )
    return sign_event(event, identity)


def build_key_request(identity: VestaIdentity, channel_id: str, key_pair: ChannelKeyPair) -> VestaEvent:
    """Build and sign a key request from ``identity`` for ``channel_id``."""
    return _new_event(identity, channel_id, KEY_REQUEST_EVENT_TYPE, {
        "publicKey": b64url_encode(identity.public_key),
        "x25519PublicKey": b64url_encode(key_pair.public_key),
    })


def build_key_grant(admin: VestaIdentity, channel_id: str, recipient_client_id: str, keys: list[SealedChannelKey]) -> VestaEvent:
    """Build and sign a key grant from ``admin`` to ``recipient_client_id``."""
    if not keys:
        raise ValueError("A key grant must carry at least one sealed key.")
    return _new_event(admin, channel_id, KEY_GRANT_EVENT_TYPE, {
        "recipient": recipient_client_id,
        "keys": [k.to_dict() for k in keys],
    })


def _try_decode(value: Any, expected_length: int) -> bytes | None:
    if not isinstance(value, str):
        return None
    try:
        decoded = b64url_decode(value)
    except Exception:  # noqa: BLE001
        return None
    return decoded if len(decoded) == expected_length else None


def try_parse_key_request(event: VestaEvent) -> ChannelKeyRequest | None:
    """Parse and verify a key request; ``None`` for any other type, a malformed payload, a key that does
    not derive the author's client id, or a bad signature."""
    if event.event_type != KEY_REQUEST_EVENT_TYPE or not isinstance(event.payload, dict):
        return None
    public_key = _try_decode(event.payload.get("publicKey"), 32)
    x25519_public_key = _try_decode(event.payload.get("x25519PublicKey"), 32)
    if public_key is None or x25519_public_key is None:
        return None
    if derive_client_id(public_key) != event.client_id:
        return None
    if not event.signature or not verify_event(event, public_key):
        return None
    return ChannelKeyRequest(event.client_id, public_key, x25519_public_key)


def try_parse_key_grant(event: VestaEvent, admin_public_keys: list[bytes]) -> ChannelKeyGrant | None:
    """Parse and verify a key grant against the admin keys the app trusts; ``None`` for any other type, an
    author that is not a trusted admin, a bad signature or a malformed payload. The caller still has to check
    ``recipient_client_id``."""
    if event.event_type != KEY_GRANT_EVENT_TYPE or not isinstance(event.payload, dict):
        return None
    admin = next((k for k in admin_public_keys if derive_client_id(k) == event.client_id), None)
    if admin is None or not event.signature or not verify_event(event, admin):
        return None
    recipient = event.payload.get("recipient")
    raw_keys = event.payload.get("keys")
    if not isinstance(recipient, str) or not isinstance(raw_keys, list) or not raw_keys:
        return None
    keys: list[SealedChannelKey] = []
    for item in raw_keys:
        if not isinstance(item, dict):
            return None
        try:
            keys.append(SealedChannelKey.from_dict(item))
        except Exception:  # noqa: BLE001
            return None
    return ChannelKeyGrant(event.client_id, recipient, keys)
