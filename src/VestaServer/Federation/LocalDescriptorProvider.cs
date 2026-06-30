using Microsoft.Extensions.Options;
using VestaCore.Identity;
using VestaCore.Relay;
using VestaCore.Utilities;
using VestaServer.Storage;

namespace VestaServer.Federation;

/// <summary>
/// Builds and signs this relay's own <see cref="ServerDescriptor"/> on demand: its public URLs plus
/// the apps whose owners have opted into discovery (<see cref="AppInfo.Discoverable"/>). Signed with
/// the relay's Ed25519 identity so the descriptor can be gossiped through untrusted peers intact.
/// </summary>
public sealed class LocalDescriptorProvider(
    IAppStore appStore,
    IOptions<DiscoveryOptions> options,
    VestaIdentity relayIdentity,
    TimeProvider timeProvider)
{
    private readonly DiscoveryOptions _options = options.Value;

    /// <summary>The base64url-encoded Ed25519 public key identifying this relay.</summary>
    public string RelayPublicKey { get; } = Base64Url.Encode(relayIdentity.PublicKey);

    /// <summary>Build a freshly-signed descriptor reflecting the current set of discoverable apps.</summary>
    public async Task<ServerDescriptor> BuildAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AppInfo> apps = await appStore.ListAsync(cancellationToken);
        List<DiscoverableApp> discoverable = [.. apps
            .Where(a => a.Discoverable)
            .Select(a => new DiscoverableApp(a.Id, a.OwnerClientId))];

        ServerDescriptor descriptor = new()
        {
            RelayPublicKey = RelayPublicKey,
            Urls = [.. _options.PublicUrls],
            Apps = discoverable,
            IssuedAt = timeProvider.GetUtcNow(),
            TtlSeconds = _options.DescriptorTtlSeconds,
        };

        return DescriptorSigner.Sign(descriptor, relayIdentity);
    }
}

/// <summary>
/// Resolves the relay's federation signing identity: from the configured base64url
/// <see cref="DiscoveryOptions.SigningKey"/> when present, otherwise a key auto-generated once and
/// persisted under <c>{contentRoot}/.vesta/relay-key.json</c> (dev convenience; supply a real secret
/// in production).
/// </summary>
public static class RelayIdentityLoader
{
    public static VestaIdentity LoadOrCreate(DiscoveryOptions options, string contentRootPath, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.SigningKey))
        {
            byte[] seed = Base64Url.Decode(options.SigningKey.Trim());
            return VestaIdentity.FromPrivateKey(seed);
        }

        string dir = Path.Combine(contentRootPath, ".vesta");
        Directory.CreateDirectory(dir);
        string keyPath = Path.Combine(dir, "relay-key.json");
        bool existed = File.Exists(keyPath);
        VestaIdentity identity = VestaIdentity.LoadOrCreate(keyPath);
        if (!existed)
        {
            logger.LogWarning(
                "Discovery: no Discovery:SigningKey configured — generated a relay signing key at {Path}. " +
                "Supply a persistent key via Discovery:SigningKey (or a secret store) in production.",
                keyPath);
        }
        return identity;
    }
}
