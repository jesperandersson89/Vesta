using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using VestaCore.Channels;
using VestaCore.Storage;
using VestaServer.Connections;
using VestaServer.Storage;

namespace VestaServer.Admin;

/// <summary>
/// Maps the <c>/admin/*</c> HTTP surface. The <c>/admin/auth/*</c> endpoints
/// are public; everything else lives behind a bearer-token endpoint filter that
/// hands off to <see cref="AdminAuthService.ValidateToken"/>.
/// </summary>
public static class AdminEndpoints
{
  public static void MapAdminApi(this IEndpointRouteBuilder builder)
  {
    // Public sub-group: challenge + verify.
    RouteGroupBuilder publicGroup = builder.MapGroup("/admin/auth");

    publicGroup.MapPost("/challenge", (AdminAuthService auth) =>
        Results.Ok(auth.IssueChallenge()));

    publicGroup.MapPost("/verify", async (AdminVerifyRequest req, AdminAuthService auth, CancellationToken ct) =>
    {
      if (string.IsNullOrEmpty(req.PublicKey) || string.IsNullOrEmpty(req.Nonce) || string.IsNullOrEmpty(req.Signature))
        return Results.BadRequest(new { error = "publicKey, nonce, and signature are required" });
      AdminToken? token = await auth.VerifyAsync(req.PublicKey, req.Nonce, req.Signature, ct);
      return token is null ? Results.Unauthorized() : Results.Ok(token);
    });

    // Protected sub-group: every endpoint here requires a valid bearer token.
    RouteGroupBuilder admin = builder.MapGroup("/admin").AddEndpointFilter(BearerTokenFilter);

    // ── Channels ─────────────────────────────────────────────────────────
    admin.MapGet("/channels", async (
        IChannelAccessStore access,
        string? app,
        bool? includeDeleted,
        CancellationToken ct) =>
    {
      IReadOnlyList<ChannelSummary> rows = await access.ListChannelsAsync(app, includeDeleted ?? true, ct);
      return Results.Ok(rows.Select(r => new
      {
        id = r.Id,
        visibility = r.Visibility.ToString().ToLowerInvariant(),
        createdAt = r.CreatedAt,
        deletedAt = r.DeletedAt,
      }));
    });

    admin.MapGet("/channels/{id}", async (
        string id,
        IChannelAccessStore access,
        IChannelStatsService stats,
        CancellationToken ct) =>
    {
      if (!ChannelId.IsValid(id))
        return Results.BadRequest(new { error = "Invalid channel id" });

      ChannelVisibility? visibility = await access.GetVisibilityAsync(id, ct);
      if (visibility is null)
        return Results.NotFound();

      // Pull the row directly from the listing query so we get CreatedAt + DeletedAt without a second round-trip.
      IReadOnlyList<ChannelSummary> summaries = await access.ListChannelsAsync(null, includeDeleted: true, ct);
      ChannelSummary? summary = null;
      foreach (ChannelSummary s in summaries)
        if (s.Id == id) { summary = s; break; }
      if (summary is null) return Results.NotFound();

      ChannelStats channelStats = await stats.GetStatsAsync(id, ct);
      IReadOnlyList<ChannelMember> members = await access.ListMembersAsync(id, ct);

      return Results.Ok(new
      {
        id = summary.Id,
        visibility = summary.Visibility.ToString().ToLowerInvariant(),
        createdAt = summary.CreatedAt,
        deletedAt = summary.DeletedAt,
        eventCount = channelStats.EventCount,
        payloadBytes = channelStats.PayloadBytes,
        latestSequence = channelStats.LatestSequence,
        members = members.Select(m => new { clientId = m.ClientId, role = m.Role }),
      });
    });

    admin.MapDelete("/channels/{id}", async (
        HttpContext ctx,
        string id,
        IChannelAccessStore access,
        ILoggerFactory loggers,
        CancellationToken ct) =>
    {
      if (!ChannelId.IsValid(id))
        return Results.BadRequest(new { error = "Invalid channel id" });

      bool deleted = await access.DeleteChannelAsync(id, ct);
      if (!deleted) return Results.NotFound();

      ILogger log = loggers.CreateLogger("VestaServer.Admin");
      log.LogInformation("Channel '{Channel}' soft-deleted by admin {PublicKey}",
          id, ctx.Items[AdminContext.PublicKeyHexItem]);
      return Results.NoContent();
    });

    // ── Apps ─────────────────────────────────────────────────────────────
    admin.MapGet("/apps", async (
        IAppStore apps,
        IAppStorageAccountant accountant,
        CancellationToken ct) =>
    {
      IReadOnlyList<AppInfo> rows = await apps.ListAsync(ct);
      return Results.Ok(rows.Select(a => new
      {
        id = a.Id,
        ownerClientId = a.OwnerClientId,
        createdAt = a.CreatedAt,
        quotas = a.Quotas,
        storageBytes = accountant.Get(a.Id),
      }));
    });

    admin.MapGet("/apps/{id}", async (
        string id,
        IAppStore apps,
        IAppStorageAccountant accountant,
        IChannelAccessStore access,
        CancellationToken ct) =>
    {
      AppInfo? app = await apps.GetAsync(id, ct);
      if (app is null) return Results.NotFound();
      int channelCount = await access.CountChannelsByAppAsync(id, ct);
      return Results.Ok(new
      {
        id = app.Id,
        ownerClientId = app.OwnerClientId,
        createdAt = app.CreatedAt,
        quotas = app.Quotas,
        storageBytes = accountant.Get(app.Id),
        channelCount,
      });
    });

    admin.MapPatch("/apps/{id}/quotas", async (
        HttpContext ctx,
        string id,
        AppQuotas req,
        IAppStore apps,
        ILoggerFactory loggers,
        CancellationToken ct) =>
    {
      bool updated = await apps.SetQuotasAsync(id, req, ct);
      if (!updated) return Results.NotFound();
      ILogger log = loggers.CreateLogger("VestaServer.Admin");
      log.LogInformation("Quotas updated for app '{App}' by admin {PublicKey}",
          id, ctx.Items[AdminContext.PublicKeyHexItem]);
      return Results.Ok(new { id, quotas = req });
    });

    // ── Metrics ──────────────────────────────────────────────────────────
    admin.MapGet("/metrics", async (
        ConnectionManager connections,
        IAppStore apps,
        IChannelAccessStore access,
        CancellationToken ct) =>
    {
      IReadOnlyList<AppInfo> appRows = await apps.ListAsync(ct);
      IReadOnlyList<ChannelSummary> channelRows = await access.ListChannelsAsync(null, includeDeleted: false, ct);
      return Results.Ok(new
      {
        activeConnections = connections.ActiveCount,
        totalApps = appRows.Count,
        totalChannels = channelRows.Count,
      });
    });
  }

  private static async ValueTask<object?> BearerTokenFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
  {
    HttpContext http = ctx.HttpContext;
    AdminAuthService auth = http.RequestServices.GetRequiredService<AdminAuthService>();
    string? header = http.Request.Headers.Authorization.FirstOrDefault();
    if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal))
      return Results.Unauthorized();
    string token = header["Bearer ".Length..].Trim();
    string? publicKeyHex = auth.ValidateToken(token);
    if (publicKeyHex is null) return Results.Unauthorized();
    http.Items[AdminContext.PublicKeyHexItem] = publicKeyHex;
    return await next(ctx);
  }
}

internal static class AdminContext
{
  public const string PublicKeyHexItem = "AdminPublicKeyHex";
}

/// <summary>Request body for <c>POST /admin/auth/verify</c>.</summary>
public sealed record AdminVerifyRequest(
    [property: JsonPropertyName("publicKey")] string PublicKey,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("signature")] string Signature);
