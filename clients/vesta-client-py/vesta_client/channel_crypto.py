"""
Encryption primitives for end-to-end encrypted channels (PLANNING #33, phase B).

Byte-compatible with the C# ``ChannelCrypto`` and the TypeScript ``channel-crypto.ts``; the shared
vectors live in ``tests/test_channel_crypto.py``.

* :class:`ChannelKeyPair` - a member's X25519 pair, derived from the Ed25519 seed with
  ``HKDF-SHA256(ikm=seed, salt="", info="vesta-x25519-v1")`` so no second secret has to be stored.
* :func:`encrypt_payload` / :func:`decrypt_payload` - AES-256-GCM over the RFC 8785 canonical JSON of
  the app payload, 96-bit nonce, AAD ``"vesta-enc-v1\\n{channel_id}\\n{event_id}\\n{event_type}"``,
  carried as the ``{"enc":1,"kid":...,"n":...,"ct":...}`` envelope the relay enforces.
* :func:`seal_key` / :func:`open_key` - a channel key sealed to one recipient: ephemeral X25519 x
  recipient X25519 -> HKDF-SHA256(salt = epk || recipient_pub, info = "vesta-channel-key-seal-v1") ->
  AES-256-GCM over the 32-byte key, AAD ``"vesta-channel-key-v1\\n{channel_id}\\n{epoch}\\n{recipient_client_id}"``.

Signing is unchanged: the Ed25519 signature covers the envelope.
"""

from __future__ import annotations

import json
import os
from dataclasses import dataclass
from typing import Any

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

from vesta_client.identity import VestaIdentity, b64url_decode, b64url_encode
from vesta_client.signing import _canonicalize

ENVELOPE_VERSION = 1
NONCE_BYTES = 12
KEY_BYTES = 32
_TAG_BYTES = 16
_DERIVATION_INFO = b"vesta-x25519-v1"
_PAYLOAD_AAD_PREFIX = "vesta-enc-v1"
_SEAL_AAD_PREFIX = "vesta-channel-key-v1"
_SEAL_INFO = b"vesta-channel-key-seal-v1"


class ChannelCryptoError(ValueError):
    """Decryption or unsealing failed: wrong key, wrong recipient, tampered data or malformed input."""


class ChannelKeyPair:
    """A member's X25519 key agreement pair."""

    def __init__(self, private_key: X25519PrivateKey) -> None:
        self._private = private_key
        self.public_key: bytes = private_key.public_key().public_bytes_raw()

    @staticmethod
    def from_identity(identity: VestaIdentity) -> "ChannelKeyPair":
        """Derive the pair from a Vesta identity (its 32-byte Ed25519 seed)."""
        return ChannelKeyPair.from_seed(identity.export_private_key())

    @staticmethod
    def from_seed(seed: bytes) -> "ChannelKeyPair":
        """Derive the pair from a raw 32-byte Ed25519 seed."""
        if len(seed) != 32:
            raise ValueError("Seed must be 32 bytes.")
        scalar = HKDF(algorithm=hashes.SHA256(), length=32, salt=None, info=_DERIVATION_INFO).derive(seed)
        return ChannelKeyPair.from_private_key(scalar)

    @staticmethod
    def from_private_key(private_key: bytes) -> "ChannelKeyPair":
        """Wrap a raw 32-byte X25519 private key (ephemeral keys, test vectors)."""
        if len(private_key) != 32:
            raise ValueError("X25519 private key must be 32 bytes.")
        return ChannelKeyPair(X25519PrivateKey.from_private_bytes(private_key))

    @staticmethod
    def generate() -> "ChannelKeyPair":
        """A fresh random pair (ephemeral keys for sealing)."""
        return ChannelKeyPair(X25519PrivateKey.generate())

    def export_private_key(self) -> bytes:
        """The raw 32-byte private scalar. Only needed for tests and key stores."""
        return self._private.private_bytes_raw()

    def agree(self, peer_public_key: bytes) -> bytes:
        """Raw X25519 shared secret with ``peer_public_key``."""
        return self._private.exchange(X25519PublicKey.from_public_bytes(peer_public_key))


@dataclass(frozen=True)
class ChannelKey:
    """A channel's symmetric key for one epoch: 32 random bytes plus the integer epoch carried as ``kid``."""

    epoch: int
    key: bytes

    @staticmethod
    def generate(epoch: int) -> "ChannelKey":
        if epoch < 0:
            raise ValueError("Epoch must be non-negative.")
        return ChannelKey(epoch, os.urandom(KEY_BYTES))


@dataclass(frozen=True)
class SealedChannelKey:
    """A channel key sealed to one recipient, as carried inside a ``vesta.channel-key`` event."""

    epoch: int
    epk: str
    n: str
    ct: str

    def to_dict(self) -> dict[str, Any]:
        return {"epoch": self.epoch, "epk": self.epk, "n": self.n, "ct": self.ct}

    @staticmethod
    def from_dict(data: dict[str, Any]) -> "SealedChannelKey":
        try:
            return SealedChannelKey(int(data["epoch"]), str(data["epk"]), str(data["n"]), str(data["ct"]))
        except (KeyError, TypeError, ValueError) as exc:
            raise ChannelCryptoError("Malformed sealed channel key.") from exc


def is_envelope(payload: Any) -> bool:
    """Mirror of the relay's shape check: exactly ``{enc:1, kid:int>=0, n:b64url(12), ct:b64url(>=1)}``."""
    if not isinstance(payload, dict) or set(payload.keys()) != {"enc", "kid", "n", "ct"}:
        return False
    enc, kid, n, ct = payload["enc"], payload["kid"], payload["n"], payload["ct"]
    if not (isinstance(enc, int) and not isinstance(enc, bool) and enc == ENVELOPE_VERSION):
        return False
    if not (isinstance(kid, int) and not isinstance(kid, bool) and kid >= 0):
        return False
    try:
        return (
            isinstance(n, str) and len(b64url_decode(n)) == NONCE_BYTES
            and isinstance(ct, str) and len(b64url_decode(ct)) >= 1
        )
    except Exception:  # noqa: BLE001 - any decode failure means "not an envelope"
        return False


def read_epoch(envelope: Any) -> int:
    """The epoch (``kid``) an envelope was encrypted under, so the caller can pick the key."""
    if not is_envelope(envelope):
        raise ChannelCryptoError("Payload is not an encrypted envelope.")
    return int(envelope["kid"])


def encrypt_payload(
    key: ChannelKey,
    channel_id: str,
    event_id: str,
    event_type: str,
    payload: Any,
    *,
    nonce: bytes | None = None,
) -> dict[str, Any]:
    """Encrypt an app payload into an envelope. ``nonce`` is random unless pinned (tests, vectors)."""
    if len(key.key) != KEY_BYTES:
        raise ValueError("Channel key must be 32 bytes.")
    nonce = os.urandom(NONCE_BYTES) if nonce is None else nonce
    if len(nonce) != NONCE_BYTES:
        raise ValueError(f"Nonce must be {NONCE_BYTES} bytes.")
    plaintext = _canonicalize(payload).encode("utf-8")
    ct = AESGCM(key.key).encrypt(nonce, plaintext, _payload_aad(channel_id, event_id, event_type))
    return {"enc": ENVELOPE_VERSION, "kid": key.epoch, "n": b64url_encode(nonce), "ct": b64url_encode(ct)}


def decrypt_payload(key: ChannelKey, channel_id: str, event_id: str, event_type: str, envelope: Any) -> Any:
    """Decrypt an envelope back to the app payload; raises :class:`ChannelCryptoError` on any mismatch."""
    epoch = read_epoch(envelope)
    if epoch != key.epoch:
        raise ChannelCryptoError(f"Envelope uses key epoch {epoch}, but key epoch {key.epoch} was supplied.")
    ct = b64url_decode(envelope["ct"])
    if len(ct) < _TAG_BYTES:
        raise ChannelCryptoError("Ciphertext shorter than the authentication tag.")
    try:
        plaintext = AESGCM(key.key).decrypt(b64url_decode(envelope["n"]), ct, _payload_aad(channel_id, event_id, event_type))
    except InvalidTag as exc:
        raise ChannelCryptoError("Payload decryption failed.") from exc
    return json.loads(plaintext.decode("utf-8"))


def seal_key(
    key: ChannelKey,
    channel_id: str,
    recipient_client_id: str,
    recipient_public_key: bytes,
    *,
    ephemeral: ChannelKeyPair | None = None,
    nonce: bytes | None = None,
) -> SealedChannelKey:
    """Seal a channel key to a recipient's X25519 public key. ``ephemeral``/``nonce`` are random unless pinned."""
    if len(key.key) != KEY_BYTES:
        raise ValueError("Channel key must be 32 bytes.")
    if len(recipient_public_key) != 32:
        raise ValueError("Recipient X25519 public key must be 32 bytes.")
    ephemeral = ChannelKeyPair.generate() if ephemeral is None else ephemeral
    nonce = os.urandom(NONCE_BYTES) if nonce is None else nonce
    if len(nonce) != NONCE_BYTES:
        raise ValueError(f"Nonce must be {NONCE_BYTES} bytes.")
    wrap_key = _wrap_key(ephemeral.agree(recipient_public_key), ephemeral.public_key, recipient_public_key)
    ct = AESGCM(wrap_key).encrypt(nonce, key.key, _seal_aad(channel_id, key.epoch, recipient_client_id))
    return SealedChannelKey(key.epoch, b64url_encode(ephemeral.public_key), b64url_encode(nonce), b64url_encode(ct))


def open_key(sealed: SealedChannelKey, channel_id: str, recipient_client_id: str, recipient: ChannelKeyPair) -> ChannelKey:
    """Open a sealed key with the recipient's pair; raises :class:`ChannelCryptoError` if it was sealed to someone else."""
    try:
        epk, nonce, ct = b64url_decode(sealed.epk), b64url_decode(sealed.n), b64url_decode(sealed.ct)
    except Exception as exc:  # noqa: BLE001
        raise ChannelCryptoError("Malformed sealed channel key.") from exc
    if len(epk) != 32 or len(nonce) != NONCE_BYTES or len(ct) != KEY_BYTES + _TAG_BYTES:
        raise ChannelCryptoError("Malformed sealed channel key.")
    wrap_key = _wrap_key(recipient.agree(epk), epk, recipient.public_key)
    try:
        channel_key = AESGCM(wrap_key).decrypt(nonce, ct, _seal_aad(channel_id, sealed.epoch, recipient_client_id))
    except InvalidTag as exc:
        raise ChannelCryptoError("Sealed channel key was not sealed to this recipient.") from exc
    return ChannelKey(sealed.epoch, channel_key)


def _payload_aad(channel_id: str, event_id: str, event_type: str) -> bytes:
    return f"{_PAYLOAD_AAD_PREFIX}\n{channel_id}\n{event_id.lower()}\n{event_type}".encode("utf-8")


def _seal_aad(channel_id: str, epoch: int, recipient_client_id: str) -> bytes:
    return f"{_SEAL_AAD_PREFIX}\n{channel_id}\n{epoch}\n{recipient_client_id}".encode("utf-8")


def _wrap_key(shared_secret: bytes, ephemeral_public_key: bytes, recipient_public_key: bytes) -> bytes:
    return HKDF(
        algorithm=hashes.SHA256(), length=32, salt=ephemeral_public_key + recipient_public_key, info=_SEAL_INFO
    ).derive(shared_secret)
