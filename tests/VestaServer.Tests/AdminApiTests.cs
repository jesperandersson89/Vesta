using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using VestaCore.Identity;
using VestaCore.Utilities;

namespace VestaServer.Tests;

/// <summary>
/// Integration tests for the <c>/admin/*</c> HTTP API: challenge → bearer
/// auth flow plus the read endpoints. Uses the same bootstrap admin pattern
/// as <see cref="DeleteChannelTests"/>.
/// </summary>
public class AdminApiTests : IClassFixture<AdminApiTests.Fixture>
{
  private readonly Fixture _fixture;

  public AdminApiTests(Fixture fixture) => _fixture = fixture;

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
      });
    }

    public void Dispose()
    {
      Factory.Dispose();
      AdminIdentity.Dispose();
    }
  }

  // ── Auth ───────────────────────────────────────────────────────────────

  [Fact]
  public async Task Challenge_ReturnsNonceAndExpiry()
  {
    HttpClient client = _fixture.Factory.CreateClient();
    HttpResponseMessage resp = await client.PostAsync("/admin/auth/challenge", content: null);
    Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

    JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
    Assert.False(string.IsNullOrEmpty(body.GetProperty("nonce").GetString()));
    Assert.True(body.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
  }

  [Fact]
  public async Task Verify_WithAdminKey_IssuesToken()
  {
    string token = await GetTokenAsync();
    Assert.False(string.IsNullOrEmpty(token));
  }

  [Fact]
  public async Task Verify_WithNonAdminKey_Returns401()
  {
    using VestaIdentity outsider = VestaIdentity.Generate();
    HttpClient client = _fixture.Factory.CreateClient();

    HttpResponseMessage chResp = await client.PostAsync("/admin/auth/challenge", null);
    JsonElement challenge = await chResp.Content.ReadFromJsonAsync<JsonElement>();
    string nonce = challenge.GetProperty("nonce").GetString()!;
    byte[] nonceBytes = Base64Url.Decode(nonce);
    byte[] signature = outsider.Sign(nonceBytes);

    HttpResponseMessage verify = await client.PostAsJsonAsync("/admin/auth/verify", new
    {
      publicKey = Base64Url.Encode(outsider.PublicKey),
      nonce,
      signature = Base64Url.Encode(signature),
    });
    Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);
  }

  [Fact]
  public async Task Verify_WithWrongSignature_Returns401()
  {
    HttpClient client = _fixture.Factory.CreateClient();
    HttpResponseMessage chResp = await client.PostAsync("/admin/auth/challenge", null);
    JsonElement challenge = await chResp.Content.ReadFromJsonAsync<JsonElement>();
    string nonce = challenge.GetProperty("nonce").GetString()!;

    HttpResponseMessage verify = await client.PostAsJsonAsync("/admin/auth/verify", new
    {
      publicKey = Base64Url.Encode(_fixture.AdminIdentity.PublicKey),
      nonce,
      signature = Base64Url.Encode(new byte[64]),
    });
    Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);
  }

  [Fact]
  public async Task ProtectedEndpoint_WithoutToken_Returns401()
  {
    HttpClient client = _fixture.Factory.CreateClient();
    HttpResponseMessage resp = await client.GetAsync("/admin/channels");
    Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
  }

  [Fact]
  public async Task ProtectedEndpoint_WithBadToken_Returns401()
  {
    HttpClient client = _fixture.Factory.CreateClient();
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-real-token");
    HttpResponseMessage resp = await client.GetAsync("/admin/channels");
    Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
  }

  // ── Channels / Apps / Metrics ─────────────────────────────────────────

  [Fact]
  public async Task ListChannels_WithToken_Returns200()
  {
    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage resp = await client.GetAsync("/admin/channels");
    Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal(JsonValueKind.Array, body.ValueKind);
  }

  [Fact]
  public async Task Metrics_Returns200WithCounters()
  {
    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage resp = await client.GetAsync("/admin/metrics");
    Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
    Assert.True(body.TryGetProperty("activeConnections", out _));
    Assert.True(body.TryGetProperty("totalApps", out _));
    Assert.True(body.TryGetProperty("totalChannels", out _));
  }

  [Fact]
  public async Task DeleteChannel_NotFound_Returns404()
  {
    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage resp = await client.DeleteAsync("/admin/channels/never/existed");
    Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
  }

  [Fact]
  public async Task ListApps_ReturnsArray()
  {
    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage resp = await client.GetAsync("/admin/apps");
    Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal(JsonValueKind.Array, body.ValueKind);
  }

  [Fact]
  public async Task GetUsage_UnknownApp_Returns404()
  {
    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage resp = await client.GetAsync("/admin/apps/never-registered/usage");
    Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
  }

  [Fact]
  public async Task GetUsage_RegisteredApp_ReturnsRollup()
  {
    VestaServer.Storage.IAppStore appStore = _fixture.Factory.Services.GetRequiredService<VestaServer.Storage.IAppStore>();
    await appStore.RegisterAsync("usageapp", "owner-usage");
    await appStore.SetQuotasAsync("usageapp", new VestaServer.Storage.AppQuotas(MaxMessagesPerMonth: 100));

    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage resp = await client.GetAsync("/admin/apps/usageapp/usage");
    Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

    JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal("usageapp", body.GetProperty("id").GetString());
    Assert.True(body.TryGetProperty("periodStart", out _));
    Assert.True(body.TryGetProperty("messages", out _));
    Assert.True(body.TryGetProperty("storageBytes", out _));
    Assert.Equal(0, body.GetProperty("channelCount").GetInt32());
    Assert.Equal(100, body.GetProperty("quotas").GetProperty("maxMessagesPerMonth").GetInt64());
  }

  [Fact]
  public async Task SetOwner_RegisteredApp_RebindsOwner()
  {
    VestaServer.Storage.IAppStore appStore = _fixture.Factory.Services.GetRequiredService<VestaServer.Storage.IAppStore>();
    await appStore.RegisterAsync("ownerapp", "owner-original");

    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage resp = await client.PatchAsJsonAsync("/admin/apps/ownerapp/owner", new { ownerClientId = "owner-rotated" });
    Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

    VestaServer.Storage.AppInfo? app = await appStore.GetAsync("ownerapp");
    Assert.Equal("owner-rotated", app?.OwnerClientId);
  }

  [Fact]
  public async Task SetOwner_UnknownApp_Returns404()
  {
    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage resp = await client.PatchAsJsonAsync("/admin/apps/never-registered/owner", new { ownerClientId = "someone" });
    Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
  }

  [Fact]
  public async Task SetOwner_EmptyClientId_Returns400()
  {
    VestaServer.Storage.IAppStore appStore = _fixture.Factory.Services.GetRequiredService<VestaServer.Storage.IAppStore>();
    await appStore.RegisterAsync("emptyownerapp", "owner-original");

    HttpClient client = await GetAuthenticatedClientAsync();
    HttpResponseMessage resp = await client.PatchAsJsonAsync("/admin/apps/emptyownerapp/owner", new { ownerClientId = "" });
    Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private async Task<string> GetTokenAsync()
  {
    HttpClient client = _fixture.Factory.CreateClient();
    HttpResponseMessage chResp = await client.PostAsync("/admin/auth/challenge", null);
    JsonElement challenge = await chResp.Content.ReadFromJsonAsync<JsonElement>();
    string nonce = challenge.GetProperty("nonce").GetString()!;
    byte[] nonceBytes = Base64Url.Decode(nonce);
    byte[] signature = _fixture.AdminIdentity.Sign(nonceBytes);

    HttpResponseMessage vfResp = await client.PostAsJsonAsync("/admin/auth/verify", new
    {
      publicKey = Base64Url.Encode(_fixture.AdminIdentity.PublicKey),
      nonce,
      signature = Base64Url.Encode(signature),
    });
    Assert.Equal(HttpStatusCode.OK, vfResp.StatusCode);
    JsonElement tokenBody = await vfResp.Content.ReadFromJsonAsync<JsonElement>();
    return tokenBody.GetProperty("token").GetString()!;
  }

  private async Task<HttpClient> GetAuthenticatedClientAsync()
  {
    string token = await GetTokenAsync();
    HttpClient client = _fixture.Factory.CreateClient();
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    return client;
  }
}
