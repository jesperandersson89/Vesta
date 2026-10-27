using Vesta;

namespace VestaClient.Tests;

/// <summary>
/// <see cref="VestaConnection.RecoverIdentityAsync"/> / <see cref="VestaConnection.RestoreIdentityAsync"/> against a
/// scripted relay holding a real backup event (cheap Argon2id cost so the tests stay fast).
/// </summary>
public class RecoverIdentityTests
{
    private static readonly IdentityBackupOptions Fast = new(MemoryKiB: 256, Iterations: 1);
    private static readonly VestaAppConfig Config = new("game", VestaIdentity.Generate().PublicKey, [new Uri("ws://unused.invalid/ws")]);

    [Fact]
    public async Task RecoverIdentityAsync_WithTheRightLogin_ReturnsTheOriginalIdentity()
    {
        using VestaIdentity original = VestaIdentity.Generate();
        await using ScriptedFetchRelay relay = await RelayHoldingBackupAsync(original, "alice", "hunter2");

        using VestaIdentity? recovered = await VestaConnection.RecoverIdentityAsync(
            Config, "alice", "hunter2", Fast, relays: [relay.Url]);

        Assert.NotNull(recovered);
        Assert.Equal(original.ClientId, recovered.ClientId);
        Assert.Equal(original.PublicKey, recovered.PublicKey);
    }

    [Fact]
    public async Task RecoverIdentityAsync_UsernameSpellingIsNormalised()
    {
        using VestaIdentity original = VestaIdentity.Generate();
        await using ScriptedFetchRelay relay = await RelayHoldingBackupAsync(original, "alice", "hunter2");

        using VestaIdentity? recovered = await VestaConnection.RecoverIdentityAsync(
            Config, "  ALICE ", "hunter2", Fast, relays: [relay.Url]);

        Assert.Equal(original.ClientId, recovered?.ClientId);
    }

    [Fact]
    public async Task RecoverIdentityAsync_WrongPassword_ReturnsNull()
    {
        using VestaIdentity original = VestaIdentity.Generate();
        await using ScriptedFetchRelay relay = await RelayHoldingBackupAsync(original, "alice", "hunter2");

        Assert.Null(await VestaConnection.RecoverIdentityAsync(Config, "alice", "wrong", Fast, relays: [relay.Url]));
        Assert.Null(await VestaConnection.RecoverIdentityAsync(Config, "bob", "hunter2", Fast, relays: [relay.Url]));
    }

    [Fact]
    public async Task RecoverIdentityAsync_JunkOnTheChannelIsSkipped()
    {
        using VestaIdentity original = VestaIdentity.Generate();
        RelayBackup backup = RelayBackup.Derive("game", "alice", "hunter2", Fast);
        SequencedEvent junk = ScriptedFetchRelay.Stored(backup.ChannelId, 1) with
        {
            Event = ScriptedFetchRelay.Stored(backup.ChannelId, 1).Event with { EventType = RelayBackup.EventType },
        };
        SequencedEvent real = new(backup.CreateEvent(original), 2, DateTimeOffset.UtcNow);
        await using ScriptedFetchRelay relay = await ScriptedFetchRelay.StartAsync([junk, real], maxPage: 1);

        using VestaIdentity? recovered = await VestaConnection.RecoverIdentityAsync(
            Config, "alice", "hunter2", Fast, relays: [relay.Url]);

        Assert.Equal(original.ClientId, recovered?.ClientId);
    }

    [Fact]
    public async Task RecoverIdentityAsync_FetchesOnlyTheBackupChannel()
    {
        using VestaIdentity original = VestaIdentity.Generate();
        await using ScriptedFetchRelay relay = await RelayHoldingBackupAsync(original, "alice", "hunter2");

        using VestaIdentity? recovered = await VestaConnection.RecoverIdentityAsync(
            Config, "alice", "hunter2", Fast, relays: [relay.Url]);

        Assert.NotNull(recovered);
        string channel = RelayBackup.Derive("game", "alice", "hunter2", Fast).ChannelId;
        Assert.All(relay.Fetches, fetch => Assert.Equal(channel, fetch.ChannelId));
    }

    private static async Task<ScriptedFetchRelay> RelayHoldingBackupAsync(VestaIdentity identity, string username, string password)
    {
        RelayBackup backup = RelayBackup.Derive("game", username, password, Fast);
        return await ScriptedFetchRelay.StartAsync([new SequencedEvent(backup.CreateEvent(identity), 1, DateTimeOffset.UtcNow)], maxPage: 100);
    }
}
