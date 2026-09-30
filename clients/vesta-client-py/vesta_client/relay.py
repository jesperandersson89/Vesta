"""
Relay independence: owner-signed relay manifests, candidate resolution, and the
per-user override — the Python mirror of the C# ``VestaCore.Relay`` /
``VestaClient.Relay`` types and the TypeScript ``relay.ts`` module.

The manifest signing input MUST match the C# ``ManifestSigner.BuildSigningInput``
byte-for-byte (RFC 8785 JCS + Ed25519) so manifests verify across all three clients.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field, replace
from datetime import datetime, timezone
from pathlib import Path
from typing import TYPE_CHECKING, Any, Protocol

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey

from vesta_client.identity import VestaIdentity, b64url_decode, b64url_encode
from vesta_client.signing import _canonicalize, normalize_timestamp_for_signing

if TYPE_CHECKING:
    from vesta_client.federation import DiscoveredRelay

RELAY_MANIFEST_EVENT_TYPE = "vesta.relay-manifest"


def manifest_channel_for(app_id: str) -> str:
    """The channel an app's relay manifest is published to: ``{appId}/vesta/relays``."""
    return f"{app_id}/vesta/relays"


# ── Types ──────────────────────────────────────────────────────────────────────


@dataclass
class RelayEndpoint:
    url: str
    priority: int
    label: str | None = None


@dataclass
class EscapeFallback:
    url: str
    valid_from: str  # ISO 8601


@dataclass
class RelayManifest:
    app_id: str
    version: int
    issued_at: str
    relays: list[RelayEndpoint]
    owner_public_key: str
    escape_fallbacks: list[EscapeFallback] = field(default_factory=list)
    signature: str | None = None

    def to_dict(self) -> dict[str, Any]:
        return {
            "appId": self.app_id,
            "version": self.version,
            "issuedAt": self.issued_at,
            "relays": [
                {"url": r.url, "priority": r.priority, "label": r.label}
                for r in self.relays
            ],
            "escapeFallbacks": [
                {"url": f.url, "validFrom": f.valid_from} for f in self.escape_fallbacks
            ],
            "ownerPublicKey": self.owner_public_key,
            "signature": self.signature,
        }

    @staticmethod
    def from_dict(d: dict[str, Any]) -> "RelayManifest":
        return RelayManifest(
            app_id=d["appId"],
            version=d["version"],
            issued_at=d["issuedAt"],
            relays=[
                RelayEndpoint(url=r["url"], priority=r["priority"], label=r.get("label"))
                for r in d.get("relays", [])
            ],
            escape_fallbacks=[
                EscapeFallback(url=f["url"], valid_from=f["validFrom"])
                for f in d.get("escapeFallbacks", [])
            ],
            owner_public_key=d.get("ownerPublicKey", ""),
            signature=d.get("signature"),
        )


@dataclass
class VestaAppConfig:
    """App-level config compiled into the app for relay-independent coordination."""

    app_id: str
    owner_public_key: str  # base64url — the manifest trust anchor
    default_relays: list[str]
    # Extra relay URLs to ask for peers when recovery runs; never adopted directly.
    discovery_seeds: list[str] = field(default_factory=list)


@dataclass
class RelayAttempt:
    """One failed connection attempt to a relay and why it failed."""

    relay: str
    reason: str


@dataclass
class RelaysExhaustedInfo:
    """Raised once per outage when every candidate has failed several full passes."""

    attempts: list[RelayAttempt]
    passes: int


class RelaysExhaustedError(RuntimeError):
    """Raised by ``connect()`` when no relay candidate could be reached."""

    def __init__(self, info: RelaysExhaustedInfo) -> None:
        super().__init__(f"Could not connect to any of the {len(info.attempts)} configured relay(s).")
        self.info: RelaysExhaustedInfo = info


# ── Manifest signing / verification ─────────────────────────────────────────────


def build_manifest_signing_input(manifest: RelayManifest) -> bytes:
    """Canonical signing-input bytes (matches C# ManifestSigner.BuildSigningInput)."""
    fields: dict[str, Any] = {
        "appId": manifest.app_id,
        "escapeFallbacks": [
            {"url": f.url, "validFrom": normalize_timestamp_for_signing(f.valid_from)}
            for f in manifest.escape_fallbacks
        ],
        "issuedAt": normalize_timestamp_for_signing(manifest.issued_at),
        "ownerPublicKey": manifest.owner_public_key,
        "relays": [
            {"label": r.label, "priority": r.priority, "url": r.url}
            for r in manifest.relays
        ],
        "version": manifest.version,
    }
    return _canonicalize(fields).encode("utf-8")


def sign_manifest(manifest: RelayManifest, identity: VestaIdentity) -> RelayManifest:
    """Return a signed copy of the manifest with owner key + signature populated."""
    owner_public_key = identity.public_key_b64
    if manifest.owner_public_key and manifest.owner_public_key != owner_public_key:
        raise ValueError(
            f"Manifest ownerPublicKey '{manifest.owner_public_key}' does not match "
            f"signing identity '{owner_public_key}'."
        )

    with_owner = replace(manifest, owner_public_key=owner_public_key, signature=None)
    signature = identity.sign_b64(build_manifest_signing_input(with_owner))
    return replace(with_owner, signature=signature)


def verify_manifest(manifest: RelayManifest, expected_owner_public_key_b64: str) -> bool:
    """
    Verify a manifest against the expected owner public key (base64url) — the app's
    compiled-in trust anchor. True only if the declared owner matches AND the signature verifies.
    """
    if not manifest.signature:
        return False
    if manifest.owner_public_key != expected_owner_public_key_b64:
        return False

    try:
        pub = Ed25519PublicKey.from_public_bytes(b64url_decode(expected_owner_public_key_b64))
        pub.verify(b64url_decode(manifest.signature), build_manifest_signing_input(manifest))
        return True
    except (InvalidSignature, ValueError):
        return False


# ── Candidate resolution ─────────────────────────────────────────────────────────


def resolve_relay_candidates(
    defaults: list[str],
    user_override: str | None = None,
    manifest_relays: list[str] | None = None,
) -> list[str]:
    """
    Merge override + manifest relays + defaults into one ordered, de-duplicated candidate list.
    Precedence: user override → manifest relays → app defaults.
    """
    ordered: list[str] = []

    def add(url: str | None) -> None:
        if url and url not in ordered:
            ordered.append(url)

    add(user_override)
    if manifest_relays:
        for url in manifest_relays:
            add(url)
    for url in defaults:
        add(url)

    if not ordered:
        raise ValueError("No relay candidates could be resolved — defaults were empty.")
    return ordered


def _extract_manifest_relays(manifest: RelayManifest) -> list[str]:
    urls: list[str] = []
    for endpoint in sorted(manifest.relays, key=lambda r: r.priority):
        urls.append(endpoint.url)
    now = datetime.now(timezone.utc)
    for fallback in manifest.escape_fallbacks:
        valid_from = datetime.fromisoformat(fallback.valid_from.replace("Z", "+00:00"))
        if valid_from <= now:
            urls.append(fallback.url)
    return urls


# ── Override + manifest stores ───────────────────────────────────────────────────


class RelayOverrideStore(Protocol):
    """Persists the user's local relay override — the individual escape hatch."""

    def get_override(self) -> str | None: ...
    def set_override(self, url: str) -> None: ...
    def clear_override(self) -> None: ...


class ManifestStore(Protocol):
    """Caches the latest verified manifest."""

    def get_cached(self) -> RelayManifest | None: ...
    def save(self, manifest: RelayManifest) -> None: ...


class InMemoryRelayOverrideStore:
    def __init__(self) -> None:
        self._override: str | None = None

    def get_override(self) -> str | None:
        return self._override

    def set_override(self, url: str) -> None:
        self._override = url

    def clear_override(self) -> None:
        self._override = None


class InMemoryManifestStore:
    def __init__(self) -> None:
        self._manifest: RelayManifest | None = None

    def get_cached(self) -> RelayManifest | None:
        return self._manifest

    def save(self, manifest: RelayManifest) -> None:
        self._manifest = manifest


class FileRelayOverrideStore:
    """A JSON-file-backed override store (e.g. ``~/.vesta/{prefix}-relays.json``)."""

    def __init__(self, path: str | Path) -> None:
        self._path = Path(path)

    def get_override(self) -> str | None:
        if not self._path.exists():
            return None
        try:
            data = json.loads(self._path.read_text(encoding="utf-8"))
            value = data.get("relay")
            return value if isinstance(value, str) and value else None
        except (json.JSONDecodeError, OSError):
            return None

    def set_override(self, url: str) -> None:
        self._path.parent.mkdir(parents=True, exist_ok=True)
        self._path.write_text(json.dumps({"relay": url}, indent=2), encoding="utf-8")

    def clear_override(self) -> None:
        if self._path.exists():
            self._path.unlink()


class FileManifestStore:
    """A JSON-file-backed manifest cache (e.g. ``~/.vesta/{prefix}-manifest.json``)."""

    def __init__(self, path: str | Path) -> None:
        self._path = Path(path)

    def get_cached(self) -> RelayManifest | None:
        if not self._path.exists():
            return None
        try:
            return RelayManifest.from_dict(json.loads(self._path.read_text(encoding="utf-8")))
        except (json.JSONDecodeError, OSError, KeyError):
            return None

    def save(self, manifest: RelayManifest) -> None:
        self._path.parent.mkdir(parents=True, exist_ok=True)
        self._path.write_text(json.dumps(manifest.to_dict(), indent=2), encoding="utf-8")


class PeerCacheStore(Protocol):
    """Remembers verified federation peers so recovery has hints when every known relay is down."""

    def load(self) -> "list[DiscoveredRelay]": ...
    def save(self, peers: "list[DiscoveredRelay]") -> None: ...


class InMemoryPeerCacheStore:
    def __init__(self) -> None:
        self._peers: list[DiscoveredRelay] = []

    def load(self) -> "list[DiscoveredRelay]":
        return list(self._peers)

    def save(self, peers: "list[DiscoveredRelay]") -> None:
        self._peers = list(peers)


class FilePeerCacheStore:
    """A JSON-file-backed peer cache (e.g. ``~/.vesta/relays/{appId}.peers.json``)."""

    def __init__(self, path: str | Path) -> None:
        self._path = Path(path)

    def load(self) -> "list[DiscoveredRelay]":
        from vesta_client.federation import DiscoveredRelay

        try:
            data = json.loads(self._path.read_text(encoding="utf-8"))
            return [
                DiscoveredRelay(
                    relay_public_key=d["relayPublicKey"],
                    urls=list(d["urls"]),
                    hosts_requested_app=bool(d["hostsRequestedApp"]),
                    issued_at=d["issuedAt"],
                )
                for d in data
            ]
        except (OSError, ValueError, KeyError, TypeError):
            return []

    def save(self, peers: "list[DiscoveredRelay]") -> None:
        self._path.parent.mkdir(parents=True, exist_ok=True)
        payload = [
            {
                "relayPublicKey": p.relay_public_key,
                "urls": p.urls,
                "hostsRequestedApp": p.hosts_requested_app,
                "issuedAt": p.issued_at,
            }
            for p in peers[:64]
        ]
        self._path.write_text(json.dumps(payload, indent=2), encoding="utf-8")


# ── RelayDirectory ───────────────────────────────────────────────────────────────


class RelayDirectory:
    """
    Turns an app config, the user override, and the latest owner-signed manifest into an
    ordered relay candidate list — and decides whether to trust an incoming manifest.
    """

    def __init__(
        self,
        config: VestaAppConfig,
        override_store: RelayOverrideStore | None = None,
        manifest_store: ManifestStore | None = None,
        peer_cache_store: PeerCacheStore | None = None,
    ) -> None:
        self._config = config
        self._override_store = override_store
        self._manifest_store = manifest_store
        self._peer_cache_store = peer_cache_store
        self._current: RelayManifest | None = None

        cached = manifest_store.get_cached() if manifest_store else None
        if (
            cached is not None
            and cached.app_id == config.app_id
            and verify_manifest(cached, config.owner_public_key)
        ):
            self._current = cached

    @property
    def config(self) -> VestaAppConfig:
        return self._config

    @property
    def peer_cache(self) -> PeerCacheStore | None:
        return self._peer_cache_store

    @property
    def active_override(self) -> str | None:
        """The user's saved relay override, if any."""
        return self._override_store.get_override() if self._override_store else None

    @property
    def manifest_channel(self) -> str:
        return manifest_channel_for(self._config.app_id)

    @property
    def current_manifest(self) -> RelayManifest | None:
        return self._current

    def resolve_candidates(self) -> list[str]:
        override = self._override_store.get_override() if self._override_store else None
        manifest_relays = _extract_manifest_relays(self._current) if self._current else None
        return resolve_relay_candidates(self._config.default_relays, override, manifest_relays)

    def try_apply_manifest(self, manifest: RelayManifest) -> bool:
        if manifest.app_id != self._config.app_id:
            return False
        if not verify_manifest(manifest, self._config.owner_public_key):
            return False
        if self._current is not None and manifest.version <= self._current.version:
            return False

        self._current = manifest
        if self._manifest_store is not None:
            self._manifest_store.save(manifest)
        return True

    def set_user_override(self, url: str) -> None:
        if self._override_store is None:
            raise RuntimeError("No relay override store was configured.")
        self._override_store.set_override(url)

    def clear_user_override(self) -> None:
        if self._override_store is None:
            raise RuntimeError("No relay override store was configured.")
        self._override_store.clear_override()
