using Vesta;

namespace VestaClient.Tests;

/// <summary><see cref="VestaConnection.GetDeviceGroupMembersAsync"/> pages through a group channel larger than one relay page.</summary>
public class DeviceGroupMembersTests
{
    [Fact]
    public async Task GetDeviceGroupMembersAsync_ReadsEveryPage_NotJustTheFirstBatch()
    {
        string groupId = IdentityLinkBuilder.GenerateGroupId();
        string channel = DeviceGroupChannel.For(groupId);
        using VestaIdentity founder = VestaIdentity.Generate();
        List<VestaIdentity> others = Enumerable.Range(0, 4).Select(_ => VestaIdentity.Generate()).ToList();

        List<VestaEvent> events = [IdentityLinkBuilder.BuildAnnounce(founder, groupId, "Founder")];
        events.AddRange(others.Select(o => IdentityLinkBuilder.BuildLink(founder, groupId, o.PublicKey)));
        List<SequencedEvent> stored = events.Select((e, i) => new SequencedEvent(e, i + 1, DateTimeOffset.UtcNow)).ToList();

        await using ScriptedFetchRelay relay = await ScriptedFetchRelay.StartAsync(stored, maxPage: 2);
        VestaAppConfig config = new("game", founder.PublicKey, [relay.Url]);
        await using VestaConnection connection = new(founder, config, relayDirectory: new RelayDirectory(config));
        await connection.ConnectAsync(relay.Url, []);

        DeviceGroup group = await connection.GetDeviceGroupMembersAsync(groupId);

        Assert.Equal(5, group.Count);
        Assert.True(group.IsMember(founder.ClientId));
        Assert.All(others, o => Assert.True(group.IsMember(o.ClientId)));
        Assert.All(relay.Fetches, f => Assert.Equal(channel, f.ChannelId));
        Assert.True(relay.Fetches.Count >= 3);
        foreach (VestaIdentity o in others) o.Dispose();
    }

    [Fact]
    public async Task GetDeviceGroupMembersAsync_EmptyChannel_ReturnsAnEmptyGroupWithoutWaitingForATimeout()
    {
        await using ScriptedFetchRelay relay = await ScriptedFetchRelay.StartAsync(eventCount: 0, maxPage: 2);
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaAppConfig config = new("game", identity.PublicKey, [relay.Url]);
        await using VestaConnection connection = new(identity, config, relayDirectory: new RelayDirectory(config));
        await connection.ConnectAsync(relay.Url, []);

        DeviceGroup group = await connection.GetDeviceGroupMembersAsync(IdentityLinkBuilder.GenerateGroupId(), TimeSpan.FromSeconds(2));

        Assert.Equal(0, group.Count);
    }
}
