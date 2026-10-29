"""
Where a client keeps the channel keys it has received or created, plus (for admins) the X25519 public
keys of the members it has seen, so it can seal new epochs to them after a rotation. Keys are stored in
the clear: the device already holds them to read the channel. Pluggable like ``ProjectionStore``.
"""

from __future__ import annotations

import json
import os
import sqlite3
from abc import ABC, abstractmethod

from vesta_client.channel_crypto import ChannelKey
from vesta_client.identity import b64url_decode, b64url_encode


class ChannelKeyStore(ABC):
    @abstractmethod
    async def load_keys(self, channel_id: str) -> list[ChannelKey]:
        """Every epoch key known for the channel, any order."""

    @abstractmethod
    async def save_key(self, channel_id: str, key: ChannelKey) -> None:
        """Save (or overwrite) one epoch key."""

    @abstractmethod
    async def load_members(self, channel_id: str) -> dict[str, bytes]:
        """Members the admin has seen on the channel: client id -> X25519 public key."""

    @abstractmethod
    async def save_member(self, channel_id: str, client_id: str, x25519_public_key: bytes) -> None:
        """Remember (or update) a member's X25519 public key."""

    @abstractmethod
    async def remove_member(self, channel_id: str, client_id: str) -> None:
        """Forget a member (after revocation) so the next rotation is not sealed to it."""


class InMemoryChannelKeyStore(ChannelKeyStore):
    """Volatile store for tests and throwaway sessions; keys are lost on exit."""

    def __init__(self) -> None:
        self._keys: dict[str, dict[int, bytes]] = {}
        self._members: dict[str, dict[str, bytes]] = {}

    async def load_keys(self, channel_id: str) -> list[ChannelKey]:
        return [ChannelKey(epoch, key) for epoch, key in self._keys.get(channel_id, {}).items()]

    async def save_key(self, channel_id: str, key: ChannelKey) -> None:
        self._keys.setdefault(channel_id, {})[key.epoch] = bytes(key.key)

    async def load_members(self, channel_id: str) -> dict[str, bytes]:
        return dict(self._members.get(channel_id, {}))

    async def save_member(self, channel_id: str, client_id: str, x25519_public_key: bytes) -> None:
        self._members.setdefault(channel_id, {})[client_id] = bytes(x25519_public_key)

    async def remove_member(self, channel_id: str, client_id: str) -> None:
        self._members.get(channel_id, {}).pop(client_id, None)


class FileChannelKeyStore(ChannelKeyStore):
    """A single JSON file, one record per channel (``{channel: {keys: {epoch: b64}, members: {id: b64}}}``)."""

    def __init__(self, path: str) -> None:
        self._path = path

    def _read(self) -> dict[str, dict[str, dict[str, str]]]:
        if not os.path.exists(self._path):
            return {}
        try:
            with open(self._path, encoding="utf-8") as f:
                return json.load(f)
        except (OSError, ValueError):
            return {}

    def _write(self, data: dict[str, dict[str, dict[str, str]]]) -> None:
        os.makedirs(os.path.dirname(os.path.abspath(self._path)), exist_ok=True)
        with open(self._path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)

    async def load_keys(self, channel_id: str) -> list[ChannelKey]:
        keys = self._read().get(channel_id, {}).get("keys", {})
        return [ChannelKey(int(epoch), b64url_decode(key)) for epoch, key in keys.items()]

    async def save_key(self, channel_id: str, key: ChannelKey) -> None:
        data = self._read()
        record = data.setdefault(channel_id, {"keys": {}, "members": {}})
        record.setdefault("keys", {})[str(key.epoch)] = b64url_encode(key.key)
        self._write(data)

    async def load_members(self, channel_id: str) -> dict[str, bytes]:
        members = self._read().get(channel_id, {}).get("members", {})
        return {client_id: b64url_decode(key) for client_id, key in members.items()}

    async def save_member(self, channel_id: str, client_id: str, x25519_public_key: bytes) -> None:
        data = self._read()
        record = data.setdefault(channel_id, {"keys": {}, "members": {}})
        record.setdefault("members", {})[client_id] = b64url_encode(x25519_public_key)
        self._write(data)

    async def remove_member(self, channel_id: str, client_id: str) -> None:
        data = self._read()
        data.get(channel_id, {}).get("members", {}).pop(client_id, None)
        self._write(data)


class SqliteChannelKeyStore(ChannelKeyStore):
    """SQLite-backed store (two tables keyed by channel); ``":memory:"`` for tests."""

    def __init__(self, path: str) -> None:
        self._conn = sqlite3.connect(path)
        self._conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS channel_keys (
                channel_id TEXT NOT NULL, epoch INTEGER NOT NULL, key BLOB NOT NULL,
                PRIMARY KEY (channel_id, epoch));
            CREATE TABLE IF NOT EXISTS channel_members (
                channel_id TEXT NOT NULL, client_id TEXT NOT NULL, x25519_public_key BLOB NOT NULL,
                PRIMARY KEY (channel_id, client_id));
            """
        )

    async def load_keys(self, channel_id: str) -> list[ChannelKey]:
        rows = self._conn.execute("SELECT epoch, key FROM channel_keys WHERE channel_id = ? ORDER BY epoch", (channel_id,))
        return [ChannelKey(int(epoch), bytes(key)) for epoch, key in rows]

    async def save_key(self, channel_id: str, key: ChannelKey) -> None:
        with self._conn:
            self._conn.execute(
                "INSERT INTO channel_keys (channel_id, epoch, key) VALUES (?, ?, ?) "
                "ON CONFLICT(channel_id, epoch) DO UPDATE SET key = excluded.key",
                (channel_id, key.epoch, bytes(key.key)),
            )

    async def load_members(self, channel_id: str) -> dict[str, bytes]:
        rows = self._conn.execute("SELECT client_id, x25519_public_key FROM channel_members WHERE channel_id = ?", (channel_id,))
        return {client_id: bytes(key) for client_id, key in rows}

    async def save_member(self, channel_id: str, client_id: str, x25519_public_key: bytes) -> None:
        with self._conn:
            self._conn.execute(
                "INSERT INTO channel_members (channel_id, client_id, x25519_public_key) VALUES (?, ?, ?) "
                "ON CONFLICT(channel_id, client_id) DO UPDATE SET x25519_public_key = excluded.x25519_public_key",
                (channel_id, client_id, bytes(x25519_public_key)),
            )

    async def remove_member(self, channel_id: str, client_id: str) -> None:
        with self._conn:
            self._conn.execute("DELETE FROM channel_members WHERE channel_id = ? AND client_id = ?", (channel_id, client_id))

    def close(self) -> None:
        self._conn.close()
