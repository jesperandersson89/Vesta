using VestaCore.Channels;
using VestaCore.Events;
using VestaCore.Identity;

namespace VestaCore.Tests.Identity;

public class DeviceGroupProjectionTests
{
    [Fact]
    public void GenerateGroupId_ProducesValidChannelComponent()
    {
        string groupId = IdentityLinkBuilder.GenerateGroupId();

        Assert.Equal(32, groupId.Length);
        Assert.True(ChannelId.IsValid(DeviceGroupChannel.For(groupId)));
        Assert.All(groupId, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')));
    }

    [Fact]
    public void DeviceGroupChannel_For_UsesReservedPrefix()
    {
        string channel = DeviceGroupChannel.For("abc123");
        Assert.Equal("vesta/identity/abc123", channel);
        Assert.True(ChannelId.IsProtocolChannel(channel));
    }

    [Fact]
    public void FounderAnnounce_BecomesTrustedMember()
    {
        using VestaIdentity founder = VestaIdentity.Generate();
        string groupId = IdentityLinkBuilder.GenerateGroupId();
        DeviceGroupProjection projection = new(groupId);

        VestaEvent announce = IdentityLinkBuilder.BuildAnnounce(founder, groupId);
        projection.ApplyLocal(announce);

        DeviceGroup state = projection.State;
        Assert.Equal(1, state.Count);
        Assert.True(state.IsMember(founder.ClientId));
    }

    [Fact]
    public void LinkFromTrustedMember_AddsTargetToGroup()
    {
        using VestaIdentity founder = VestaIdentity.Generate();
        using VestaIdentity newDevice = VestaIdentity.Generate();
        string groupId = IdentityLinkBuilder.GenerateGroupId();
        DeviceGroupProjection projection = new(groupId);

        projection.ApplyLocal(IdentityLinkBuilder.BuildAnnounce(founder, groupId));
        projection.ApplyLocal(IdentityLinkBuilder.BuildLink(founder, groupId, newDevice.PublicKey));

        DeviceGroup state = projection.State;
        Assert.Equal(2, state.Count);
        Assert.True(state.IsMember(founder.ClientId));
        Assert.True(state.IsMember(newDevice.ClientId));
    }

    [Fact]
    public void LinkFromUntrustedDevice_IsIgnored()
    {
        using VestaIdentity founder = VestaIdentity.Generate();
        using VestaIdentity outsider = VestaIdentity.Generate();
        using VestaIdentity newDevice = VestaIdentity.Generate();
        string groupId = IdentityLinkBuilder.GenerateGroupId();
        DeviceGroupProjection projection = new(groupId);

        projection.ApplyLocal(IdentityLinkBuilder.BuildAnnounce(founder, groupId));
        // Outsider tries to link a new device — this must NOT propagate trust.
        projection.ApplyLocal(IdentityLinkBuilder.BuildLink(outsider, groupId, newDevice.PublicKey));

        DeviceGroup state = projection.State;
        Assert.Equal(1, state.Count);
        Assert.False(state.IsMember(newDevice.ClientId));
        Assert.False(state.IsMember(outsider.ClientId));
    }

    [Fact]
    public void TransitiveLink_PropagatesTrust()
    {
        using VestaIdentity founder = VestaIdentity.Generate();
        using VestaIdentity deviceB = VestaIdentity.Generate();
        using VestaIdentity deviceC = VestaIdentity.Generate();
        string groupId = IdentityLinkBuilder.GenerateGroupId();
        DeviceGroupProjection projection = new(groupId);

        projection.ApplyLocal(IdentityLinkBuilder.BuildAnnounce(founder, groupId));
        projection.ApplyLocal(IdentityLinkBuilder.BuildLink(founder, groupId, deviceB.PublicKey));
        // B is now trusted; B can vouch for C.
        projection.ApplyLocal(IdentityLinkBuilder.BuildLink(deviceB, groupId, deviceC.PublicKey));

        DeviceGroup state = projection.State;
        Assert.Equal(3, state.Count);
        Assert.True(state.IsMember(deviceC.ClientId));
    }

    [Fact]
    public void UnlinkFromTrustedMember_RemovesTarget()
    {
        using VestaIdentity founder = VestaIdentity.Generate();
        using VestaIdentity deviceB = VestaIdentity.Generate();
        string groupId = IdentityLinkBuilder.GenerateGroupId();
        DeviceGroupProjection projection = new(groupId);

        projection.ApplyLocal(IdentityLinkBuilder.BuildAnnounce(founder, groupId));
        projection.ApplyLocal(IdentityLinkBuilder.BuildLink(founder, groupId, deviceB.PublicKey));
        Assert.True(projection.State.IsMember(deviceB.ClientId));

        projection.ApplyLocal(IdentityLinkBuilder.BuildUnlink(founder, groupId, deviceB.PublicKey));
        Assert.False(projection.State.IsMember(deviceB.ClientId));
    }

    [Fact]
    public void LinkForDifferentGroup_IsIgnored()
    {
        using VestaIdentity founder = VestaIdentity.Generate();
        using VestaIdentity newDevice = VestaIdentity.Generate();
        string groupA = IdentityLinkBuilder.GenerateGroupId();
        string groupB = IdentityLinkBuilder.GenerateGroupId();
        DeviceGroupProjection projection = new(groupA);

        projection.ApplyLocal(IdentityLinkBuilder.BuildAnnounce(founder, groupA));
        // Link for a different group is published into THIS channel by mistake — ignore it.
        VestaEvent crossLink = IdentityLinkBuilder.BuildLink(founder, groupB, newDevice.PublicKey);
        // Force the channel ID to match (the projection has no way to know what channel it came from
        // other than via reduce, which keys on payload.groupId).
        VestaEvent forged = crossLink with { ChannelId = DeviceGroupChannel.For(groupA) };
        projection.ApplyLocal(forged);

        DeviceGroup state = projection.State;
        Assert.Equal(1, state.Count);
        Assert.False(state.IsMember(newDevice.ClientId));
    }

    [Fact]
    public void PairingPayload_RoundTrip()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        PairingPayload payload = new(
            GroupId: "abc123",
            PublicKey: Utilities.Base64Url.Encode(identity.PublicKey),
            ServerUrl: "wss://vesta.example/ws");

        string encoded = payload.ToBase64();
        PairingPayload decoded = PairingPayload.FromBase64(encoded);

        Assert.Equal(payload.GroupId, decoded.GroupId);
        Assert.Equal(payload.PublicKey, decoded.PublicKey);
        Assert.Equal(payload.ServerUrl, decoded.ServerUrl);
    }

    [Fact]
    public void IdentityLinkBuilder_RejectsInvalidGroupId()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        Assert.Throws<ArgumentException>(() => IdentityLinkBuilder.BuildAnnounce(identity, "BAD/ID"));
        Assert.Throws<ArgumentException>(() => IdentityLinkBuilder.BuildAnnounce(identity, ""));
    }

    [Fact]
    public void ChannelId_ValidateForAppWrite_RejectsProtocolPrefix()
    {
        Assert.Throws<ArgumentException>(() => ChannelId.ValidateForAppWrite("vesta/identity/abc"));
        // But ordinary validation still accepts it (server / SDK protocol writes use Validate).
        Assert.True(ChannelId.IsValid("vesta/identity/abc"));
    }
}
