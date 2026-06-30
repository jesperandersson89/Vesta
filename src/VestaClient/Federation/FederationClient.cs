using System.Net.Http.Json;
using System.Text.Json;
using VestaClient.Relay;
using VestaCore.Identity;
using VestaCore.Relay;

namespace VestaClient.Federation;

/// <summary>
/// A relay this client discovered through server-to-server federation. It is <b>show-only</b>:
/// the descriptor's signature has been verified (the relay really authored it) and the app's
/// owner client id matched the app's compiled-in trust anchor, but the client still cannot prove
/// the relay actually carries the app's data. The user adopts a discovered relay manually (via
/// <see cref="VestaConnection.SetUserRelayOverrideAsync"/>); discovery never auto-fails-over here —
/// owner-signed manifest relays remain the only automatic tier.
/// </summary>
/// <param name="RelayPublicKey">The base64url Ed25519 public key the relay signs its descriptor with.</param>
/// <param name="Urls">The relay's advertised WebSocket URLs, in preference order.</param>
/// <param name="HostsRequestedApp">
/// True when this relay advertised the specific app that was queried <i>and</i> the advertised
/// owner client id matched the app's trust anchor. False for relays returned by a "list all"
/// browse that do not (verifiably) host the app.
/// </param>
/// <param name="IssuedAt">When the relay issued the descriptor.</param>
public sealed record DiscoveredRelay(
    string RelayPublicKey,
    IReadOnlyList<Uri> Urls,
    bool HostsRequestedApp,
    DateTimeOffset IssuedAt);

/// <summary>
/// Pulls signed <see cref="ServerDescriptor"/> records from a relay's public <c>/federation/*</c>
/// HTTP surface so a client whose relays are failing can discover <i>other</i> relays in the mesh —
/// without any central hub. This is the read side of the gossip network: any single reachable
/// relay can answer "who else hosts this app?" because relays anti-entropy-gossip each other's
/// descriptors.
///
/// Every descriptor is verified two ways before it is surfaced:
/// <list type="number">
///   <item>Signature — <see cref="DescriptorSigner.Verify"/> proves the relay key authored it.</item>
///   <item>Ownership — each advertised <see cref="DiscoverableApp.OwnerClientId"/> is compared to the
///   client id derived from the app's compiled-in <see cref="VestaAppConfig.OwnerPublicKey"/>; a
///   relay claiming to host the app under a different owner is dropped as a spoof.</item>
/// </list>
/// What this cannot prove is that the relay actually holds the app's events — so results are
/// returned as unverified candidates for the user to adopt, never auto-trusted.
/// </summary>
public sealed class FederationClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly VestaAppConfig _appConfig;
    private readonly string _expectedOwnerClientId;

    /// <summary>
    /// Create a federation client for the app described by <paramref name="appConfig"/>. The
    /// app's owner public key is turned into the expected owner client id once, up front, and used
    /// to reject relays that advertise the app under any other owner.
    /// </summary>
    /// <param name="appConfig">The app config carrying the owner trust anchor and app id.</param>
    /// <param name="httpClient">
    /// The <see cref="HttpClient"/> used for the federation GETs. Optional; a default instance with
    /// a short timeout is created when not supplied. Callers that share an <see cref="HttpClient"/>
    /// should pass their own.
    /// </param>
    public FederationClient(VestaAppConfig appConfig, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(appConfig);

        _appConfig = appConfig;
        _expectedOwnerClientId = VestaIdentity.DeriveClientId(appConfig.OwnerPublicKey);
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Ask a reachable relay which relays (itself included) host this app, by GETting
    /// <c>/federation/apps/{appId}</c>. Returns the verified, owner-matching relays as show-only
    /// candidates, newest descriptor first. An unreachable or non-discovery relay yields an empty
    /// list rather than throwing.
    /// </summary>
    /// <param name="federationBaseUrl">
    /// The HTTP(S) base URL of any reachable relay's federation surface (e.g.
    /// <c>https://relay.example</c>). Use <see cref="ToFederationBaseUrl"/> to derive it from a
    /// WebSocket relay URL.
    /// </param>
    public async Task<IReadOnlyList<DiscoveredRelay>> DiscoverRelaysForAppAsync(
        Uri federationBaseUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(federationBaseUrl);

        Uri requestUri = new(federationBaseUrl, $"/federation/apps/{Uri.EscapeDataString(_appConfig.AppId)}");
        IReadOnlyList<ServerDescriptor> descriptors = await FetchDescriptorsAsync(requestUri, cancellationToken);

        List<DiscoveredRelay> results = new();
        foreach (ServerDescriptor descriptor in descriptors)
        {
            if (!IsAuthentic(descriptor))
            {
                continue;
            }
            if (!AdvertisesAppForOwner(descriptor, _appConfig.AppId))
            {
                continue;
            }
            if (TryBuildRelay(descriptor, hostsRequestedApp: true, out DiscoveredRelay? relay))
            {
                results.Add(relay);
            }
        }

        return OrderNewestFirst(results);
    }

    /// <summary>
    /// Browse every relay a reachable relay knows about, by GETting <c>/federation/peers</c>. This
    /// is the "show me all relays" escape hatch the user can fall back on when their app is not
    /// (yet) advertised anywhere obvious. Each relay is flagged with whether it verifiably hosts the
    /// app, so the UI can surface app-hosting relays first while still letting the user pick any.
    /// </summary>
    /// <param name="federationBaseUrl">The HTTP(S) base URL of any reachable relay's federation surface.</param>
    public async Task<IReadOnlyList<DiscoveredRelay>> ListAllRelaysAsync(
        Uri federationBaseUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(federationBaseUrl);

        Uri requestUri = new(federationBaseUrl, "/federation/peers");
        IReadOnlyList<ServerDescriptor> descriptors = await FetchDescriptorsAsync(requestUri, cancellationToken);

        List<DiscoveredRelay> results = new();
        foreach (ServerDescriptor descriptor in descriptors)
        {
            if (!IsAuthentic(descriptor))
            {
                continue;
            }
            bool hostsApp = AdvertisesAppForOwner(descriptor, _appConfig.AppId);
            if (TryBuildRelay(descriptor, hostsApp, out DiscoveredRelay? relay))
            {
                results.Add(relay);
            }
        }

        return OrderNewestFirst(results);
    }

    /// <summary>
    /// Derive the HTTP(S) federation base URL from a relay's WebSocket URL: <c>ws</c> → <c>http</c>,
    /// <c>wss</c> → <c>https</c>, with any path/query stripped (the federation routes live at the
    /// host root). Returns false for URLs that are not absolute ws/wss/http/https.
    /// </summary>
    public static bool ToFederationBaseUrl(Uri relayUrl, out Uri? federationBaseUrl)
    {
        federationBaseUrl = null;
        if (relayUrl is null || !relayUrl.IsAbsoluteUri)
        {
            return false;
        }

        string scheme = relayUrl.Scheme switch
        {
            "ws" or "http" => "http",
            "wss" or "https" => "https",
            _ => string.Empty
        };
        if (scheme.Length == 0)
        {
            return false;
        }

        UriBuilder builder = new()
        {
            Scheme = scheme,
            Host = relayUrl.Host,
            Port = relayUrl.IsDefaultPort ? -1 : relayUrl.Port,
            Path = "/"
        };
        federationBaseUrl = builder.Uri;
        return true;
    }

    private async Task<IReadOnlyList<ServerDescriptor>> FetchDescriptorsAsync(
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            List<ServerDescriptor>? descriptors =
                await response.Content.ReadFromJsonAsync<List<ServerDescriptor>>(JsonOptions, cancellationToken);
            return descriptors ?? (IReadOnlyList<ServerDescriptor>)[];
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Request timed out — treat as no results rather than surfacing to the caller.
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IsAuthentic(ServerDescriptor descriptor)
        => !descriptor.IsExpired(DateTimeOffset.UtcNow) && DescriptorSigner.Verify(descriptor);

    private bool AdvertisesAppForOwner(ServerDescriptor descriptor, string appId)
    {
        foreach (DiscoverableApp app in descriptor.Apps)
        {
            if (string.Equals(app.AppId, appId, StringComparison.Ordinal) &&
                string.Equals(app.OwnerClientId, _expectedOwnerClientId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryBuildRelay(ServerDescriptor descriptor, bool hostsRequestedApp, out DiscoveredRelay relay)
    {
        List<Uri> urls = new();
        foreach (string url in descriptor.Urls)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                urls.Add(uri);
            }
        }

        if (urls.Count == 0)
        {
            relay = null!;
            return false;
        }

        relay = new DiscoveredRelay(descriptor.RelayPublicKey, urls, hostsRequestedApp, descriptor.IssuedAt);
        return true;
    }

    private static IReadOnlyList<DiscoveredRelay> OrderNewestFirst(List<DiscoveredRelay> relays)
        => [.. relays.OrderByDescending(r => r.IssuedAt)];
}
