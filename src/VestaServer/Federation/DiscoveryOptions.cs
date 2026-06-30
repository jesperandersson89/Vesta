namespace VestaServer.Federation;

/// <summary>
/// Configuration for opt-in server-to-server discovery (federation), bound from the
/// <c>Discovery</c> configuration section. When <see cref="Enabled"/> is false (the default)
/// the relay exposes no <c>/federation/*</c> surface and never gossips — it stays a private relay.
/// </summary>
public sealed class DiscoveryOptions
{
    /// <summary>Master switch. When false, no federation endpoints are mapped and no gossip runs.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Seed peer base URLs (e.g. <c>https://relay-b.example</c>) used to bootstrap the gossip mesh.
    /// The gossip service pulls <c>/federation/descriptor</c> and <c>/federation/peers</c> from these.
    /// </summary>
    public List<string> Seeds { get; set; } = [];

    /// <summary>
    /// This relay's publicly reachable WebSocket URLs (e.g. <c>wss://relay.example/ws</c>), advertised
    /// in its descriptor. Required when <see cref="Enabled"/> — a relay behind NAT cannot infer this.
    /// </summary>
    public List<string> PublicUrls { get; set; } = [];

    /// <summary>
    /// The relay's Ed25519 signing key (base64url-encoded 32-byte seed) used to sign its descriptor.
    /// If null/empty while enabled, the relay auto-generates one and persists it under the content root
    /// (dev convenience). In production, supply it from a secret store.
    /// </summary>
    public string? SigningKey { get; set; }

    /// <summary>How often (seconds) the gossip service pulls from seeds + known peers.</summary>
    public int GossipIntervalSeconds { get; set; } = 60;

    /// <summary>How long (seconds) this relay's own descriptor stays valid before peers should re-pull it.</summary>
    public int DescriptorTtlSeconds { get; set; } = 300;

    /// <summary>Upper bound on descriptors retained in the directory (anti-flooding).</summary>
    public int MaxPeers { get; set; } = 256;
}
