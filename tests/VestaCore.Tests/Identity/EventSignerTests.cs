using System.Text.Json;
using VestaCore.Events;
using VestaCore.Identity;

namespace VestaCore.Tests.Identity;

public class EventSignerTests
{
    private static VestaEvent CreateTestEvent(VestaIdentity identity)
    {
        JsonElement payload = JsonSerializer.Deserialize<JsonElement>("""{"title":"Buy milk","done":false}""");

        return new VestaEvent(
            Id: Guid.Parse("01961a3e-7c5d-7f8a-b1c2-d3e4f5a6b7c8"),
            ChannelId: "test/channel",
            Timestamp: new DateTimeOffset(2026, 5, 29, 12, 0, 0, TimeSpan.Zero),
            ClientId: identity.ClientId,
            EventType: "app.todo.item-added",
            Payload: payload,
            ParentId: null,
            Signature: null
        );
    }

    [Fact]
    public void SignEvent_ProducesSignedEvent()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaEvent unsigned = CreateTestEvent(identity);

        VestaEvent signed = EventSigner.SignEvent(unsigned, identity);

        Assert.NotNull(signed.Signature);
        Assert.NotEmpty(signed.Signature);
        // All other fields should be unchanged
        Assert.Equal(unsigned.Id, signed.Id);
        Assert.Equal(unsigned.ChannelId, signed.ChannelId);
        Assert.Equal(unsigned.Timestamp, signed.Timestamp);
        Assert.Equal(unsigned.ClientId, signed.ClientId);
        Assert.Equal(unsigned.EventType, signed.EventType);
        Assert.Equal(unsigned.ParentId, signed.ParentId);
    }

    [Fact]
    public void SignEvent_ThrowsIfClientIdMismatch()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaEvent vestaEvent = CreateTestEvent(identity) with { ClientId = "wrong-client-id-12345" };

        Assert.Throws<ArgumentException>(() => EventSigner.SignEvent(vestaEvent, identity));
    }

    [Fact]
    public void VerifyEvent_ReturnsTrueForValidSignature()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaEvent unsigned = CreateTestEvent(identity);
        VestaEvent signed = EventSigner.SignEvent(unsigned, identity);

        bool result = EventSigner.VerifyEvent(signed, identity.PublicKey);

        Assert.True(result);
    }

    [Fact]
    public void VerifyEvent_ReturnsFalseForTamperedPayload()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaEvent unsigned = CreateTestEvent(identity);
        VestaEvent signed = EventSigner.SignEvent(unsigned, identity);

        JsonElement tamperedPayload = JsonSerializer.Deserialize<JsonElement>("""{"title":"Tampered!","done":true}""");
        VestaEvent tampered = signed with { Payload = tamperedPayload };

        bool result = EventSigner.VerifyEvent(tampered, identity.PublicKey);

        Assert.False(result);
    }

    [Fact]
    public void VerifyEvent_ReturnsFalseForTamperedChannelId()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaEvent unsigned = CreateTestEvent(identity);
        VestaEvent signed = EventSigner.SignEvent(unsigned, identity);
        VestaEvent tampered = signed with { ChannelId = "other/channel" };

        bool result = EventSigner.VerifyEvent(tampered, identity.PublicKey);

        Assert.False(result);
    }

    [Fact]
    public void VerifyEvent_ReturnsFalseForTamperedEventType()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaEvent unsigned = CreateTestEvent(identity);
        VestaEvent signed = EventSigner.SignEvent(unsigned, identity);
        VestaEvent tampered = signed with { EventType = "app.todo.item-deleted" };

        bool result = EventSigner.VerifyEvent(tampered, identity.PublicKey);

        Assert.False(result);
    }

    [Fact]
    public void VerifyEvent_ReturnsFalseForWrongPublicKey()
    {
        using VestaIdentity identity1 = VestaIdentity.Generate();
        using VestaIdentity identity2 = VestaIdentity.Generate();
        VestaEvent unsigned = CreateTestEvent(identity1);
        VestaEvent signed = EventSigner.SignEvent(unsigned, identity1);

        bool result = EventSigner.VerifyEvent(signed, identity2.PublicKey);

        Assert.False(result);
    }

    [Fact]
    public void VerifyEvent_ReturnsFalseForNullSignature()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaEvent unsigned = CreateTestEvent(identity);

        bool result = EventSigner.VerifyEvent(unsigned, identity.PublicKey);

        Assert.False(result);
    }

    [Fact]
    public void VerifyClientId_ReturnsTrueForMatchingKey()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaEvent vestaEvent = CreateTestEvent(identity);

        bool result = EventSigner.VerifyClientId(vestaEvent, identity.PublicKey);

        Assert.True(result);
    }

    [Fact]
    public void VerifyClientId_ReturnsFalseForWrongKey()
    {
        using VestaIdentity identity1 = VestaIdentity.Generate();
        using VestaIdentity identity2 = VestaIdentity.Generate();
        VestaEvent vestaEvent = CreateTestEvent(identity1);

        bool result = EventSigner.VerifyClientId(vestaEvent, identity2.PublicKey);

        Assert.False(result);
    }

    [Fact]
    public void SignEvent_IsDeterministic()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaEvent unsigned = CreateTestEvent(identity);

        VestaEvent signed1 = EventSigner.SignEvent(unsigned, identity);
        VestaEvent signed2 = EventSigner.SignEvent(unsigned, identity);

        // Ed25519 is deterministic — same input + same key = same signature
        Assert.Equal(signed1.Signature, signed2.Signature);
    }

    [Fact]
    public void SignEvent_WithParentId_ProducesVerifiableSignature()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        Guid parentId = Guid.Parse("01961a3e-0000-7f8a-b1c2-d3e4f5a6b7c8");
        VestaEvent unsigned = CreateTestEvent(identity) with { ParentId = parentId };

        VestaEvent signed = EventSigner.SignEvent(unsigned, identity);

        Assert.True(EventSigner.VerifyEvent(signed, identity.PublicKey));
    }

    [Fact]
    public void SignEvent_TamperedParentId_FailsVerification()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaEvent unsigned = CreateTestEvent(identity);
        VestaEvent signed = EventSigner.SignEvent(unsigned, identity);

        Guid fakeParent = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        VestaEvent tampered = signed with { ParentId = fakeParent };

        Assert.False(EventSigner.VerifyEvent(tampered, identity.PublicKey));
    }

    [Fact]
    public void BuildSigningInput_IsCanonicalJson()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaEvent vestaEvent = CreateTestEvent(identity);

        byte[] signingInput = EventSigner.BuildSigningInput(vestaEvent);
        string json = System.Text.Encoding.UTF8.GetString(signingInput);

        // JCS ensures keys are sorted lexicographically
        // channelId < clientId < id < parentId < payload < timestamp < type
        int channelIdPos = json.IndexOf("\"channelId\"");
        int clientIdPos = json.IndexOf("\"clientId\"");
        int idPos = json.IndexOf("\"id\"");
        int parentIdPos = json.IndexOf("\"parentId\"");
        int payloadPos = json.IndexOf("\"payload\"");
        int timestampPos = json.IndexOf("\"timestamp\"");
        int typePos = json.IndexOf("\"type\"");

        Assert.True(channelIdPos < clientIdPos);
        Assert.True(clientIdPos < idPos);
        Assert.True(idPos < parentIdPos);
        Assert.True(parentIdPos < payloadPos);
        Assert.True(payloadPos < timestampPos);
        Assert.True(timestampPos < typePos);

        // Should not contain whitespace between tokens
        Assert.DoesNotContain(": ", json);
        Assert.DoesNotContain(", ", json);
    }

    [Fact]
    public void BuildSigningInput_IncludesNullParentId()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaEvent vestaEvent = CreateTestEvent(identity);

        byte[] signingInput = EventSigner.BuildSigningInput(vestaEvent);
        string json = System.Text.Encoding.UTF8.GetString(signingInput);

        // null values should be included per spec
        Assert.Contains("\"parentId\":null", json);
    }

    [Fact]
    public void SignEvent_SignatureIsBase64UrlEncoded()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaEvent unsigned = CreateTestEvent(identity);

        VestaEvent signed = EventSigner.SignEvent(unsigned, identity);

        // Should not contain standard base64 characters that are URL-unsafe
        Assert.DoesNotContain("+", signed.Signature);
        Assert.DoesNotContain("/", signed.Signature);
        Assert.DoesNotContain("=", signed.Signature);
    }
}
