namespace VestaCore.Relay;

/// <summary>
/// A single relay endpoint advertised by a <see cref="RelayManifest"/>.
/// </summary>
/// <param name="Url">The relay's WebSocket URL (e.g. <c>wss://relay.example/ws</c>).</param>
/// <param name="Priority">Lower numbers are preferred. Clients try relays in ascending priority order.</param>
/// <param name="Label">Optional human-readable label for diagnostics / UI.</param>
public sealed record RelayEndpoint(string Url, int Priority, string? Label = null);

/// <summary>
/// A pre-signed escape fallback relay. Because it is part of the owner-signed manifest, an
/// owner can stage a future migration target in advance; clients may begin trusting it once
/// <paramref name="ValidFrom"/> has passed even if they never reach the primary relay again.
/// </summary>
/// <param name="Url">The fallback relay's WebSocket URL.</param>
/// <param name="ValidFrom">The instant from which clients may treat this fallback as usable.</param>
public sealed record EscapeFallback(string Url, DateTimeOffset ValidFrom);

/// <summary>
/// An owner-signed declaration of where an app's swarm should rendezvous. Published as a normal
/// signed event to the app's manifest channel (see <see cref="ChannelFor"/>) so every client can
/// discover relay changes without any relay-side cooperation — the relay stays a pure relay.
///
/// Trust model: the <see cref="OwnerPublicKey"/> is the anchor. A client only accepts a manifest
/// whose <see cref="OwnerPublicKey"/> matches the app's compiled-in owner key AND whose
/// <see cref="Signature"/> verifies against it. <see cref="Version"/> is a monotonic counter — the
/// newest valid version wins and lower versions are rejected (anti-rollback).
/// </summary>
public sealed record RelayManifest
{
    /// <summary>The app this manifest governs. Must match the manifest channel's app segment.</summary>
    public required string AppId { get; init; }

    /// <summary>
    /// Monotonic version counter. Clients keep the highest valid version they have seen and reject
    /// any manifest with a lower version (rollback protection).
    /// </summary>
    public required int Version { get; init; }

    /// <summary>When the manifest was issued. Informational / display only — not used for ordering.</summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>The relays the owner currently designates, in preference order.</summary>
    public required IReadOnlyList<RelayEndpoint> Relays { get; init; }

    /// <summary>Pre-signed fallbacks clients may adopt once their <see cref="EscapeFallback.ValidFrom"/> passes.</summary>
    public IReadOnlyList<EscapeFallback> EscapeFallbacks { get; init; } = [];

    /// <summary>The base64url-encoded Ed25519 public key of the app owner — the trust anchor.</summary>
    public required string OwnerPublicKey { get; init; }

    /// <summary>The base64url-encoded Ed25519 signature over the canonical manifest. Null until signed.</summary>
    public string? Signature { get; init; }

    /// <summary>The reserved event type used for manifest events.</summary>
    public const string EventType = "vesta.relay-manifest";

    /// <summary>
    /// The channel an app's relay manifest is published to: <c>{appId}/vesta/relays</c>.
    /// This is a normal app channel (it does not start with the reserved <c>vesta/</c> prefix),
    /// so apps can write to it, but by convention only the owner's signed manifests are trusted.
    /// </summary>
    public static string ChannelFor(string appId) => $"{appId}/vesta/relays";
}
