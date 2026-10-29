using System.Text.Json;
using Vesta;

namespace VestaCore.Tests;

public class ChannelKeyEventsTests
{
    private const string Channel = "notes/secret";

    [Fact]
    public void KeyRequest_RoundTrips_AndIsSelfAuthenticating()
    {
        using VestaIdentity member = VestaIdentity.Generate();
        using ChannelKeyPair pair = ChannelKeyPair.FromIdentity(member);

        VestaEvent evt = ChannelKeyEvents.BuildKeyRequest(member, Channel, pair);
        ChannelKeyRequest? request = ChannelKeyEvents.TryParseKeyRequest(evt);

        Assert.Equal(ChannelKeyEvents.KeyRequestEventType, evt.EventType);
        Assert.NotNull(request);
        Assert.Equal(member.ClientId, request.ClientId);
        Assert.Equal(member.PublicKey, request.PublicKey);
        Assert.Equal(pair.PublicKey, request.X25519PublicKey);
    }

    [Fact]
    public void KeyRequest_RejectsForgedAuthor_TamperedKey_AndWrongType()
    {
        using VestaIdentity member = VestaIdentity.Generate();
        using VestaIdentity other = VestaIdentity.Generate();
        using ChannelKeyPair pair = ChannelKeyPair.FromIdentity(member);
        VestaEvent evt = ChannelKeyEvents.BuildKeyRequest(member, Channel, pair);

        Assert.Null(ChannelKeyEvents.TryParseKeyRequest(evt with { ClientId = other.ClientId }));
        Assert.Null(ChannelKeyEvents.TryParseKeyRequest(evt with { Signature = null }));
        Assert.Null(ChannelKeyEvents.TryParseKeyRequest(evt with { EventType = "app.note" }));

        JsonElement swapped = JsonDocument.Parse($$"""{"publicKey":"{{Base64Url.Encode(member.PublicKey)}}","x25519PublicKey":"{{Base64Url.Encode(new byte[32])}}"}""").RootElement;
        Assert.Null(ChannelKeyEvents.TryParseKeyRequest(evt with { Payload = swapped }));
    }

    [Fact]
    public void KeyGrant_RoundTrips_OnlyFromTrustedAdmins()
    {
        using VestaIdentity admin = VestaIdentity.Generate();
        using VestaIdentity impostor = VestaIdentity.Generate();
        SealedChannelKey sealedKey = new(1, Base64Url.Encode(new byte[32]), Base64Url.Encode(new byte[12]), Base64Url.Encode(new byte[48]));

        VestaEvent evt = ChannelKeyEvents.BuildKeyGrant(admin, Channel, "rcpt", [sealedKey]);
        ChannelKeyGrant? grant = ChannelKeyEvents.TryParseKeyGrant(evt, [admin.PublicKey]);

        Assert.NotNull(grant);
        Assert.Equal(admin.ClientId, grant.AdminClientId);
        Assert.Equal("rcpt", grant.RecipientClientId);
        Assert.Equal(sealedKey, Assert.Single(grant.Keys));

        Assert.Null(ChannelKeyEvents.TryParseKeyGrant(evt, [impostor.PublicKey]));
        Assert.Null(ChannelKeyEvents.TryParseKeyGrant(evt, []));
        VestaEvent forged = ChannelKeyEvents.BuildKeyGrant(impostor, Channel, "rcpt", [sealedKey]);
        Assert.Null(ChannelKeyEvents.TryParseKeyGrant(forged, [admin.PublicKey]));
    }

    [Fact]
    public void KeyGrant_RequiresAtLeastOneKey()
    {
        using VestaIdentity admin = VestaIdentity.Generate();
        Assert.Throws<ArgumentException>(() => ChannelKeyEvents.BuildKeyGrant(admin, Channel, "rcpt", []));
    }
}
