using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vesta;

namespace AccountServer.Tests;

/// <summary>
/// The "log in with a username and password on any device" flow end to end: the SDK derives the login secret and
/// the encrypted identity on the client, the reference account server stores and returns them without being able
/// to read either.
/// </summary>
public sealed class AccountFlowTests : IDisposable
{
    private static readonly IdentityBackupOptions Fast = new(MemoryKiB: 256, Iterations: 1);

    private readonly string _dataFile = Path.Combine(Path.GetTempPath(), $"accounts-{Guid.NewGuid():N}.json");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public AccountFlowTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Accounts:DataFile", _dataFile));
        _http = _factory.CreateClient();
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        File.Delete(_dataFile);
    }

    [Fact]
    public async Task SignUpOnOneDevice_LogInOnAnother_RecoversTheSameIdentity()
    {
        using VestaIdentity identity = VestaIdentity.Generate();

        Assert.Equal(HttpStatusCode.Created, (await SignUpAsync("alice", "correct horse", identity)).StatusCode);

        using VestaIdentity? onNewDevice = await LogInAsync("alice", "correct horse");
        Assert.NotNull(onNewDevice);
        Assert.Equal(identity.ClientId, onNewDevice.ClientId);
        Assert.Equal(identity.ExportPrivateKey(), onNewDevice.ExportPrivateKey());
    }

    [Fact]
    public async Task ServerStoresNeitherThePasswordNorThePrivateKey()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        await SignUpAsync("alice", "correct horse", identity);

        string stored = await File.ReadAllTextAsync(_dataFile);

        Assert.DoesNotContain("correct horse", stored);
        Assert.DoesNotContain(Base64Url.Encode(identity.ExportPrivateKey()), stored);
        Assert.DoesNotContain(RelayBackup.Derive("game", "alice", "correct horse", Fast).AuthSecret, stored);
        Assert.Contains("vesta-identity-backup", stored);
    }

    [Fact]
    public async Task WrongPasswordAndUnknownUser_AreIndistinguishable()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        await SignUpAsync("alice", "right", identity);

        HttpResponseMessage wrongPassword = await LoginRawAsync("alice", "wrong");
        HttpResponseMessage unknownUser = await LoginRawAsync("nobody", "right");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync(), await unknownUser.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UsernamesAreNormalised_AndDuplicatesAreRejected()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        await SignUpAsync("Alice ", "pw", identity);

        Assert.Equal(HttpStatusCode.Conflict, (await SignUpAsync("alice", "pw", VestaIdentity.Generate())).StatusCode);
        using VestaIdentity? restored = await LogInAsync(" ALICE", "pw");
        Assert.Equal(identity.ClientId, restored?.ClientId);
    }

    [Fact]
    public async Task ChangingThePassword_ReKeysTheAccount_AndKeepsTheIdentity()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        await SignUpAsync("alice", "old password", identity);
        RelayBackup oldKeys = RelayBackup.Derive("game", "alice", "old password", Fast);
        RelayBackup newKeys = RelayBackup.Derive("game", "alice", "new password", Fast);

        HttpResponseMessage changed = await _http.PostAsJsonAsync("/accounts/password", new
        {
            username = "alice",
            authSecret = oldKeys.AuthSecret,
            newAuthSecret = newKeys.AuthSecret,
            blob = newKeys.ExportBlob(identity),
        });

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Null(await LogInAsync("alice", "old password"));
        using VestaIdentity? restored = await LogInAsync("alice", "new password");
        Assert.Equal(identity.ClientId, restored?.ClientId);
    }

    [Fact]
    public async Task UpdatingTheBlob_RequiresTheLoginSecret()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        using VestaIdentity replacement = VestaIdentity.Generate();
        await SignUpAsync("alice", "pw", identity);
        RelayBackup keys = RelayBackup.Derive("game", "alice", "pw", Fast);
        RelayBackup stranger = RelayBackup.Derive("game", "alice", "guess", Fast);

        HttpResponseMessage denied = await _http.PutAsJsonAsync("/accounts/blob", new { username = "alice", authSecret = stranger.AuthSecret, blob = keys.ExportBlob(replacement) });
        HttpResponseMessage allowed = await _http.PutAsJsonAsync("/accounts/blob", new { username = "alice", authSecret = keys.AuthSecret, blob = keys.ExportBlob(replacement) });

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        using VestaIdentity? restored = await LogInAsync("alice", "pw");
        Assert.Equal(replacement.ClientId, restored?.ClientId);
    }

    [Fact]
    public async Task DeletingTheAccount_RemovesIt()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        await SignUpAsync("alice", "pw", identity);
        RelayBackup keys = RelayBackup.Derive("game", "alice", "pw", Fast);

        HttpResponseMessage deleted = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/accounts")
        {
            Content = JsonContent.Create(new { username = "alice", authSecret = keys.AuthSecret }),
        });

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(await LogInAsync("alice", "pw"));
    }

    [Fact]
    public async Task RepeatedFailures_ThrottleLogins_EvenWithTheRightSecret()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        await SignUpAsync("alice", "pw", identity);

        for (int i = 0; i < LoginThrottle.UserLimit; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginRawAsync("alice", "wrong")).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginRawAsync("alice", "pw")).StatusCode);
    }

    [Fact]
    public async Task MalformedRequests_AreRejected()
    {
        HttpResponseMessage shortSecret = await _http.PostAsJsonAsync("/accounts", new { username = "alice", authSecret = "short", blob = "{}" });
        HttpResponseMessage shortName = await _http.PostAsJsonAsync("/accounts", new { username = "al", authSecret = new string('A', 43), blob = "{}" });
        HttpResponseMessage hugeBlob = await _http.PostAsJsonAsync("/accounts", new { username = "alice", authSecret = new string('A', 43), blob = new string('x', 5000) });

        Assert.Equal(HttpStatusCode.BadRequest, shortSecret.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, shortName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, hugeBlob.StatusCode);
    }

    private Task<HttpResponseMessage> SignUpAsync(string username, string password, VestaIdentity identity)
    {
        RelayBackup keys = RelayBackup.Derive("game", username, password, Fast);
        return _http.PostAsJsonAsync("/accounts", new { username, authSecret = keys.AuthSecret, blob = keys.ExportBlob(identity) });
    }

    private Task<HttpResponseMessage> LoginRawAsync(string username, string password)
    {
        RelayBackup keys = RelayBackup.Derive("game", username, password, Fast);
        return _http.PostAsJsonAsync("/accounts/login", new { username, authSecret = keys.AuthSecret });
    }

    private async Task<VestaIdentity?> LogInAsync(string username, string password)
    {
        HttpResponseMessage response = await LoginRawAsync(username, password);
        if (!response.IsSuccessStatusCode)
            return null;

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return RelayBackup.Derive("game", username, password, Fast).ImportBlob(body.RootElement.GetProperty("blob").GetString()!);
    }
}
