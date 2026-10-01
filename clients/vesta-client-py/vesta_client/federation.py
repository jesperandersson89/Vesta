"""
Server-to-server discovery (federation) — the Python mirror of the C#
``VestaCore.Relay.ServerDescriptor`` / ``VestaCore.Relay.DescriptorSigner`` /
``VestaClient.Federation.FederationClient``.

A discovery-enabled relay self-signs and publishes a ``ServerDescriptor`` advertising itself
and the discoverable apps it hosts. Peers gossip these by anti-entropy pull, so a client
reaching any relay in the mesh can learn which relays host a given app — without a central
hub. Discovered relays are always show-only: verified but never auto-adopted.
"""

from __future__ import annotations

import asyncio
import json
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from typing import Any, Callable

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey

from vesta_client.identity import b64url_decode, derive_client_id
from vesta_client.relay import VestaAppConfig
from vesta_client.signing import _canonicalize, normalize_timestamp_for_signing


@dataclass
class DiscoverableApp:
    """An app a relay advertises as discoverable."""

    app_id: str
    owner_client_id: str


@dataclass
class ServerDescriptor:
    """A relay's self-asserted, signed advertisement of itself for federation."""

    relay_public_key: str
    urls: list[str]
    issued_at: str
    ttl_seconds: int
    apps: list[DiscoverableApp] = field(default_factory=list)
    signature: str | None = None
    accepts_unregistered_apps: bool | None = None

    def is_expired(self, now: datetime | None = None) -> bool:
        now = now or datetime.now(timezone.utc)
        issued = datetime.fromisoformat(self.issued_at.replace("Z", "+00:00"))
        return now > issued + timedelta(seconds=self.ttl_seconds)

    @staticmethod
    def from_dict(d: dict[str, Any]) -> "ServerDescriptor":
        return ServerDescriptor(
            relay_public_key=d["relayPublicKey"],
            urls=list(d.get("urls", [])),
            issued_at=d["issuedAt"],
            ttl_seconds=d["ttlSeconds"],
            apps=[
                DiscoverableApp(app_id=a["appId"], owner_client_id=a["ownerClientId"])
                for a in d.get("apps", [])
            ],
            signature=d.get("signature"),
            accepts_unregistered_apps=d.get("acceptsUnregisteredApps"),
        )


@dataclass
class DiscoveredRelay:
    """A relay discovered through federation — verified but never auto-adopted."""

    relay_public_key: str
    urls: list[str]
    hosts_requested_app: bool
    issued_at: str
    accepts_unregistered_apps: bool | None = None


def build_descriptor_signing_input(descriptor: ServerDescriptor) -> bytes:
    """Canonical signing-input bytes (matches C# DescriptorSigner.BuildSigningInput)."""
    fields: dict[str, Any] = {
        "apps": [{"appId": a.app_id, "ownerClientId": a.owner_client_id} for a in descriptor.apps],
        "issuedAt": normalize_timestamp_for_signing(descriptor.issued_at),
        "relayPublicKey": descriptor.relay_public_key,
        "ttlSeconds": descriptor.ttl_seconds,
        "urls": list(descriptor.urls),
    }
    if isinstance(descriptor.accepts_unregistered_apps, bool):
        fields["acceptsUnregisteredApps"] = descriptor.accepts_unregistered_apps
    return _canonicalize(fields).encode("utf-8")


def verify_descriptor(descriptor: ServerDescriptor) -> bool:
    """Verify a descriptor's self-signature. Proves authorship, not trustworthiness of its claims."""
    if not descriptor.signature or not descriptor.relay_public_key:
        return False
    try:
        pub = Ed25519PublicKey.from_public_bytes(b64url_decode(descriptor.relay_public_key))
        pub.verify(b64url_decode(descriptor.signature), build_descriptor_signing_input(descriptor))
        return True
    except (InvalidSignature, ValueError):
        return False


def _default_fetch(url: str) -> list[dict[str, Any]]:
    request = urllib.request.Request(url, headers={"Accept": "application/json"})
    with urllib.request.urlopen(request, timeout=10) as response:
        body = json.loads(response.read())
    return body if isinstance(body, list) else []


class FederationClient:
    """
    Pulls signed ``ServerDescriptor`` records from a relay's public ``/federation/*`` HTTP
    surface so a client whose relays are failing can discover other relays in the mesh.

    Every descriptor is verified two ways before being surfaced: (1) signature — proves the
    relay key authored it; (2) ownership — the advertised ``owner_client_id`` is compared
    against the client id derived from the app's compiled-in ``owner_public_key``; a relay
    claiming to host the app under a different owner is dropped as a spoof.
    """

    def __init__(
        self,
        app_config: VestaAppConfig,
        fetch: Callable[[str], list[dict[str, Any]]] | None = None,
    ) -> None:
        self._app_config = app_config
        self._expected_owner_client_id = derive_client_id(b64url_decode(app_config.owner_public_key))
        self._fetch = fetch or _default_fetch

    async def discover_relays_for_app(self, federation_base_url: str) -> list[DiscoveredRelay]:
        """Ask a reachable relay which relays (itself included) host this app."""
        url = urllib.parse.urljoin(
            federation_base_url, f"/federation/apps/{urllib.parse.quote(self._app_config.app_id)}"
        )
        descriptors = await self._fetch_descriptors(url)

        results: list[DiscoveredRelay] = []
        for descriptor in descriptors:
            if not self._is_authentic(descriptor):
                continue
            if not self._advertises_app_for_owner(descriptor):
                continue
            results.append(self._to_discovered_relay(descriptor, hosts_requested_app=True))
        return self._order_newest_first(results)

    async def list_all_relays(self, federation_base_url: str) -> list[DiscoveredRelay]:
        """Browse every relay a reachable relay knows about."""
        url = urllib.parse.urljoin(federation_base_url, "/federation/peers")
        descriptors = await self._fetch_descriptors(url)

        results: list[DiscoveredRelay] = []
        for descriptor in descriptors:
            if not self._is_authentic(descriptor):
                continue
            hosts_app = self._advertises_app_for_owner(descriptor)
            results.append(self._to_discovered_relay(descriptor, hosts_requested_app=hosts_app))
        return self._order_newest_first(results)

    async def _fetch_descriptors(self, url: str) -> list[ServerDescriptor]:
        try:
            raw = await asyncio.to_thread(self._fetch, url)
        except (urllib.error.URLError, TimeoutError, OSError, ValueError):
            return []
        try:
            return [ServerDescriptor.from_dict(d) for d in raw]
        except (KeyError, TypeError):
            return []

    def _is_authentic(self, descriptor: ServerDescriptor) -> bool:
        return not descriptor.is_expired() and verify_descriptor(descriptor)

    def _advertises_app_for_owner(self, descriptor: ServerDescriptor) -> bool:
        return any(
            a.app_id == self._app_config.app_id and a.owner_client_id == self._expected_owner_client_id
            for a in descriptor.apps
        )

    @staticmethod
    def _to_discovered_relay(descriptor: ServerDescriptor, hosts_requested_app: bool) -> DiscoveredRelay:
        return DiscoveredRelay(
            relay_public_key=descriptor.relay_public_key,
            urls=list(descriptor.urls),
            hosts_requested_app=hosts_requested_app,
            issued_at=descriptor.issued_at,
            accepts_unregistered_apps=descriptor.accepts_unregistered_apps,
        )

    @staticmethod
    def _order_newest_first(relays: list[DiscoveredRelay]) -> list[DiscoveredRelay]:
        return sorted(relays, key=lambda r: r.issued_at, reverse=True)

    @staticmethod
    def to_federation_base_url(relay_url: str) -> str | None:
        """
        Derive the HTTP(S) federation base URL from a relay's WebSocket URL: ``ws`` → ``http``,
        ``wss`` → ``https``, with any path/query stripped.
        """
        parsed = urllib.parse.urlparse(relay_url)
        scheme = {"ws": "http", "http": "http", "wss": "https", "https": "https"}.get(parsed.scheme)
        if not scheme or not parsed.netloc:
            return None
        return f"{scheme}://{parsed.netloc}/"


__all__ = [
    "DiscoverableApp",
    "DiscoveredRelay",
    "FederationClient",
    "ServerDescriptor",
    "build_descriptor_signing_input",
    "verify_descriptor",
]
