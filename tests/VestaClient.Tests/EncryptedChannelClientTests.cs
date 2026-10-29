using Vesta;

namespace VestaClient.Tests;

/// <summary>
/// Client side of encrypted channels, phase A: WELCOME <c>features</c>, the
/// <see cref="VestaConnection.CreateChannelAsync"/> <c>encrypted</c> gate, <see cref="VestaConnection.RevokeAccessAsync"/>
/// and the <c>ENCRYPTION_REQUIRED</c> classification. Mirrors <c>encrypted-channels.test.mjs</c> and
/// <c>test_encrypted_channels.py</c>.
/// </summary>
public class EncryptedChannelClientTests
{
    [Fact]
    public async Task Features_FromWelcome_AreExposed_AndEmptyOnLegacyRelays()
    {
        await using ScriptedFetchRelay modern = await ScriptedFetchRelay.StartAsync(0, 3, features: [RelayFeatures.EncryptedChannels]);
        await using VestaConnection modernConnection = await ConnectAsync(modern);
        Assert.Equal([RelayFeatures.EncryptedChannels], modernConnection.Features);
        Assert.True(modernConnection.SupportsFeature(RelayFeatures.EncryptedChannels));

        await using ScriptedFetchRelay legacy = await ScriptedFetchRelay.StartAsync(0, 3);
        await using VestaConnection legacyConnection = await ConnectAsync(legacy);
        Assert.Empty(legacyConnection.Features);
        Assert.False(legacyConnection.SupportsFeature(RelayFeatures.EncryptedChannels));
    }

    [Fact]
    public async Task CreateChannelAsync_Encrypted_SendsTheFlag_WhenTheRelaySupportsIt()
    {
        await using ScriptedFetchRelay relay = await ScriptedFetchRelay.StartAsync(0, 3, features: [RelayFeatures.EncryptedChannels]);
        await using VestaConnection connection = await ConnectAsync(relay);
        TaskCompletionSource<AckMessage> acked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnAck += ack => acked.TrySetResult(ack);

        await connection.CreateChannelAsync("notes/secret", "private", encrypted: true);
        await acked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        CreateChannelMessage create = Assert.IsType<CreateChannelMessage>(Assert.Single(relay.Controls));
        Assert.Equal("notes/secret", create.ChannelId);
        Assert.Equal("private", create.Visibility);
        Assert.True(create.Encrypted);
    }

    [Fact]
    public async Task CreateChannelAsync_NotEncrypted_OmitsTheFlag()
    {
        await using ScriptedFetchRelay relay = await ScriptedFetchRelay.StartAsync(0, 3, features: [RelayFeatures.EncryptedChannels]);
        await using VestaConnection connection = await ConnectAsync(relay);
        TaskCompletionSource<AckMessage> acked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnAck += ack => acked.TrySetResult(ack);

        await connection.CreateChannelAsync("notes/plain");
        await acked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        CreateChannelMessage create = Assert.IsType<CreateChannelMessage>(Assert.Single(relay.Controls));
        Assert.Null(create.Encrypted);
    }

    [Fact]
    public async Task CreateChannelAsync_Encrypted_Throws_WhenTheRelayDoesNotAdvertiseTheFeature()
    {
        await using ScriptedFetchRelay relay = await ScriptedFetchRelay.StartAsync(0, 3);
        await using VestaConnection connection = await ConnectAsync(relay);

        await Assert.ThrowsAsync<NotSupportedException>(() => connection.CreateChannelAsync("notes/secret", encrypted: true));

        Assert.Empty(relay.Controls);
    }

    [Fact]
    public async Task RevokeAccessAsync_SendsRevokeAccess()
    {
        await using ScriptedFetchRelay relay = await ScriptedFetchRelay.StartAsync(0, 3, features: [RelayFeatures.EncryptedChannels]);
        await using VestaConnection connection = await ConnectAsync(relay);
        TaskCompletionSource<AckMessage> acked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnAck += ack => acked.TrySetResult(ack);

        await connection.RevokeAccessAsync("notes/secret", "former-member");
        AckMessage ack = await acked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("notes/secret", ack.ChannelId);
        RevokeAccessMessage revoke = Assert.IsType<RevokeAccessMessage>(Assert.Single(relay.Controls));
        Assert.Equal("former-member", revoke.ClientId);
    }

    [Fact]
    public void Classify_EncryptionRequired_IsEventFatalButNotALimit()
    {
        VestaErrorCodes.Classification classification = VestaErrorCodes.Classify(VestaErrorCodes.EncryptionRequired);

        Assert.False(classification.IsLimit);
        Assert.False(classification.IsTransient);
        Assert.True(classification.IsEventFatal);
    }

    private static async Task<VestaConnection> ConnectAsync(ScriptedFetchRelay relay)
    {
        VestaIdentity identity = VestaIdentity.Generate();
        VestaAppConfig config = new("notes", identity.PublicKey, [relay.Url]);
        VestaConnection connection = new(identity, config, relayDirectory: new RelayDirectory(config));
        await connection.ConnectAsync(relay.Url, []);
        return connection;
    }
}
