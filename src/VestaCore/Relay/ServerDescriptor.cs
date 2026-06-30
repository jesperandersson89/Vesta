namespace VestaCore.Relay;

/// <summary>
/// An app a relay advertises as discoverable. Only apps whose owner has opted in (the per-app
/// <c>discoverable</c> flag) and whose host relay has discovery enabled appear here. The
/// <see cref="OwnerClientId"/> lets a client cross-check a discovered relay against the app's
/// compiled-in trust anchor: derive the expected client id from <c>VestaAppConfig.OwnerPublicKey</c>
/// (<c>VestaIdentity.DeriveClientId</c>) and drop the relay if it does not match.
/// </summary>
/// <param name="AppId">The app namespace the relay hosts.</param>
/// <param name="OwnerClientId">The client id recorded as the app's owner on that relay (a deterministic hash of the owner's Ed25519 public key).</param>
public sealed record DiscoverableApp(string AppId, string OwnerClientId);

/// <summary>
/// A relay's self-asserted, signed advertisement of itself for server-to-server discovery
/// (federation). A discovery-enabled relay publishes this at <c>GET /federation/descriptor</c>;
/// peers gossip it by anti-entropy pull so a client reaching any relay in the mesh can learn
/// which relays host a given app — without a central hub.
///
/// Trust model: the descriptor is <b>self-signed</b> — the relay declares its own
/// <see cref="RelayPublicKey"/> and signs with the matching private key, so a descriptor relayed
/// through untrusted peers cannot be tampered with in flight. The relay key is just an identity;
/// a relay can still <i>lie</i> about which apps it hosts, which is why discovered relays are
/// presented to the user as unverified (never auto-adopted) and clients cross-check each
/// advertised <see cref="DiscoverableApp.OwnerClientId"/> against the app's own trust anchor.
/// </summary>
public sealed record ServerDescriptor
{
    /// <summary>The base64url-encoded Ed25519 public key that identifies this relay and signs the descriptor.</summary>
    public required string RelayPublicKey { get; init; }

    /// <summary>The relay's publicly reachable WebSocket URLs (e.g. <c>wss://relay.example/ws</c>), in preference order.</summary>
    public required IReadOnlyList<string> Urls { get; init; }

    /// <summary>The discoverable apps this relay hosts. May be empty (the relay still participates in the mesh).</summary>
    public IReadOnlyList<DiscoverableApp> Apps { get; init; } = [];

    /// <summary>When the descriptor was issued. Combined with <see cref="TtlSeconds"/> to expire stale gossip.</summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>How long (seconds) the descriptor stays valid. Peers evict it from their directory once expired.</summary>
    public required int TtlSeconds { get; init; }

    /// <summary>The base64url-encoded Ed25519 signature over the canonical descriptor. Null until signed.</summary>
    public string? Signature { get; init; }

    /// <summary>True once <see cref="IssuedAt"/> plus <see cref="TtlSeconds"/> has passed relative to <paramref name="now"/>.</summary>
    public bool IsExpired(DateTimeOffset now) => now > IssuedAt.AddSeconds(TtlSeconds);
}
