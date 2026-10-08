using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using VestaCore.Events;
using VestaCore.Identity;
using VestaCore.Protocol;
using VestaCore.Serialization;
using VestaCore.Storage;
using VestaCore.Utilities;
using VestaServer.Storage;

namespace VestaServer.Tests;

/// <summary>
/// Admin API tests for app lifecycle (soft delete / restore), export, alerts, audit,
/// config and the per-app dashboard endpoints. In-memory stores, no Docker.
/// </summary>
public class AdminAppLifecycleTests : IClassFixture<AdminAppLifecycleTests.Fixture>
{
  private readonly Fixture _fixture;

  public AdminAppLifecycleTests(Fixture fixture) => _fixture = fixture;

  public sealed class Fixture : IDisposable
  {
    public VestaIdentity AdminIdentity { get; } = VestaIdentity.Generate();
    public WebApplicationFactory<Program> Factory { get; }

    public Fixture()
    {
      VestaIdentity admin = AdminIdentity;
      Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
      {
        builder.UseSetting("UseInMemoryStore", "true");
        builder.UseSetting("Admin:BootstrapPublicKeys:0", Base64Url.Encode(admin.PublicKey));
        builder.UseSetting("AdminApi:AuthRateLimitPerMinute", "0");
      });
    }

    public void Dispose()
    {
      Factory.Dispose();
      AdminIdentity.Dispose();
    }
  }

  private IAppStore Apps => _fixture.Factory.Services.GetRequiredService<IAppStore>();
  private IChannelAccessStore Channels => _fixture.Factory.Services.GetRequiredService<IChannelAccessStore>();
  private IEventStore Events => _fixture.Factory.Services.GetRequiredService<IEventStore>();

  // ── Soft delete / restore ──────────────────────────────────────────────

  [Fact]
  public async Task DeleteApp_RegisteredApp_SoftDeletesAppAndCascadesToChannels()
  {
    await Apps.RegisterAsync("delapp", "owner-del");
    await Channels.RecordImplicitChannelAsync("delapp/room");
    HttpClient client = await GetAuthenticatedClientAsync();

    HttpResponseMessage resp = await client.DeleteAsync("/admin/apps/delapp");

    Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    Assert.False(await Apps.ExistsAsync("delapp"));
    Assert.True(await Channels.IsDeletedAsync("delapp/room"));

    JsonElement active = await client.GetFromJsonAsync<JsonElement>("/admin/apps");
    Assert.DoesNotContain(active.EnumerateArray(), a => a.GetProperty("id").GetString() == "delapp");

    JsonElement all = await client.GetFromJsonAsync<JsonElement>("/admin/apps?includeDeleted=true");
    JsonElement row = all.EnumerateArray().Single(a => a.GetProperty("id").GetString() == "delapp");
    Assert.NotEqual(JsonValueKind.Null, row.GetProperty("deletedAt").ValueKind);
  }

  [Fact]
  public async Task DeleteApp_UnknownApp_Returns404()
  {
    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage resp = await client.DeleteAsync("/admin/apps/never-registered");
    Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
  }

  [Fact]
  public async Task DeleteApp_AlreadyDeleted_IsIdempotent()
  {
    await Apps.RegisterAsync("twiceapp", "owner");
    HttpClient client = await GetAuthenticatedClientAsync();

    Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/admin/apps/twiceapp")).StatusCode);
    DateTimeOffset? first = (await Apps.GetAsync("twiceapp"))?.DeletedAt;
    Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/admin/apps/twiceapp")).StatusCode);

    Assert.Equal(first, (await Apps.GetAsync("twiceapp"))?.DeletedAt);
  }

  [Fact]
  public async Task DeleteApp_ThenRegisterSameId_StillReserved()
  {
    await Apps.RegisterAsync("reservedapp", "owner");
    HttpClient client = await GetAuthenticatedClientAsync();
    await client.DeleteAsync("/admin/apps/reservedapp");

    await Assert.ThrowsAsync<AppAlreadyRegisteredException>(() => Apps.RegisterAsync("reservedapp", "intruder"));
  }

  [Fact]
  public async Task MutatingDeletedApp_Returns409()
  {
    await Apps.RegisterAsync("frozenapp", "owner");
    HttpClient client = await GetAuthenticatedClientAsync();
    await client.DeleteAsync("/admin/apps/frozenapp");

    Assert.Equal(HttpStatusCode.Conflict,
        (await client.PatchAsJsonAsync("/admin/apps/frozenapp/quotas", new { maxChannels = 5 })).StatusCode);
    Assert.Equal(HttpStatusCode.Conflict,
        (await client.PatchAsJsonAsync("/admin/apps/frozenapp/owner", new { ownerClientId = "x" })).StatusCode);
    Assert.Equal(HttpStatusCode.Conflict,
        (await client.PatchAsJsonAsync("/admin/apps/frozenapp/discoverable", new { discoverable = true })).StatusCode);
  }

  [Fact]
  public async Task RestoreApp_AfterDelete_RestoresOnlyCascadedChannels()
  {
    await Apps.RegisterAsync("resapp", "owner");
    await Channels.RecordImplicitChannelAsync("resapp/independent");
    await Channels.RecordImplicitChannelAsync("resapp/cascaded");
    await Channels.DeleteChannelAsync("resapp/independent");
    await Task.Delay(10);
    HttpClient client = await GetAuthenticatedClientAsync();
    await client.DeleteAsync("/admin/apps/resapp");

    HttpResponseMessage resp = await client.PostAsync("/admin/apps/resapp/restore", content: null);

    Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    Assert.True(await Apps.ExistsAsync("resapp"));
    Assert.False(await Channels.IsDeletedAsync("resapp/cascaded"));
    Assert.True(await Channels.IsDeletedAsync("resapp/independent"));
  }

  [Fact]
  public async Task RestoreApp_NotDeleted_Returns409()
  {
    await Apps.RegisterAsync("liveapp", "owner");
    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage resp = await client.PostAsync("/admin/apps/liveapp/restore", content: null);
    Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
  }

  [Fact]
  public async Task RestoreApp_UnknownApp_Returns404()
  {
    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage resp = await client.PostAsync("/admin/apps/never-registered/restore", content: null);
    Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
  }

  // ── Export ─────────────────────────────────────────────────────────────

  [Fact]
  public async Task ExportApp_WithEvents_StreamsManifestThenEventsInSequenceOrder()
  {
    await Apps.RegisterAsync("exportapp", "owner-exp");
    await Channels.RecordImplicitChannelAsync("exportapp/one");
    await Channels.RecordImplicitChannelAsync("exportapp/two");
    for (int i = 0; i < 3; i++) await Events.AppendAsync(CreateEvent("exportapp/one"));
    await Events.AppendAsync(CreateEvent("exportapp/two"));
    HttpClient client = await GetAuthenticatedClientAsync();

    HttpResponseMessage resp = await client.GetAsync("/admin/apps/exportapp/export");

    Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    Assert.Equal("application/x-ndjson", resp.Content.Headers.ContentType?.MediaType);
    string? fileName = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
    Assert.StartsWith("exportapp-", fileName);
    Assert.EndsWith(".jsonl", fileName);

    string[] lines = (await resp.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Assert.Equal(5, lines.Length);

    JsonElement manifest = JsonDocument.Parse(lines[0]).RootElement;
    Assert.Equal("manifest", manifest.GetProperty("type").GetString());
    Assert.Equal(1, manifest.GetProperty("version").GetInt32());
    Assert.Equal("exportapp", manifest.GetProperty("app").GetProperty("id").GetString());
    Assert.Equal(2, manifest.GetProperty("channels").GetArrayLength());

    List<JsonElement> eventLines = [.. lines.Skip(1).Select(l => JsonDocument.Parse(l).RootElement)];
    Assert.All(eventLines, e => Assert.Equal("event", e.GetProperty("type").GetString()));
    Assert.Equal(
        [1L, 2L, 3L],
        eventLines.Where(e => e.GetProperty("event").GetProperty("channelId").GetString() == "exportapp/one")
            .Select(e => e.GetProperty("sequence").GetInt64()));
  }

  [Fact]
  public async Task ExportApp_DeletedApp_StillExportable()
  {
    await Apps.RegisterAsync("exportdeleted", "owner");
    await Channels.RecordImplicitChannelAsync("exportdeleted/room");
    await Events.AppendAsync(CreateEvent("exportdeleted/room"));
    HttpClient client = await GetAuthenticatedClientAsync();
    await client.DeleteAsync("/admin/apps/exportdeleted");

    HttpResponseMessage resp = await client.GetAsync("/admin/apps/exportdeleted/export");

    Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    string[] lines = (await resp.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Assert.Equal(2, lines.Length);
  }

  [Fact]
  public async Task ExportApp_UnknownApp_Returns404()
  {
    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage resp = await client.GetAsync("/admin/apps/never-registered/export");
    Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
  }

  // ── Audit ──────────────────────────────────────────────────────────────

  [Fact]
  public async Task Audit_AfterQuotaPatch_RecordsActionWithAdminKey()
  {
    await Apps.RegisterAsync("auditapp", "owner");
    HttpClient client = await GetAuthenticatedClientAsync();
    await client.PatchAsJsonAsync("/admin/apps/auditapp/quotas", new { maxChannels = 7 });

    JsonElement rows = await client.GetFromJsonAsync<JsonElement>("/admin/audit?target=auditapp");

    JsonElement entry = rows.EnumerateArray().Single(r => r.GetProperty("action").GetString() == "app.quotas");
    Assert.Equal(Convert.ToHexString(_fixture.AdminIdentity.PublicKey), entry.GetProperty("adminPublicKey").GetString());
    Assert.Equal(7, entry.GetProperty("details").GetProperty("maxChannels").GetInt32());
  }

  [Fact]
  public async Task Audit_AfterLogin_RecordsAuthLogin()
  {
    HttpClient client = await GetAuthenticatedClientAsync();
    JsonElement rows = await client.GetFromJsonAsync<JsonElement>("/admin/audit?limit=500");
    Assert.Contains(rows.EnumerateArray(), r => r.GetProperty("action").GetString() == "auth.login");
  }

  [Fact]
  public async Task Audit_WithoutToken_Returns401()
  {
    HttpResponseMessage resp = await _fixture.Factory.CreateClient().GetAsync("/admin/audit");
    Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
  }

  // ── Alerts ─────────────────────────────────────────────────────────────

  [Fact]
  public async Task Alerts_RaisedAlert_ListedAndAcknowledgeable()
  {
    await Apps.RegisterAsync("alertapp", "owner");
    IAppAlertStore alerts = _fixture.Factory.Services.GetRequiredService<IAppAlertStore>();
    await alerts.SyncAsync("alertapp", [new AppAlertCrossing(AppAlertMetrics.Messages, 80, 85, 100, new DateOnly(2026, 10, 1))]);
    HttpClient client = await GetAuthenticatedClientAsync();

    JsonElement all = await client.GetFromJsonAsync<JsonElement>("/admin/alerts?app=alertapp");
    JsonElement alert = all.EnumerateArray().Single();
    Assert.Equal("messages", alert.GetProperty("metric").GetString());
    Assert.Equal(80, alert.GetProperty("thresholdPercent").GetInt32());
    long id = alert.GetProperty("id").GetInt64();

    Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/admin/alerts/{id}/ack", null)).StatusCode);

    JsonElement forApp = await client.GetFromJsonAsync<JsonElement>("/admin/apps/alertapp/alerts?active=true");
    Assert.NotEqual(JsonValueKind.Null, forApp.EnumerateArray().Single().GetProperty("acknowledgedAt").ValueKind);

    JsonElement apps = await client.GetFromJsonAsync<JsonElement>("/admin/apps");
    Assert.Equal(1, apps.EnumerateArray().Single(a => a.GetProperty("id").GetString() == "alertapp").GetProperty("activeAlerts").GetInt32());

    JsonElement metrics = await client.GetFromJsonAsync<JsonElement>("/admin/metrics");
    Assert.True(metrics.GetProperty("activeAlerts").GetInt32() >= 1);
  }

  [Fact]
  public async Task Alerts_AcknowledgeUnknownId_Returns404()
  {
    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage resp = await client.PostAsync("/admin/alerts/999999/ack", null);
    Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
  }

  [Fact]
  public async Task DeleteApp_WithOpenAlert_ResolvesAlert()
  {
    await Apps.RegisterAsync("alertdelapp", "owner");
    IAppAlertStore alerts = _fixture.Factory.Services.GetRequiredService<IAppAlertStore>();
    await alerts.SyncAsync("alertdelapp", [new AppAlertCrossing(AppAlertMetrics.Channels, 100, 5, 5, null)]);
    HttpClient client = await GetAuthenticatedClientAsync();

    await client.DeleteAsync("/admin/apps/alertdelapp");

    Assert.Empty(await alerts.ListAsync("alertdelapp", activeOnly: true));
  }

  // ── Dashboard data ─────────────────────────────────────────────────────

  [Fact]
  public async Task GetApp_ReturnsDashboardFields()
  {
    await Apps.RegisterAsync("detailapp", "owner");
    HttpClient client = await GetAuthenticatedClientAsync();

    JsonElement body = await client.GetFromJsonAsync<JsonElement>("/admin/apps/detailapp");

    Assert.Equal(0, body.GetProperty("activeConnections").GetInt32());
    Assert.Equal(JsonValueKind.Null, body.GetProperty("deletedAt").ValueKind);
  }

  [Fact]
  public async Task GetAppChannels_ReturnsStatsOrderedByEventCount()
  {
    await Apps.RegisterAsync("statsapp", "owner");
    await Channels.RecordImplicitChannelAsync("statsapp/quiet");
    await Channels.RecordImplicitChannelAsync("statsapp/busy");
    await Channels.RecordImplicitChannelAsync("statsapp/empty");
    await Events.AppendAsync(CreateEvent("statsapp/quiet"));
    for (int i = 0; i < 3; i++) await Events.AppendAsync(CreateEvent("statsapp/busy"));
    HttpClient client = await GetAuthenticatedClientAsync();

    JsonElement rows = await client.GetFromJsonAsync<JsonElement>("/admin/apps/statsapp/channels");

    string[] ids = [.. rows.EnumerateArray().Select(r => r.GetProperty("id").GetString()!)];
    Assert.Equal(["statsapp/busy", "statsapp/quiet", "statsapp/empty"], ids);
    Assert.Equal(3, rows[0].GetProperty("eventCount").GetInt64());
    Assert.Equal(0, rows[2].GetProperty("eventCount").GetInt64());
  }

  [Fact]
  public async Task GetAppChannels_UnknownApp_Returns404()
  {
    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage resp = await client.GetAsync("/admin/apps/never-registered/channels");
    Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
  }

  [Fact]
  public async Task GetUsageHistory_ReturnsCurrentPeriodFromAccountant()
  {
    await Apps.RegisterAsync("histapp", "owner");
    _fixture.Factory.Services.GetRequiredService<IAppUsageAccountant>().SetMessages("histapp", 7);
    HttpClient client = await GetAuthenticatedClientAsync();

    JsonElement rows = await client.GetFromJsonAsync<JsonElement>("/admin/apps/histapp/usage/history?months=12");

    Assert.Equal(7, rows.EnumerateArray().Single().GetProperty("messages").GetInt64());
  }

  [Fact]
  public async Task GetUsageHistory_UnknownApp_Returns404()
  {
    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage resp = await client.GetAsync("/admin/apps/never-registered/usage/history");
    Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
  }

  [Fact]
  public async Task Metrics_CountsDeletedAppsSeparately()
  {
    await Apps.RegisterAsync("metricdel", "owner");
    HttpClient client = await GetAuthenticatedClientAsync();
    await client.DeleteAsync("/admin/apps/metricdel");

    JsonElement metrics = await client.GetFromJsonAsync<JsonElement>("/admin/metrics");

    Assert.True(metrics.GetProperty("deletedApps").GetInt32() >= 1);
  }

  [Fact]
  public async Task Config_ReportsGraceAndThresholdDefaults()
  {
    HttpClient client = await GetAuthenticatedClientAsync();

    JsonElement config = await client.GetFromJsonAsync<JsonElement>("/admin/config");

    Assert.False(config.GetProperty("appDeletionPrunerEnabled").GetBoolean());
    Assert.Equal(TimeSpan.FromDays(7).TotalSeconds, config.GetProperty("appDeletionGracePeriodSeconds").GetDouble());
    Assert.Equal([80, 100], config.GetProperty("alertThresholdsPercent").EnumerateArray().Select(e => e.GetInt32()));
  }

  // ── Pause / throttle ───────────────────────────────────────────────────

  [Fact]
  public async Task PauseApp_RejectsPublishUntilResumed()
  {
    await Apps.RegisterAsync("pauseapp", "owner");
    HttpClient client = await GetAuthenticatedClientAsync();
    using WebSocket ws = await ConnectAsync("client-pause");

    HttpResponseMessage paused = await client.PostAsync("/admin/apps/pauseapp/pause", content: null);
    Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
    Assert.NotEqual(JsonValueKind.Null, (await paused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pausedAt").ValueKind);

    VestaEvent blocked = CreateEvent("pauseapp/chat");
    await SendAsync(ws, new PublishMessage("pauseapp/chat", blocked with { ClientId = "client-pause" }));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("APP_PAUSED", error.Code);
    Assert.Equal(blocked.Id, error.EventId);

    HttpResponseMessage resumed = await client.PostAsync("/admin/apps/pauseapp/resume", content: null);
    Assert.Equal(JsonValueKind.Null, (await resumed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pausedAt").ValueKind);

    await SendAsync(ws, new PublishMessage("pauseapp/chat", CreateEvent("pauseapp/chat") with { ClientId = "client-pause" }));
    Assert.IsType<AckMessage>(await ReceiveAsync(ws));
  }

  [Fact]
  public async Task PauseApp_BlocksCreateChannel_ButKeepsReadsWorking()
  {
    await Apps.RegisterAsync("pausechan", "owner");
    HttpClient client = await GetAuthenticatedClientAsync();
    await client.PostAsync("/admin/apps/pausechan/pause", content: null);

    using WebSocket ws = await ConnectAsync("client-pausechan", subscribeTo: "pausechan/room");
    Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>("/admin/apps/pausechan")).GetProperty("activeConnections").GetInt32());

    await SendAsync(ws, new CreateChannelMessage("pausechan/new", "private", []));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("APP_PAUSED", error.Code);
  }

  [Fact]
  public async Task PauseApp_IsIdempotent_AndShownInListAndMetrics()
  {
    await Apps.RegisterAsync("pausetwice", "owner");
    HttpClient client = await GetAuthenticatedClientAsync();

    await client.PostAsync("/admin/apps/pausetwice/pause", content: null);
    DateTimeOffset? first = (await Apps.GetAsync("pausetwice"))?.PausedAt;
    await Task.Delay(10);
    await client.PostAsync("/admin/apps/pausetwice/pause", content: null);

    Assert.Equal(first, (await Apps.GetAsync("pausetwice"))?.PausedAt);
    JsonElement apps = await client.GetFromJsonAsync<JsonElement>("/admin/apps");
    Assert.NotEqual(JsonValueKind.Null,
        apps.EnumerateArray().Single(a => a.GetProperty("id").GetString() == "pausetwice").GetProperty("pausedAt").ValueKind);
    Assert.True((await client.GetFromJsonAsync<JsonElement>("/admin/metrics")).GetProperty("pausedApps").GetInt32() >= 1);
  }

  [Fact]
  public async Task ThrottleApp_CapsTotalPublishesAcrossClients()
  {
    await Apps.RegisterAsync("throttleapp", "owner");
    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage set = await client.PatchAsJsonAsync("/admin/apps/throttleapp/throttle", new { perMinute = 2 });
    Assert.Equal(HttpStatusCode.OK, set.StatusCode);

    using WebSocket a = await ConnectAsync("client-throttle-a");
    using WebSocket b = await ConnectAsync("client-throttle-b");

    await SendAsync(a, new PublishMessage("throttleapp/chat", CreateEvent("throttleapp/chat") with { ClientId = "client-throttle-a" }));
    Assert.IsType<AckMessage>(await ReceiveAsync(a));
    await SendAsync(b, new PublishMessage("throttleapp/chat", CreateEvent("throttleapp/chat") with { ClientId = "client-throttle-b" }));
    Assert.IsType<AckMessage>(await ReceiveAsync(b));

    await SendAsync(a, new PublishMessage("throttleapp/chat", CreateEvent("throttleapp/chat") with { ClientId = "client-throttle-a" }));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(a));
    Assert.Equal("RATE_LIMITED", error.Code);
    Assert.Contains("throttled", error.Message);
  }

  [Fact]
  public async Task ThrottleApp_NullRemovesThrottle_InvalidValueRejected()
  {
    await Apps.RegisterAsync("throttleclear", "owner");
    HttpClient client = await GetAuthenticatedClientAsync();
    await client.PatchAsJsonAsync("/admin/apps/throttleclear/throttle", new { perMinute = 5 });
    Assert.Equal(5, (await Apps.GetAsync("throttleclear"))?.ThrottlePerMinute);

    Assert.Equal(HttpStatusCode.BadRequest,
        (await client.PatchAsJsonAsync("/admin/apps/throttleclear/throttle", new { perMinute = 0 })).StatusCode);
    Assert.Equal(5, (await Apps.GetAsync("throttleclear"))?.ThrottlePerMinute);

    HttpResponseMessage cleared = await client.PatchAsJsonAsync("/admin/apps/throttleclear/throttle", new { perMinute = (int?)null });
    Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
    Assert.Null((await Apps.GetAsync("throttleclear"))?.ThrottlePerMinute);
  }

  [Fact]
  public async Task TrafficControl_UnknownApp_Returns404_DeletedApp_Returns409()
  {
    await Apps.RegisterAsync("tcdeleted", "owner");
    HttpClient client = await GetAuthenticatedClientAsync();
    await client.DeleteAsync("/admin/apps/tcdeleted");

    Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/admin/apps/never-registered/pause", null)).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/admin/apps/never-registered/resume", null)).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound,
        (await client.PatchAsJsonAsync("/admin/apps/never-registered/throttle", new { perMinute = 1 })).StatusCode);

    Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/admin/apps/tcdeleted/pause", null)).StatusCode);
    Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/admin/apps/tcdeleted/resume", null)).StatusCode);
    Assert.Equal(HttpStatusCode.Conflict,
        (await client.PatchAsJsonAsync("/admin/apps/tcdeleted/throttle", new { perMinute = 1 })).StatusCode);
  }

  [Fact]
  public async Task TrafficControl_RecordsAuditEntries()
  {
    await Apps.RegisterAsync("tcaudit", "owner");
    HttpClient client = await GetAuthenticatedClientAsync();
    await client.PostAsync("/admin/apps/tcaudit/pause", null);
    await client.PostAsync("/admin/apps/tcaudit/resume", null);
    await client.PatchAsJsonAsync("/admin/apps/tcaudit/throttle", new { perMinute = 9 });

    JsonElement rows = await client.GetFromJsonAsync<JsonElement>("/admin/audit?target=tcaudit");

    string[] actions = [.. rows.EnumerateArray().Select(r => r.GetProperty("action").GetString()!)];
    Assert.Contains("app.pause", actions);
    Assert.Contains("app.resume", actions);
    Assert.Contains("app.throttle", actions);
  }

  // ── Forced disconnect on delete ────────────────────────────────────────

  [Fact]
  public async Task DeleteApp_ClosesConnectionsSubscribedToTheApp()
  {
    await Apps.RegisterAsync("killapp", "owner");
    HttpClient client = await GetAuthenticatedClientAsync();
    using WebSocket ws = await ConnectAsync("client-kill", subscribeTo: "killapp/room");
    Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>("/admin/apps/killapp")).GetProperty("activeConnections").GetInt32());

    Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/admin/apps/killapp")).StatusCode);

    ErrorMessage notice = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("UNKNOWN_APP", notice.Code);
    Assert.Null(await ReceiveAsync(ws));

    JsonElement audit = await client.GetFromJsonAsync<JsonElement>("/admin/audit?target=killapp");
    JsonElement entry = audit.EnumerateArray().Single(r => r.GetProperty("action").GetString() == "app.delete");
    Assert.Equal(1, entry.GetProperty("details").GetProperty("disconnected").GetInt32());
  }

  [Fact]
  public async Task DeleteApp_LeavesConnectionsOfOtherAppsOpen()
  {
    await Apps.RegisterAsync("doomedapp", "owner");
    await Apps.RegisterAsync("bystanderapp", "owner");
    HttpClient client = await GetAuthenticatedClientAsync();
    using WebSocket bystander = await ConnectAsync("client-bystander", subscribeTo: "bystanderapp/room");

    await client.DeleteAsync("/admin/apps/doomedapp");

    await SendAsync(bystander, new PublishMessage("bystanderapp/room", CreateEvent("bystanderapp/room") with { ClientId = "client-bystander" }));
    Assert.IsType<AckMessage>(await ReceiveAsync(bystander));
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private static readonly JsonSerializerOptions WsJson = VestaJsonOptions.Default;

  private async Task<WebSocket> ConnectAsync(string clientId, string? subscribeTo = null)
  {
    Microsoft.AspNetCore.TestHost.WebSocketClient wsClient = _fixture.Factory.Server.CreateWebSocketClient();
    WebSocket ws = await wsClient.ConnectAsync(new Uri(_fixture.Factory.Server.BaseAddress, "/ws"), CancellationToken.None);
    string[] channels = subscribeTo is null ? [] : [subscribeTo];
    await SendAsync(ws, new HelloMessage(clientId, channels, new Dictionary<string, long>()));
    Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));
    return ws;
  }

  private static async Task SendAsync(WebSocket ws, ProtocolMessage message)
  {
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes<ProtocolMessage>(message, WsJson);
    await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
  }

  private static async Task<ProtocolMessage?> ReceiveAsync(WebSocket ws)
  {
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
    byte[] buffer = new byte[16384];
    using MemoryStream stream = new();
    ValueWebSocketReceiveResult result;
    do
    {
      result = await ws.ReceiveAsync(buffer.AsMemory(), timeout.Token);
      if (result.MessageType == WebSocketMessageType.Close)
        return null;
      stream.Write(buffer, 0, result.Count);
    }
    while (!result.EndOfMessage);
    stream.Position = 0;
    return await JsonSerializer.DeserializeAsync<ProtocolMessage>(stream, WsJson, timeout.Token);
  }
  private static VestaEvent CreateEvent(string channelId) => new(
      Id: Guid.NewGuid(),
      ChannelId: channelId,
      Timestamp: DateTimeOffset.UtcNow,
      ClientId: "test-client",
      EventType: "test",
      Payload: JsonDocument.Parse("""{"hello":"world"}""").RootElement);

  private async Task<HttpClient> GetAuthenticatedClientAsync()
  {
    HttpClient client = _fixture.Factory.CreateClient();
    HttpResponseMessage challengeResp = await client.PostAsync("/admin/auth/challenge", null);
    JsonElement challenge = await challengeResp.Content.ReadFromJsonAsync<JsonElement>();
    string nonce = challenge.GetProperty("nonce").GetString()!;
    byte[] signature = _fixture.AdminIdentity.Sign(Base64Url.Decode(nonce));

    HttpResponseMessage verifyResp = await client.PostAsJsonAsync("/admin/auth/verify", new
    {
      publicKey = Base64Url.Encode(_fixture.AdminIdentity.PublicKey),
      nonce,
      signature = Base64Url.Encode(signature),
    });
    Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);
    JsonElement token = await verifyResp.Content.ReadFromJsonAsync<JsonElement>();
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.GetProperty("token").GetString());
    return client;
  }
}
