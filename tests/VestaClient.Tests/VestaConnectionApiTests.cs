using System.Text.Json;
using Vesta;

namespace VestaClient.Tests;

/// <summary>Tests for the zero-plumbing connection surface: string app config, identity-first ctor, owner helpers.</summary>
public sealed class VestaConnectionApiTests : IDisposable
{
    private readonly VestaIdentity _owner = VestaIdentity.Generate();
    private readonly string _appId = $"api-{Guid.NewGuid():N}";

    public void Dispose() => _owner.Dispose();

    private VestaAppConfig Config() => new(_appId, Base64Url.Encode(_owner.PublicKey), "ws://localhost:1/ws");

    private VestaConnection Connection(VestaIdentity identity, SqliteClientEventStore store) =>
        new(identity, Config(), store, new RelayDirectory(Config()));

    [Fact]
    public void VestaAppConfig_StringConstructor_DecodesKeyAndParsesRelays()
    {
        VestaAppConfig config = new("chess", Base64Url.Encode(_owner.PublicKey), " ws://a.example/ws ", "ws://b.example/ws");

        Assert.Equal(_owner.PublicKey, config.OwnerPublicKey);
        Assert.Equal([new Uri("ws://a.example/ws"), new Uri("ws://b.example/ws")], config.DefaultRelays);
    }

    [Fact]
    public async Task Constructor_WithIdentity_UsesIdentityClientId()
    {
        using SqliteClientEventStore store = SqliteClientEventStore.CreateInMemory();
        await using VestaConnection connection = Connection(_owner, store);

        VestaEvent evt = new(Guid.NewGuid(), $"{_appId}/room", DateTimeOffset.UtcNow, _owner.ClientId, "app.test",
            JsonSerializer.SerializeToElement(new { n = 1 }));
        await connection.PublishAsync(evt);

        IReadOnlyList<OutboxEntry> pending = await store.GetPendingOutboxAsync();
        Assert.Single(pending);
        Assert.NotNull(pending[0].Event.Signature);
    }

    [Fact]
    public async Task PublishRelayManifestAsync_AsOwner_QueuesSignedManifestWithNextVersion()
    {
        using SqliteClientEventStore store = SqliteClientEventStore.CreateInMemory();
        await using VestaConnection connection = Connection(_owner, store);

        RelayManifest manifest = await connection.PublishRelayManifestAsync([new Uri("wss://new.example/ws")]);

        Assert.Equal(1, manifest.Version);
        Assert.True(ManifestSigner.Verify(manifest, _owner.PublicKey));
        IReadOnlyList<OutboxEntry> pending = await store.GetPendingOutboxAsync();
        Assert.Equal(RelayManifest.ChannelFor(_appId), pending.Single().Event.ChannelId);
        Assert.Equal(RelayManifest.EventType, pending.Single().Event.EventType);
        RelayManifest? roundTripped = pending.Single().Event.Payload.Deserialize<RelayManifest>(VestaJsonOptions.Default);
        Assert.Equal(manifest.Signature, roundTripped?.Signature);
    }

    [Fact]
    public async Task PublishRelayManifestAsync_AsNonOwner_Throws()
    {
        using VestaIdentity stranger = VestaIdentity.Generate();
        using SqliteClientEventStore store = SqliteClientEventStore.CreateInMemory();
        await using VestaConnection connection = Connection(stranger, store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.PublishRelayManifestAsync([new Uri("wss://new.example/ws")]));
    }

    [Fact]
    public async Task DiscoverRelaysAsync_WithNoRelayCandidates_Throws()
    {
        using SqliteClientEventStore store = SqliteClientEventStore.CreateInMemory();
        await using VestaConnection connection = Connection(_owner, store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.DiscoverRelaysAsync());
    }
}
