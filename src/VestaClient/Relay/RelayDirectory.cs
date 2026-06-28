using VestaCore.Relay;

namespace VestaClient.Relay;

/// <summary>
/// The client-side policy that turns an app's <see cref="VestaAppConfig"/>, the user's local
/// override, and the latest owner-signed <see cref="RelayManifest"/> into an ordered relay
/// candidate list — and that decides whether to trust an incoming manifest.
///
/// This is where the two coordination layers meet: an owner-signed manifest steers the whole
/// swarm to new relays, while the user's local override is an individual escape hatch that always
/// wins locally. The relay never participates — manifests are ordinary signed events verified here.
/// </summary>
public sealed class RelayDirectory
{
    private readonly VestaAppConfig _config;
    private readonly IRelayOverrideStore? _overrideStore;
    private readonly IManifestStore? _manifestStore;
    private RelayManifest? _currentManifest;

    public RelayDirectory(
        VestaAppConfig config,
        IRelayOverrideStore? overrideStore = null,
        IManifestStore? manifestStore = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        _config = config;
        _overrideStore = overrideStore;
        _manifestStore = manifestStore;

        // Load any cached manifest, but only trust it if it still verifies against the
        // app's compiled-in owner key — a tampered cache must never influence resolution.
        RelayManifest? cached = manifestStore?.GetCached();
        if (cached is not null &&
            cached.AppId == config.AppId &&
            ManifestSigner.Verify(cached, config.OwnerPublicKey))
        {
            _currentManifest = cached;
        }
    }

    /// <summary>The channel this app's relay manifests are published to.</summary>
    public string ManifestChannel => RelayManifest.ChannelFor(_config.AppId);

    /// <summary>The latest verified manifest, or null if none has been accepted yet.</summary>
    public RelayManifest? CurrentManifest => _currentManifest;

    /// <summary>
    /// Resolve the ordered relay candidate list from the user override, the current manifest, and
    /// the app defaults, in that precedence.
    /// </summary>
    public IReadOnlyList<Uri> ResolveCandidates()
    {
        Uri? userOverride = _overrideStore?.GetOverride();
        IReadOnlyList<Uri>? manifestRelays = _currentManifest is null
            ? null
            : ExtractRelays(_currentManifest);

        return RelayResolver.Resolve(_config.DefaultRelays, userOverride, manifestRelays);
    }

    /// <summary>
    /// Validate and possibly adopt an incoming manifest. Returns true only if the manifest is
    /// signed by the trusted owner, targets this app, and is strictly newer than the current one
    /// (anti-rollback). Accepted manifests are persisted.
    /// </summary>
    public bool TryApplyManifest(RelayManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.AppId != _config.AppId)
        {
            return false;
        }
        if (!ManifestSigner.Verify(manifest, _config.OwnerPublicKey))
        {
            return false;
        }
        if (_currentManifest is not null && manifest.Version <= _currentManifest.Version)
        {
            return false;
        }

        _currentManifest = manifest;
        _manifestStore?.Save(manifest);
        return true;
    }

    /// <summary>Set the user's local relay override. Requires an override store.</summary>
    public void SetUserOverride(Uri relay)
    {
        if (_overrideStore is null)
        {
            throw new InvalidOperationException("No relay override store was configured.");
        }
        _overrideStore.SetOverride(relay);
    }

    /// <summary>Clear the user's local relay override. Requires an override store.</summary>
    public void ClearUserOverride()
    {
        if (_overrideStore is null)
        {
            throw new InvalidOperationException("No relay override store was configured.");
        }
        _overrideStore.ClearOverride();
    }

    private static IReadOnlyList<Uri> ExtractRelays(RelayManifest manifest)
    {
        List<Uri> uris = new();

        foreach (RelayEndpoint endpoint in manifest.Relays.OrderBy(r => r.Priority))
        {
            if (Uri.TryCreate(endpoint.Url, UriKind.Absolute, out Uri? uri))
            {
                uris.Add(uri);
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (EscapeFallback fallback in manifest.EscapeFallbacks)
        {
            if (fallback.ValidFrom <= now && Uri.TryCreate(fallback.Url, UriKind.Absolute, out Uri? uri))
            {
                uris.Add(uri);
            }
        }

        return uris;
    }
}
