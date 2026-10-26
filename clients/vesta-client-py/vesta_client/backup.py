"""
Identity backup and recovery for the Python SDK (mirrors the C# ``IdentityBackup``, ``RelayBackup``
and ``RecoveryPhrase``; blobs, phrases and relay channels are byte-for-byte interchangeable).

* :func:`export_identity` / :func:`import_identity` - password-encrypted blob (Argon2id + AES-256-GCM).
* :func:`recovery_phrase_from_seed` / :func:`seed_from_recovery_phrase` - 24 words (BIP-39 wordlist).
* :class:`RelayBackup` - park the blob on a relay, recoverable with just (app, username, password).
"""

from __future__ import annotations

import hashlib
import json
import os
import unicodedata
from collections.abc import Iterable
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any
from uuid import uuid4

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.argon2 import Argon2id

from vesta_client._bip39_words import BIP39_ENGLISH
from vesta_client.identity import VestaIdentity, b64url_decode, b64url_encode
from vesta_client.signing import sign_event
from vesta_client.types import VestaEvent

BACKUP_FORMAT = "vesta-identity-backup"
BACKUP_VERSION = 1
RELAY_BACKUP_EVENT_TYPE = "vesta.identity-backup"

_SALT_LENGTH = 16
_NONCE_LENGTH = 12
_TAG_LENGTH = 16
_SEED_LENGTH = 32
_MATERIAL_LENGTH = 64

# Blobs are untrusted input: refuse parameters that would make opening one a denial of service.
_MIN_MEMORY_KIB = 8
_MAX_MEMORY_KIB = 524_288
_MAX_ITERATIONS = 10


class IdentityBackupError(Exception):
    """A backup cannot be opened: wrong password, corrupted or unsupported blob."""


class RecoveryPhraseError(ValueError):
    """A recovery phrase is malformed or its checksum does not match."""


@dataclass(frozen=True)
class IdentityBackupOptions:
    """Argon2id cost: memory in KiB (default 64 MiB) and passes (default 3); parallelism is fixed at 1."""

    memory_kib: int = 65536
    iterations: int = 3


_DEFAULT_OPTIONS = IdentityBackupOptions()


def _validate(options: IdentityBackupOptions) -> IdentityBackupOptions:
    if not (_MIN_MEMORY_KIB <= options.memory_kib <= _MAX_MEMORY_KIB) or not (
        1 <= options.iterations <= _MAX_ITERATIONS
    ):
        raise IdentityBackupError(
            f"Unsupported Argon2id cost (memory {options.memory_kib} KiB, {options.iterations} passes)."
        )
    return options


def _derive_material(password: str, salt: bytes, options: IdentityBackupOptions) -> bytes:
    kdf = Argon2id(
        salt=salt,
        length=_MATERIAL_LENGTH,
        iterations=options.iterations,
        lanes=1,
        memory_cost=options.memory_kib,
    )
    # NFKC so the same password typed on different platforms and keyboards derives the same key.
    return kdf.derive(unicodedata.normalize("NFKC", password).encode("utf-8"))


def _aad(client_id: str) -> bytes:
    return f"vesta-identity-backup-v1\n{client_id}".encode("utf-8")


def _seal(identity: VestaIdentity, material: bytes, salt: bytes, options: IdentityBackupOptions) -> dict[str, Any]:
    nonce = os.urandom(_NONCE_LENGTH)
    sealed = AESGCM(material[:32]).encrypt(nonce, identity.export_private_key(), _aad(identity.client_id))
    return {
        "format": BACKUP_FORMAT,
        "version": BACKUP_VERSION,
        "clientId": identity.client_id,
        "kdf": {
            "name": "argon2id",
            "memoryKiB": options.memory_kib,
            "iterations": options.iterations,
            "parallelism": 1,
            "salt": b64url_encode(salt),
        },
        "cipher": {"name": "aes-256-gcm", "nonce": b64url_encode(nonce)},
        "ciphertext": b64url_encode(sealed),
    }


@dataclass(frozen=True)
class _ParsedBlob:
    client_id: str
    options: IdentityBackupOptions
    salt: bytes
    nonce: bytes
    ciphertext: bytes


def _parse(data: Any) -> _ParsedBlob:
    try:
        if data["format"] != BACKUP_FORMAT:
            raise IdentityBackupError("Not a Vesta identity backup.")
        if data["version"] != BACKUP_VERSION:
            raise IdentityBackupError(f"Unsupported backup version {data['version']}.")
        kdf, cipher = data["kdf"], data["cipher"]
        if kdf["name"] != "argon2id" or kdf["parallelism"] != 1 or cipher["name"] != "aes-256-gcm":
            raise IdentityBackupError("Unsupported backup algorithms.")
        options = _validate(IdentityBackupOptions(int(kdf["memoryKiB"]), int(kdf["iterations"])))
        salt = b64url_decode(kdf["salt"])
        nonce = b64url_decode(cipher["nonce"])
        ciphertext = b64url_decode(data["ciphertext"])
        if (
            len(salt) != _SALT_LENGTH
            or len(nonce) != _NONCE_LENGTH
            or len(ciphertext) != _SEED_LENGTH + _TAG_LENGTH
        ):
            raise IdentityBackupError("The backup is malformed.")
        return _ParsedBlob(str(data["clientId"]), options, salt, nonce, ciphertext)
    except IdentityBackupError:
        raise
    except (KeyError, TypeError, ValueError):
        raise IdentityBackupError("The backup is malformed.") from None


def _open(blob: _ParsedBlob, material: bytes) -> VestaIdentity:
    try:
        seed = AESGCM(material[:32]).decrypt(blob.nonce, blob.ciphertext, _aad(blob.client_id))
    except InvalidTag:
        raise IdentityBackupError("Wrong password, or the backup is corrupted.") from None
    identity = VestaIdentity.from_private_key(seed)
    if identity.client_id != blob.client_id:
        raise IdentityBackupError("The backup does not match its client id.")
    return identity


def export_identity(
    identity: VestaIdentity, password: str, options: IdentityBackupOptions | None = None
) -> str:
    """Encrypt ``identity``'s seed under ``password`` into a versioned JSON blob (random salt)."""
    opts = _validate(options or _DEFAULT_OPTIONS)
    salt = os.urandom(_SALT_LENGTH)
    material = _derive_material(password, salt, opts)
    return json.dumps(_seal(identity, material, salt, opts), separators=(",", ":"))


def import_identity(blob: str, password: str) -> VestaIdentity:
    """Open a blob made by :func:`export_identity`. Raises :class:`IdentityBackupError` on failure."""
    try:
        data = json.loads(blob)
    except (TypeError, ValueError):
        raise IdentityBackupError("The backup is not valid JSON.") from None
    parsed = _parse(data)
    return _open(parsed, _derive_material(password, parsed.salt, parsed.options))


class RelayBackup:
    """
    A backup an app can park on a relay so a player who lost every device can recover with just
    (username, password). The encryption key and the unguessable channel
    (``vesta/identity-backup/{hex}``) come from one Argon2id run over (app, username, password).
    Anyone can publish junk to the channel, so :meth:`restore` trusts only blobs that decrypt.
    """

    def __init__(self, material: bytes, salt: bytes, options: IdentityBackupOptions) -> None:
        self._material = material
        self._salt = salt
        self._options = options
        self.channel_id = f"vesta/identity-backup/{material[32:48].hex()}"

    @classmethod
    def derive(
        cls, app_id: str, username: str, password: str, options: IdentityBackupOptions | None = None
    ) -> "RelayBackup":
        """Derive keys and channel. The username is trimmed, NFKC-normalised and lower-cased."""
        if not app_id or not username:
            raise ValueError("app_id and username are required")
        opts = _validate(options or _DEFAULT_OPTIONS)
        normalized = unicodedata.normalize("NFKC", username.strip()).lower()
        salt = hashlib.sha256(f"vesta-backup-v1\n{app_id}\n{normalized}".encode("utf-8")).digest()[:_SALT_LENGTH]
        return cls(_derive_material(password, salt, opts), salt, opts)

    def create_event(self, identity: VestaIdentity) -> VestaEvent:
        """A signed, replace-style event carrying the encrypted identity; publish it on ``channel_id``."""
        event = VestaEvent(
            id=str(uuid4()),
            channel_id=self.channel_id,
            timestamp=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            client_id=identity.client_id,
            event_type=RELAY_BACKUP_EVENT_TYPE,
            payload=_seal(identity, self._material, self._salt, self._options),
            replace=True,
        )
        return sign_event(event, identity)

    def restore(self, events: Iterable[VestaEvent]) -> VestaIdentity | None:
        """The identity from the first event on ``channel_id`` that decrypts under the derived key, or None."""
        for event in events:
            if event.event_type != RELAY_BACKUP_EVENT_TYPE or event.channel_id != self.channel_id:
                continue
            try:
                blob = _parse(event.payload)
                if blob.salt != self._salt or blob.options != self._options:
                    continue
                return _open(blob, self._material)
            except IdentityBackupError:
                continue
        return None


_WORD_INDEX = {word: index for index, word in enumerate(BIP39_ENGLISH)}
_PHRASE_WORDS = 24


def recovery_phrase_from_seed(seed: bytes) -> str:
    """The 24-word phrase (BIP-39 entropy-to-mnemonic, no PBKDF2) for a 32-byte seed."""
    if len(seed) != _SEED_LENGTH:
        raise ValueError(f"A recovery phrase encodes exactly {_SEED_LENGTH} bytes.")
    bits = int.from_bytes(seed + hashlib.sha256(seed).digest()[:1], "big")
    return " ".join(BIP39_ENGLISH[(bits >> (11 * (_PHRASE_WORDS - 1 - i))) & 0x7FF] for i in range(_PHRASE_WORDS))


def seed_from_recovery_phrase(phrase: str) -> bytes:
    """The seed for a phrase; case and extra whitespace are ignored. Raises :class:`RecoveryPhraseError`."""
    words = phrase.split()
    if len(words) != _PHRASE_WORDS:
        raise RecoveryPhraseError(f"A recovery phrase has {_PHRASE_WORDS} words, got {len(words)}.")
    bits = 0
    for raw in words:
        index = _WORD_INDEX.get(raw.lower())
        if index is None:
            raise RecoveryPhraseError(f"'{raw}' is not a recovery-phrase word.")
        bits = (bits << 11) | index
    data = bits.to_bytes(_SEED_LENGTH + 1, "big")
    seed, checksum = data[:_SEED_LENGTH], data[_SEED_LENGTH]
    if hashlib.sha256(seed).digest()[0] != checksum:
        raise RecoveryPhraseError(
            "The recovery phrase checksum does not match; check for a mistyped or misordered word."
        )
    return seed
