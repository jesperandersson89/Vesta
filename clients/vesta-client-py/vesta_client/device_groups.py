"""
Cross-device identity (device groups) — Python SDK.

Mirrors ``VestaCore.Identity`` from the C# implementation. See ``PLANNING.md``
→ "Cross-Device Identity" for the design rationale.
"""

from __future__ import annotations

import json
import secrets
import threading
from dataclasses import dataclass, field
from typing import Any

from vesta_client.events import create_event
from vesta_client.identity import (
    VestaIdentity,
    b64url_decode,
    b64url_encode,
    derive_client_id,
)
from vesta_client.types import SequencedEvent, VestaEvent


# ─── Constants ────────────────────────────────────────────────────────────

PROTOCOL_CHANNEL_PREFIX = "vesta/"
ANNOUNCE_EVENT_TYPE = "vesta.identity.announce"
LINK_EVENT_TYPE = "vesta.identity.link"
UNLINK_EVENT_TYPE = "vesta.identity.unlink"


# ─── Helpers ──────────────────────────────────────────────────────────────


def device_group_channel(group_id: str) -> str:
    """Canonical channel ID for a device group: ``vesta/identity/{group_id}``."""
    if not group_id:
        raise ValueError("Group ID must not be empty.")
    return f"vesta/identity/{group_id}"


def is_protocol_channel(channel_id: str) -> bool:
    """True if the channel ID is in the reserved ``vesta/`` namespace."""
    return channel_id.startswith(PROTOCOL_CHANNEL_PREFIX)


def generate_group_id() -> str:
    """
    Generate a new random group identifier suitable for embedding in a channel ID.

    Returns 32 lowercase hex chars (128 bits of entropy). Hex is used because
    the ``_`` character in base64url is not permitted in channel IDs.
    """
    return secrets.token_hex(16)


def _validate_group_id(group_id: str) -> None:
    if not group_id:
        raise ValueError("Group ID must not be empty.")
    for c in group_id:
        ok = ("a" <= c <= "z") or ("0" <= c <= "9") or c == "-"
        if not ok:
            raise ValueError(
                f"Group ID '{group_id}' contains invalid character '{c}'. "
                "Only [a-z0-9-] are allowed."
            )


# ─── Event builders ───────────────────────────────────────────────────────


def build_announce(
    identity: VestaIdentity,
    group_id: str,
    device_name: str | None = None,
) -> VestaEvent:
    """Build and sign a ``vesta.identity.announce`` event."""
    _validate_group_id(group_id)
    payload: dict[str, Any] = {"groupId": group_id}
    if device_name is not None:
        payload["deviceName"] = device_name
    return create_event(
        channel_id=device_group_channel(group_id),
        client_id=identity.client_id,
        event_type=ANNOUNCE_EVENT_TYPE,
        payload=payload,
        identity=identity,
    )


def build_link(
    signer: VestaIdentity,
    group_id: str,
    target_public_key: bytes,
    reason: str | None = None,
) -> VestaEvent:
    """
    Build and sign a ``vesta.identity.link`` event vouching for ``target_public_key``
    as a member of ``group_id``. Signed by ``signer``, who must already be a trusted
    member of the group for the link to be honored.
    """
    _validate_group_id(group_id)
    if len(target_public_key) != 32:
        raise ValueError("Target public key must be 32 bytes (Ed25519).")
    payload: dict[str, Any] = {
        "targetPublicKey": b64url_encode(target_public_key),
        "targetClientId": derive_client_id(target_public_key),
        "groupId": group_id,
    }
    if reason is not None:
        payload["reason"] = reason
    return create_event(
        channel_id=device_group_channel(group_id),
        client_id=signer.client_id,
        event_type=LINK_EVENT_TYPE,
        payload=payload,
        identity=signer,
    )


def build_unlink(
    signer: VestaIdentity,
    group_id: str,
    target_public_key: bytes,
    reason: str | None = None,
) -> VestaEvent:
    """Build and sign a ``vesta.identity.unlink`` event removing ``target_public_key``."""
    _validate_group_id(group_id)
    if len(target_public_key) != 32:
        raise ValueError("Target public key must be 32 bytes (Ed25519).")
    payload: dict[str, Any] = {
        "targetPublicKey": b64url_encode(target_public_key),
        "targetClientId": derive_client_id(target_public_key),
        "groupId": group_id,
    }
    if reason is not None:
        payload["reason"] = reason
    return create_event(
        channel_id=device_group_channel(group_id),
        client_id=signer.client_id,
        event_type=UNLINK_EVENT_TYPE,
        payload=payload,
        identity=signer,
    )


# ─── PairingPayload ──────────────────────────────────────────────────────


@dataclass
class PairingPayload:
    """
    The information one device delivers to another out-of-band to bootstrap a
    device-group link. Not secret — contains only public information.
    """

    group_id: str
    public_key: str            # base64url-encoded 32-byte Ed25519 public key
    server_url: str | None = None

    def to_base64(self) -> str:
        """Encode as a compact base64url string (suitable for QR codes, deep links)."""
        d: dict[str, Any] = {"groupId": self.group_id, "publicKey": self.public_key}
        if self.server_url is not None:
            d["serverUrl"] = self.server_url
        return b64url_encode(json.dumps(d, separators=(",", ":")).encode("utf-8"))

    @staticmethod
    def from_base64(encoded: str) -> "PairingPayload":
        """Decode from a base64url-encoded payload previously produced by :meth:`to_base64`."""
        if not encoded:
            raise ValueError("Encoded pairing payload must not be empty.")
        d = json.loads(b64url_decode(encoded).decode("utf-8"))
        return PairingPayload(
            group_id=d["groupId"],
            public_key=d["publicKey"],
            server_url=d.get("serverUrl"),
        )


# ─── DeviceGroup view ────────────────────────────────────────────────────


@dataclass(frozen=True)
class DeviceGroup:
    """
    The current materialized state of a device group. Produced by
    :class:`DeviceGroupProjection` by replaying the identity channel.
    """

    group_id: str
    members: dict[str, str] = field(default_factory=dict)
    """Map of trusted ``clientId`` → base64url public key (empty string for the
    founder until they are cross-linked)."""

    def is_member(self, client_id: str) -> bool:
        return client_id in self.members


# ─── DeviceGroupProjection ───────────────────────────────────────────────


class DeviceGroupProjection:
    """
    Client-side reducer that materializes the current membership of a device
    group by replaying its ``vesta/identity/{group_id}`` channel.

    Trust rules (Phase 1):

    * The author of the first ``announce`` event is self-trusted (founder).
    * A ``link`` event is honored only if its signer is already trusted; it
      adds the target to the trusted set.
    * An ``unlink`` event is honored only if its signer is already trusted;
      it removes the target.
    * Signature verification of events is the caller's responsibility.
    """

    def __init__(self, group_id: str) -> None:
        if not group_id:
            raise ValueError("Group ID must not be empty.")
        self.group_id = group_id
        self._members: dict[str, str] = {}
        self._last_sequence: int = 0
        self._lock = threading.RLock()

    @property
    def last_sequence(self) -> int:
        with self._lock:
            return self._last_sequence

    @property
    def state(self) -> DeviceGroup:
        with self._lock:
            return DeviceGroup(group_id=self.group_id, members=dict(self._members))

    def is_member(self, client_id: str) -> bool:
        with self._lock:
            return client_id in self._members

    def apply(self, sequenced: SequencedEvent) -> None:
        """Apply a server-confirmed event. Advances ``last_sequence``."""
        with self._lock:
            self._reduce(sequenced.event)
            if sequenced.sequence > self._last_sequence:
                self._last_sequence = sequenced.sequence

    def apply_batch(self, events: list[SequencedEvent]) -> None:
        for s in events:
            self.apply(s)

    def apply_local(self, event: VestaEvent) -> None:
        """Apply a locally-authored event before sequencing. Does not advance ``last_sequence``."""
        with self._lock:
            self._reduce(event)

    def _reduce(self, evt: VestaEvent) -> None:
        t = evt.event_type
        if t == ANNOUNCE_EVENT_TYPE:
            self._reduce_announce(evt)
        elif t == LINK_EVENT_TYPE:
            self._reduce_link(evt)
        elif t == UNLINK_EVENT_TYPE:
            self._reduce_unlink(evt)

    def _reduce_announce(self, evt: VestaEvent) -> None:
        payload = evt.payload if isinstance(evt.payload, dict) else None
        if not payload or payload.get("groupId") != self.group_id:
            return
        # Founder bootstrap.
        if len(self._members) == 0:
            self._members[evt.client_id] = ""

    def _reduce_link(self, evt: VestaEvent) -> None:
        if evt.client_id not in self._members:
            return
        payload = evt.payload if isinstance(evt.payload, dict) else None
        if not payload or payload.get("groupId") != self.group_id:
            return
        target_pub = payload.get("targetPublicKey")
        target_cid = payload.get("targetClientId")
        if not isinstance(target_pub, str) or not isinstance(target_cid, str):
            return
        try:
            pub_bytes = b64url_decode(target_pub)
        except Exception:
            return
        if len(pub_bytes) != 32:
            return
        if derive_client_id(pub_bytes) != target_cid:
            return
        self._members[target_cid] = target_pub

    def _reduce_unlink(self, evt: VestaEvent) -> None:
        if evt.client_id not in self._members:
            return
        payload = evt.payload if isinstance(evt.payload, dict) else None
        if not payload or payload.get("groupId") != self.group_id:
            return
        target_cid = payload.get("targetClientId")
        if isinstance(target_cid, str):
            self._members.pop(target_cid, None)
