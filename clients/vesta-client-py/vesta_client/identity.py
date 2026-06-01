"""Persistent client identity (Ed25519 keypair)."""

from __future__ import annotations

import base64
import hashlib
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

VESTA_DIR = Path.home() / ".vesta"


def b64url_encode(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def b64url_decode(s: str) -> bytes:
    pad = (-len(s)) % 4
    return base64.urlsafe_b64decode(s + ("=" * pad))


def derive_client_id(public_key: bytes) -> str:
    """clientId = base64url(sha256(publicKey))[:22] — matches C# VestaIdentity."""
    return b64url_encode(hashlib.sha256(public_key).digest())[:22]


@dataclass
class VestaIdentity:
    """A Vesta client identity backed by an Ed25519 keypair."""

    client_id: str
    public_key: bytes               # 32 bytes
    _private_key: Ed25519PrivateKey

    @property
    def public_key_b64(self) -> str:
        return b64url_encode(self.public_key)

    def sign(self, data: bytes) -> bytes:
        return self._private_key.sign(data)

    def sign_b64(self, data: bytes) -> str:
        return b64url_encode(self.sign(data))

    @staticmethod
    def generate() -> "VestaIdentity":
        priv = Ed25519PrivateKey.generate()
        pub_bytes = priv.public_key().public_bytes(
            encoding=serialization.Encoding.Raw,
            format=serialization.PublicFormat.Raw,
        )
        return VestaIdentity(
            client_id=derive_client_id(pub_bytes),
            public_key=pub_bytes,
            _private_key=priv,
        )

    @staticmethod
    def from_private_key(seed: bytes) -> "VestaIdentity":
        priv = Ed25519PrivateKey.from_private_bytes(seed)
        pub_bytes = priv.public_key().public_bytes(
            encoding=serialization.Encoding.Raw,
            format=serialization.PublicFormat.Raw,
        )
        return VestaIdentity(
            client_id=derive_client_id(pub_bytes),
            public_key=pub_bytes,
            _private_key=priv,
        )

    def export_private_key(self) -> bytes:
        return self._private_key.private_bytes(
            encoding=serialization.Encoding.Raw,
            format=serialization.PrivateFormat.Raw,
            encryption_algorithm=serialization.NoEncryption(),
        )


def load_or_create_identity(prefix: str) -> VestaIdentity:
    """
    Load or create a persistent client identity stored in
    ``~/.vesta/{prefix}-identity.json``.

    File format::

        { "clientId": "...", "publicKey": "...", "privateKey": "...", ... }

    Keys are base64url-encoded. Apps may store additional fields in the same
    file (e.g. ``username``) via :func:`save_identity_extra` / :func:`load_identity_extra`;
    those fields are preserved when this function regenerates the file.
    """
    VESTA_DIR.mkdir(exist_ok=True)
    path = VESTA_DIR / f"{prefix}-identity.json"

    if path.exists():
        data: dict[str, Any] = json.loads(path.read_text(encoding="utf-8"))
        priv_b64 = data.get("privateKey")
        if priv_b64:
            return VestaIdentity.from_private_key(b64url_decode(priv_b64))
        # Older format (only clientId) — fall through and regenerate.

    identity = VestaIdentity.generate()
    data = {
        "clientId": identity.client_id,
        "publicKey": identity.public_key_b64,
        "privateKey": b64url_encode(identity.export_private_key()),
    }
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")
    return identity


def load_identity_extra(prefix: str) -> dict[str, Any]:
    """Read the full identity JSON (including any extra app-specific fields)."""
    path = VESTA_DIR / f"{prefix}-identity.json"
    if not path.exists():
        return {}
    return json.loads(path.read_text(encoding="utf-8"))


def save_identity_extra(prefix: str, extra: dict[str, Any]) -> None:
    """Merge additional fields into the identity file (e.g. username)."""
    path = VESTA_DIR / f"{prefix}-identity.json"
    data: dict[str, Any] = {}
    if path.exists():
        data = json.loads(path.read_text(encoding="utf-8"))
    data.update(extra)
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")
