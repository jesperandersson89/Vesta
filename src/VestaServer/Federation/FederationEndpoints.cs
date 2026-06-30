using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using VestaCore.Channels;
using VestaCore.Relay;

namespace VestaServer.Federation;

/// <summary>
/// Maps the public <c>/federation/*</c> HTTP surface used for server-to-server discovery. All
/// endpoints are unauthenticated reads — they return only signed, already-public descriptors
/// (a relay's own and those it has learned via gossip). Mapped only when <c>Discovery:Enabled</c>.
/// </summary>
public static class FederationEndpoints
{
    public static void MapFederationApi(this IEndpointRouteBuilder builder)
    {
        RouteGroupBuilder group = builder.MapGroup("/federation");

        // This relay's own freshly-signed descriptor.
        group.MapGet("/descriptor", async (LocalDescriptorProvider local, CancellationToken ct) =>
            Results.Ok(await local.BuildAsync(ct)));

        // All descriptors this relay knows (its own + gossiped peers), deduplicated.
        group.MapGet("/peers", async (
            LocalDescriptorProvider local,
            IServerDirectory directory,
            CancellationToken ct) =>
        {
            ServerDescriptor self = await local.BuildAsync(ct);
            return Results.Ok(Combine(self, directory.All()));
        });

        // Descriptors (own + peers) that advertise the given app.
        group.MapGet("/apps/{appId}", async (
            string appId,
            LocalDescriptorProvider local,
            IServerDirectory directory,
            CancellationToken ct) =>
        {
            if (!AppId.IsValid(appId))
            {
                return Results.BadRequest(new { error = "Invalid app id" });
            }

            ServerDescriptor self = await local.BuildAsync(ct);
            IEnumerable<ServerDescriptor> selfMatch = self.Apps.Any(a => string.Equals(a.AppId, appId, StringComparison.Ordinal))
                ? [self]
                : [];
            return Results.Ok(Combine(selfMatch, directory.ForApp(appId)));
        });
    }

    private static List<ServerDescriptor> Combine(ServerDescriptor self, IEnumerable<ServerDescriptor> peers)
        => Combine([self], peers);

    private static List<ServerDescriptor> Combine(IEnumerable<ServerDescriptor> first, IEnumerable<ServerDescriptor> second)
    {
        Dictionary<string, ServerDescriptor> byKey = new(StringComparer.Ordinal);
        foreach (ServerDescriptor d in first.Concat(second))
        {
            if (string.IsNullOrEmpty(d.RelayPublicKey))
            {
                continue;
            }
            // Keep the newest descriptor per relay key.
            if (!byKey.TryGetValue(d.RelayPublicKey, out ServerDescriptor? existing) || d.IssuedAt > existing.IssuedAt)
            {
                byKey[d.RelayPublicKey] = d;
            }
        }
        return [.. byKey.Values];
    }
}
