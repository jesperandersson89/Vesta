using System.Net.Http.Json;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VestaCore.Relay;

namespace VestaServer.Federation;

/// <summary>
/// Background anti-entropy gossip: on an interval, pulls <c>/federation/descriptor</c> and
/// <c>/federation/peers</c> from configured seeds and from the URLs of already-known peers, verifies
/// each descriptor's signature, and merges fresh ones into the <see cref="IServerDirectory"/>. This
/// is how a relay's view of the mesh stays current without any central coordinator — partial views
/// converge as descriptors propagate peer to peer.
/// </summary>
public sealed class FederationGossipService(
    IHttpClientFactory httpClientFactory,
    IServerDirectory directory,
    LocalDescriptorProvider local,
    IOptions<DiscoveryOptions> options,
    ILogger<FederationGossipService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DiscoveryOptions opts = options.Value;
        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(5, opts.GossipIntervalSeconds));

        // Brief startup delay so the host finishes wiring before the first round.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await GossipRoundAsync(opts, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Federation gossip round failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task GossipRoundAsync(DiscoveryOptions opts, CancellationToken cancellationToken)
    {
        HashSet<string> bases = new(StringComparer.OrdinalIgnoreCase);
        foreach (string seed in opts.Seeds)
        {
            if (TryFederationBase(seed, out string? baseUrl))
            {
                bases.Add(baseUrl);
            }
        }
        foreach (ServerDescriptor known in directory.All())
        {
            foreach (string url in known.Urls)
            {
                if (TryFederationBase(url, out string? baseUrl))
                {
                    bases.Add(baseUrl);
                }
            }
        }

        HttpClient client = httpClientFactory.CreateClient("federation");
        client.Timeout = TimeSpan.FromSeconds(10);

        foreach (string baseUrl in bases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PullDescriptorAsync(client, $"{baseUrl}/federation/descriptor", cancellationToken);
            await PullPeersAsync(client, $"{baseUrl}/federation/peers", cancellationToken);
        }
    }

    private async Task PullDescriptorAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            ServerDescriptor? descriptor = await client.GetFromJsonAsync<ServerDescriptor>(url, JsonOptions, cancellationToken);
            if (descriptor is not null)
            {
                Accept(descriptor);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Gossip pull failed: {Url}", url);
        }
    }

    private async Task PullPeersAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            List<ServerDescriptor>? peers = await client.GetFromJsonAsync<List<ServerDescriptor>>(url, JsonOptions, cancellationToken);
            if (peers is null)
            {
                return;
            }
            foreach (ServerDescriptor descriptor in peers)
            {
                Accept(descriptor);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Gossip pull failed: {Url}", url);
        }
    }

    private void Accept(ServerDescriptor descriptor)
    {
        // Never store our own descriptor, and only accept verifiable ones.
        if (string.Equals(descriptor.RelayPublicKey, local.RelayPublicKey, StringComparison.Ordinal))
        {
            return;
        }
        if (!DescriptorSigner.Verify(descriptor))
        {
            return;
        }
        directory.Merge(descriptor);
    }

    /// <summary>
    /// Derive the federation HTTP base (scheme + authority) from a seed or advertised relay URL.
    /// Maps <c>ws</c>→<c>http</c> and <c>wss</c>→<c>https</c> and drops any path (e.g. <c>/ws</c>),
    /// by convention the <c>/federation/*</c> surface is served on the same host as the relay.
    /// </summary>
    internal static bool TryFederationBase(string url, [NotNullWhen(true)] out string? baseUrl)
    {
        baseUrl = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string scheme = uri.Scheme switch
        {
            "ws" or "http" => "http",
            "wss" or "https" => "https",
            _ => string.Empty,
        };
        if (scheme.Length == 0)
        {
            return false;
        }

        baseUrl = $"{scheme}://{uri.Authority}";
        return true;
    }
}
