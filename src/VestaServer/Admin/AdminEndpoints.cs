using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using VestaCore.Channels;
using VestaCore.Protocol;
using VestaCore.Storage;
using VestaCore.Utilities;
using VestaServer.Connections;
using VestaServer.Storage;

namespace VestaServer.Admin;

/// <summary>
/// Maps the <c>/admin/*</c> HTTP surface. The <c>/admin/auth/*</c> endpoints
/// are public (rate limited); everything else lives behind a bearer-token endpoint filter that
/// hands off to <see cref="AdminAuthService.ValidateToken"/>. Every mutating endpoint records an
/// entry in <see cref="IAdminAuditStore"/>.
/// </summary>
public static class AdminEndpoints
{
  public const string AuthRateLimitPolicy = "admin-auth";

  // How long a peer gets to finish the close handshake before the relay aborts the socket.
  private static readonly TimeSpan DisconnectGrace = TimeSpan.FromSeconds(5);

  public static void MapAdminApi(this IEndpointRouteBuilder builder)
  {
    // Public sub-group: challenge + verify.
    RouteGroupBuilder publicGroup = builder.MapGroup("/admin/auth").RequireRateLimiting(AuthRateLimitPolicy);

    publicGroup.MapPost("/challenge", (AdminAuthService auth) =>
        Results.Ok(auth.IssueChallenge()));

    publicGroup.MapPost("/verify", async (
        HttpContext ctx,
        AdminVerifyRequest req,
        AdminAuthService auth,
        IAdminAuditStore audit,
        ILoggerFactory loggers,
        CancellationToken ct) =>
    {
      if (string.IsNullOrEmpty(req.PublicKey) || string.IsNullOrEmpty(req.Nonce) || string.IsNullOrEmpty(req.Signature))
        return Results.BadRequest(new { error = "publicKey, nonce, and signature are required" });
      AdminToken? token = await auth.VerifyAsync(req.PublicKey, req.Nonce, req.Signature, ct);
      if (token is null)
      {
        loggers.CreateLogger("VestaServer.Admin").LogWarning(
            "Rejected admin auth attempt from {RemoteIp}", ctx.Connection.RemoteIpAddress);
        return Results.Unauthorized();
      }

      string actor = Convert.ToHexString(Base64Url.Decode(req.PublicKey));
      await RecordAuditAsync(audit, loggers, actor, "auth.login", null, new { remoteIp = ctx.Connection.RemoteIpAddress?.ToString() }, ct);
      return Results.Ok(token);
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
      await RecordAuditAsync(ctx, "channel.delete", id, null);
      return Results.NoContent();
    });

    // ── Apps ─────────────────────────────────────────────────────────────
    admin.MapGet("/apps", async (
        IAppStore apps,
        IAppStorageAccountant accountant,
        IAppAlertStore alerts,
        bool? includeDeleted,
        CancellationToken ct) =>
    {
      IReadOnlyList<AppInfo> rows = includeDeleted == true
          ? await apps.ListAllAsync(ct)
          : await apps.ListAsync(ct);
      IReadOnlyDictionary<string, int> alertCounts = await alerts.CountActiveByAppAsync(ct);
      return Results.Ok(rows.Select(a => new
      {
        id = a.Id,
        ownerClientId = a.OwnerClientId,
        createdAt = a.CreatedAt,
        quotas = a.Quotas,
        discoverable = a.Discoverable,
        storageBytes = accountant.Get(a.Id),
        deletedAt = a.DeletedAt,
        pausedAt = a.PausedAt,
        throttlePerMinute = a.ThrottlePerMinute,
        activeAlerts = alertCounts.GetValueOrDefault(a.Id),
      }));
    });

    admin.MapGet("/apps/{id}", async (
        string id,
        IAppStore apps,
        IAppStorageAccountant accountant,
        IChannelAccessStore access,
        ConnectionManager connections,
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
        discoverable = app.Discoverable,
        storageBytes = accountant.Get(app.Id),
        channelCount,
        deletedAt = app.DeletedAt,
        pausedAt = app.PausedAt,
        throttlePerMinute = app.ThrottlePerMinute,
        activeConnections = connections.CountByApp(app.Id),
      });
    });

    admin.MapGet("/apps/{id}/usage", async (
        string id,
        IAppStore apps,
        IAppStorageAccountant storageAccountant,
        IAppUsageAccountant usageAccountant,
        IChannelAccessStore access,
        CancellationToken ct) =>
    {
      AppInfo? app = await apps.GetAsync(id, ct);
      if (app is null) return Results.NotFound();
      int channelCount = await access.CountChannelsByAppAsync(id, ct);
      return Results.Ok(new
      {
        id = app.Id,
        periodStart = usageAccountant.CurrentPeriod,
        messages = usageAccountant.GetMessages(app.Id),
        storageBytes = storageAccountant.Get(app.Id),
        channelCount,
        quotas = app.Quotas,
      });
    });

    admin.MapGet("/apps/{id}/usage/history", async (
        string id,
        IAppStore apps,
        IAppUsageHistory history,
        int? months,
        CancellationToken ct) =>
    {
      if (await apps.GetAsync(id, ct) is null) return Results.NotFound();
      int window = Math.Clamp(months ?? 12, 1, 36);
      IReadOnlyList<AppUsagePeriod> rows = await history.GetAsync(id, window, ct);
      return Results.Ok(rows.Select(r => new { periodStart = r.PeriodStart, messages = r.Messages }));
    });

    admin.MapGet("/apps/{id}/channels", async (
        string id,
        IAppStore apps,
        IChannelAccessStore access,
        IChannelStatsService stats,
        CancellationToken ct) =>
    {
      if (await apps.GetAsync(id, ct) is null) return Results.NotFound();
      IReadOnlyList<ChannelSummary> channels = await access.ListChannelsAsync(id, includeDeleted: true, ct);
      IReadOnlyList<AppChannelStats> rows = await stats.GetStatsForAppAsync(id, ct);
      Dictionary<string, AppChannelStats> byId = rows.ToDictionary(r => r.ChannelId);
      return Results.Ok(channels
          .Select(c =>
          {
            byId.TryGetValue(c.Id, out AppChannelStats? s);
            return new
            {
              id = c.Id,
              visibility = c.Visibility.ToString().ToLowerInvariant(),
              createdAt = c.CreatedAt,
              deletedAt = c.DeletedAt,
              eventCount = s?.EventCount ?? 0,
              payloadBytes = s?.PayloadBytes ?? 0,
              latestSequence = s?.LatestSequence ?? 0,
            };
          })
          .OrderByDescending(c => c.eventCount)
          .ThenBy(c => c.id, StringComparer.Ordinal));
    });

    admin.MapGet("/apps/{id}/alerts", async (
        string id,
        IAppStore apps,
        IAppAlertStore alerts,
        bool? active,
        CancellationToken ct) =>
    {
      if (await apps.GetAsync(id, ct) is null) return Results.NotFound();
      IReadOnlyList<AppAlert> rows = await alerts.ListAsync(id, active ?? false, ct);
      return Results.Ok(rows.Select(AlertJson));
    });

    admin.MapGet("/apps/{id}/export", async (
        HttpContext ctx,
        string id,
        IAppStore apps,
        IChannelAccessStore access,
        IEventStore events,
        TimeProvider clock,
        CancellationToken ct) =>
    {
      AppInfo? app = await apps.GetAsync(id, ct);
      if (app is null) return Results.NotFound();

      DateTimeOffset exportedAt = clock.GetUtcNow();
      await RecordAuditAsync(ctx, "app.export", id, null);
      string fileName = $"{id}-{exportedAt.UtcDateTime:yyyyMMddTHHmmss}Z.jsonl";
      return Results.Stream(
          stream => AppExportWriter.WriteAsync(stream, app, access, events, exportedAt, ctx.RequestAborted),
          "application/x-ndjson",
          fileName);
    });

    admin.MapDelete("/apps/{id}", async (
        HttpContext ctx,
        string id,
        IAppStore apps,
        IChannelAccessStore access,
        IAppAlertStore alerts,
        ConnectionManager connections,
        ILoggerFactory loggers,
        CancellationToken ct) =>
    {
      DateTimeOffset? deletedAt = await apps.DeleteAsync(id, ct);
      if (deletedAt is null) return Results.NotFound();

      // Cascade with the app's own tombstone so restore can undo exactly this set of channels.
      int channels = await access.DeleteChannelsByAppAsync(id, deletedAt.Value, ct);
      await alerts.SyncAsync(id, [], ct);

      // After the channel cascade, so a client racing to resubscribe is refused rather than re-attached.
      int disconnected = await connections.DisconnectAppAsync(
          id,
          new ErrorMessage("UNKNOWN_APP", $"App '{id}' was deleted by the operator"),
          "App deleted",
          DisconnectGrace);

      loggers.CreateLogger("VestaServer.Admin").LogInformation(
          "App '{App}' soft-deleted ({Channels} channel(s), {Disconnected} connection(s) closed) by admin {PublicKey}",
          id, channels, disconnected, ctx.Items[AdminContext.PublicKeyHexItem]);
      await RecordAuditAsync(ctx, "app.delete", id, new { channels, disconnected, deletedAt });
      return Results.NoContent();
    });

    admin.MapPost("/apps/{id}/pause", async (
        HttpContext ctx,
        string id,
        IAppStore apps,
        CancellationToken ct) =>
    {
      IResult? blocked = await RejectUnlessActiveAsync(apps, id, ct);
      if (blocked is not null) return blocked;

      await apps.SetPausedAsync(id, paused: true, ct);
      AppInfo? app = await apps.GetAsync(id, ct);
      await RecordAuditAsync(ctx, "app.pause", id, null);
      return Results.Ok(new { id, pausedAt = app?.PausedAt });
    });

    admin.MapPost("/apps/{id}/resume", async (
        HttpContext ctx,
        string id,
        IAppStore apps,
        CancellationToken ct) =>
    {
      IResult? blocked = await RejectUnlessActiveAsync(apps, id, ct);
      if (blocked is not null) return blocked;

      await apps.SetPausedAsync(id, paused: false, ct);
      await RecordAuditAsync(ctx, "app.resume", id, null);
      return Results.Ok(new { id, pausedAt = (DateTimeOffset?)null });
    });

    admin.MapPatch("/apps/{id}/throttle", async (
        HttpContext ctx,
        string id,
        AppThrottleRequest req,
        IAppStore apps,
        CancellationToken ct) =>
    {
      if (req.PerMinute is <= 0)
        return Results.BadRequest(new { error = "perMinute must be a positive number, or null to remove the throttle" });

      IResult? blocked = await RejectUnlessActiveAsync(apps, id, ct);
      if (blocked is not null) return blocked;

      await apps.SetThrottleAsync(id, req.PerMinute, ct);
      await RecordAuditAsync(ctx, "app.throttle", id, new { req.PerMinute });
      return Results.Ok(new { id, throttlePerMinute = req.PerMinute });
    });

    admin.MapPost("/apps/{id}/restore", async (
        HttpContext ctx,
        string id,
        IAppStore apps,
        IChannelAccessStore access,
        ILoggerFactory loggers,
        CancellationToken ct) =>
    {
      AppInfo? app = await apps.GetAsync(id, ct);
      if (app is null) return Results.NotFound();
      if (app.DeletedAt is not DateTimeOffset deletedAt)
        return Results.Conflict(new { error = "App is not deleted" });

      // Channels first: if this fails midway the app is still deleted, so a retry is possible.
      int channels = await access.RestoreChannelsByAppAsync(id, deletedAt, ct);
      await apps.RestoreAsync(id, ct);

      loggers.CreateLogger("VestaServer.Admin").LogInformation(
          "App '{App}' restored ({Channels} channel(s)) by admin {PublicKey}",
          id, channels, ctx.Items[AdminContext.PublicKeyHexItem]);
      await RecordAuditAsync(ctx, "app.restore", id, new { channels });
      return Results.Ok(new { id, deletedAt = (DateTimeOffset?)null });
    });

    admin.MapPatch("/apps/{id}/quotas", async (
        HttpContext ctx,
        string id,
        AppQuotas req,
        IAppStore apps,
        ILoggerFactory loggers,
        CancellationToken ct) =>
    {
      IResult? blocked = await RejectUnlessActiveAsync(apps, id, ct);
      if (blocked is not null) return blocked;

      bool updated = await apps.SetQuotasAsync(id, req, ct);
      if (!updated) return Results.NotFound();
      ILogger log = loggers.CreateLogger("VestaServer.Admin");
      log.LogInformation("Quotas updated for app '{App}' by admin {PublicKey}",
          id, ctx.Items[AdminContext.PublicKeyHexItem]);
      await RecordAuditAsync(ctx, "app.quotas", id, req);
      return Results.Ok(new { id, quotas = req });
    });

    admin.MapPatch("/apps/{id}/discoverable", async (
        HttpContext ctx,
        string id,
        AppDiscoverableRequest req,
        IAppStore apps,
        ILoggerFactory loggers,
        CancellationToken ct) =>
    {
      IResult? blocked = await RejectUnlessActiveAsync(apps, id, ct);
      if (blocked is not null) return blocked;

      bool updated = await apps.SetDiscoverableAsync(id, req.Discoverable, ct);
      if (!updated) return Results.NotFound();
      ILogger log = loggers.CreateLogger("VestaServer.Admin");
      log.LogInformation("Discoverable set to {Discoverable} for app '{App}' by admin {PublicKey}",
          req.Discoverable, id, ctx.Items[AdminContext.PublicKeyHexItem]);
      await RecordAuditAsync(ctx, "app.discoverable", id, new { req.Discoverable });
      return Results.Ok(new { id, discoverable = req.Discoverable });
    });

    admin.MapPatch("/apps/{id}/owner", async (
        HttpContext ctx,
        string id,
        AppOwnerRequest req,
        IAppStore apps,
        ILoggerFactory loggers,
        CancellationToken ct) =>
    {
      if (string.IsNullOrWhiteSpace(req.OwnerClientId))
        return Results.BadRequest(new { error = "ownerClientId is required" });

      IResult? blocked = await RejectUnlessActiveAsync(apps, id, ct);
      if (blocked is not null) return blocked;

      bool updated = await apps.SetOwnerAsync(id, req.OwnerClientId, ct);
      if (!updated) return Results.NotFound();
      ILogger log = loggers.CreateLogger("VestaServer.Admin");
      log.LogInformation("Owner rebound for app '{App}' to {ClientId} by admin {PublicKey}",
          id, req.OwnerClientId, ctx.Items[AdminContext.PublicKeyHexItem]);
      await RecordAuditAsync(ctx, "app.owner", id, new { ownerClientId = req.OwnerClientId });
      return Results.Ok(new { id, ownerClientId = req.OwnerClientId });
    });

    // ── Alerts ───────────────────────────────────────────────────────────
    admin.MapGet("/alerts", async (
        IAppAlertStore alerts,
        bool? active,
        string? app,
        CancellationToken ct) =>
    {
      IReadOnlyList<AppAlert> rows = await alerts.ListAsync(app, active ?? true, ct);
      return Results.Ok(rows.Select(AlertJson));
    });

    admin.MapPost("/alerts/{id:long}/ack", async (
        HttpContext ctx,
        long id,
        IAppAlertStore alerts,
        CancellationToken ct) =>
    {
      bool acknowledged = await alerts.AcknowledgeAsync(id, ct);
      if (!acknowledged) return Results.NotFound();
      await RecordAuditAsync(ctx, "alert.ack", id.ToString(), null);
      return Results.NoContent();
    });

    // ── Audit / config ───────────────────────────────────────────────────
    admin.MapGet("/audit", async (
        IAdminAuditStore audit,
        int? limit,
        string? target,
        CancellationToken ct) =>
    {
      IReadOnlyList<AdminAuditEntry> rows = await audit.ListAsync(Math.Clamp(limit ?? 100, 1, 500), target, ct);
      return Results.Ok(rows.Select(r => new
      {
        id = r.Id,
        at = r.At,
        adminPublicKey = r.AdminPublicKey,
        action = r.Action,
        target = r.Target,
        details = r.Details,
      }));
    });

    admin.MapGet("/config", (
        IOptions<AppDeletionPrunerOptions> appPruner,
        IOptions<ChannelDeletionPrunerOptions> channelPruner,
        IOptions<AdminApiOptions> adminApi,
        IOptions<AppAlertOptions> alertOptions) =>
        Results.Ok(new
        {
          appDeletionPrunerEnabled = appPruner.Value.Enabled,
          appDeletionGracePeriodSeconds = appPruner.Value.GracePeriod.TotalSeconds,
          channelDeletionPrunerEnabled = channelPruner.Value.Enabled,
          channelDeletionGracePeriodSeconds = channelPruner.Value.GracePeriod.TotalSeconds,
          tokenTtlSeconds = adminApi.Value.TokenTtl.TotalSeconds,
          alertThresholdsPercent = alertOptions.Value.ThresholdsPercent,
        }));

    // ── Metrics ──────────────────────────────────────────────────────────
    admin.MapGet("/metrics", async (
        ConnectionManager connections,
        IAppStore apps,
        IAppAlertStore alerts,
        IChannelAccessStore access,
        CancellationToken ct) =>
    {
      IReadOnlyList<AppInfo> appRows = await apps.ListAllAsync(ct);
      IReadOnlyList<ChannelSummary> channelRows = await access.ListChannelsAsync(null, includeDeleted: false, ct);
      IReadOnlyDictionary<string, int> alertCounts = await alerts.CountActiveByAppAsync(ct);
      return Results.Ok(new
      {
        activeConnections = connections.ActiveCount,
        totalApps = appRows.Count(a => a.DeletedAt is null),
        totalChannels = channelRows.Count,
        deletedApps = appRows.Count(a => a.DeletedAt is not null),
        pausedApps = appRows.Count(a => a.DeletedAt is null && a.PausedAt is not null),
        activeAlerts = alertCounts.Values.Sum(),
      });
    });
  }

  private static object AlertJson(AppAlert a) => new
  {
    id = a.Id,
    appId = a.AppId,
    metric = a.Metric,
    thresholdPercent = a.ThresholdPercent,
    observed = a.Observed,
    limit = a.LimitValue,
    periodStart = a.PeriodStart,
    raisedAt = a.RaisedAt,
    resolvedAt = a.ResolvedAt,
    acknowledgedAt = a.AcknowledgedAt,
  };

  /// <summary>404 for unknown apps, 409 for soft-deleted ones (restore first); <c>null</c> when the app is active.</summary>
  private static async Task<IResult?> RejectUnlessActiveAsync(IAppStore apps, string id, CancellationToken ct)
  {
    AppInfo? existing = await apps.GetAsync(id, ct);
    return existing switch
    {
      null => Results.NotFound(),
      { DeletedAt: not null } => Results.Conflict(new { error = "App is deleted; restore it first" }),
      _ => null,
    };
  }

  private static Task RecordAuditAsync(HttpContext ctx, string action, string? target, object? details)
      => RecordAuditAsync(
          ctx.RequestServices.GetRequiredService<IAdminAuditStore>(),
          ctx.RequestServices.GetRequiredService<ILoggerFactory>(),
          AdminContext.Actor(ctx),
          action,
          target,
          details,
          ctx.RequestAborted);

  // A failing audit write must not turn an already-applied mutation into a 500.
  private static async Task RecordAuditAsync(
      IAdminAuditStore audit,
      ILoggerFactory loggers,
      string actor,
      string action,
      string? target,
      object? details,
      CancellationToken ct)
  {
    try
    {
      await audit.RecordAsync(actor, action, target, details, ct);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      loggers.CreateLogger("VestaServer.Admin").LogError(ex, "Failed to record audit entry {Action} for {Target}", action, target);
    }
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

  public static string Actor(HttpContext ctx) => ctx.Items[PublicKeyHexItem] as string ?? "unknown";
}

/// <summary>Request body for <c>POST /admin/auth/verify</c>.</summary>
public sealed record AdminVerifyRequest(
    [property: JsonPropertyName("publicKey")] string PublicKey,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("signature")] string Signature);

/// <summary>Request body for <c>PATCH /admin/apps/{id}/discoverable</c>.</summary>
public sealed record AppDiscoverableRequest(
    [property: JsonPropertyName("discoverable")] bool Discoverable);

/// <summary>Request body for <c>PATCH /admin/apps/{id}/throttle</c>; <c>null</c> removes the throttle.</summary>
public sealed record AppThrottleRequest(
    [property: JsonPropertyName("perMinute")] int? PerMinute);

/// <summary>Request body for <c>PATCH /admin/apps/{id}/owner</c>.</summary>
public sealed record AppOwnerRequest(
    [property: JsonPropertyName("ownerClientId")] string OwnerClientId);
