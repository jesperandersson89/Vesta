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

    /// <summary>
    /// Create a directory wired to the default file-backed stores — the user override and the
    /// manifest cache are persisted under <c>~/.vesta/relays/</c>, keyed by the app id. This is the
    /// zero-config path used by <see cref="VestaConnection"/> so relay self-reconfiguration (manifest
    /// adoption + the local escape hatch) survives restarts without any per-app plumbing.
    /// </summary>
    public static RelayDirectory CreateDefault(VestaAppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        string directory = DefaultStoreDirectory();
        string safeAppId = SanitizeAppId(config.AppId);

        return new RelayDirectory(
            config,
            new FileRelayOverrideStore(Path.Combine(directory, $"{safeAppId}.override.json")),
            new FileManifestStore(Path.Combine(directory, $"{safeAppId}.manifest.json")));
    }

    private static string DefaultStoreDirectory()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".vesta", "relays");
    }

    private static string SanitizeAppId(string appId)
    {
        char[] chars = appId.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }
        return new string(chars);
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
