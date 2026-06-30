using VestaCore.Identity;
using VestaCore.Relay;
using VestaCore.Utilities;

namespace VestaCore.Tests.Relay;

public class DescriptorSignerTests
{
    private static ServerDescriptor CreateDescriptor()
    {
        return new ServerDescriptor
        {
            RelayPublicKey = string.Empty,
            Urls = ["wss://relay-a.example/ws", "wss://relay-b.example/ws"],
            Apps =
            [
                new DiscoverableApp("chess", "owner-client-id-aaaaa"),
                new DiscoverableApp("chat", "owner-client-id-bbbbb")
            ],
            IssuedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TtlSeconds = 300
        };
    }

    [Fact]
    public void Sign_PopulatesRelayKeyAndSignature()
    {
        using VestaIdentity relay = VestaIdentity.Generate();

        ServerDescriptor signed = DescriptorSigner.Sign(CreateDescriptor(), relay);

        Assert.NotNull(signed.Signature);
        Assert.NotEmpty(signed.Signature);
        Assert.Equal(Base64Url.Encode(relay.PublicKey), signed.RelayPublicKey);
    }

    [Fact]
    public void Sign_ThrowsIfRelayKeyMismatch()
    {
        using VestaIdentity relay = VestaIdentity.Generate();
        using VestaIdentity other = VestaIdentity.Generate();
        ServerDescriptor descriptor = CreateDescriptor() with
        {
            RelayPublicKey = Base64Url.Encode(other.PublicKey)
        };

        Assert.Throws<ArgumentException>(() => DescriptorSigner.Sign(descriptor, relay));
    }

    [Fact]
    public void Verify_ReturnsTrueForValidSelfSignedDescriptor()
    {
        using VestaIdentity relay = VestaIdentity.Generate();
        ServerDescriptor signed = DescriptorSigner.Sign(CreateDescriptor(), relay);

        Assert.True(DescriptorSigner.Verify(signed));
    }

    [Fact]
    public void Verify_ReturnsFalseForUnsignedDescriptor()
    {
        using VestaIdentity relay = VestaIdentity.Generate();
        ServerDescriptor withKey = CreateDescriptor() with { RelayPublicKey = Base64Url.Encode(relay.PublicKey) };

        Assert.False(DescriptorSigner.Verify(withKey));
    }

    [Fact]
    public void Verify_ReturnsFalseForTamperedUrls()
    {
        using VestaIdentity relay = VestaIdentity.Generate();
        ServerDescriptor signed = DescriptorSigner.Sign(CreateDescriptor(), relay);

        ServerDescriptor tampered = signed with { Urls = ["wss://evil.example/ws"] };

        Assert.False(DescriptorSigner.Verify(tampered));
    }

    [Fact]
    public void Verify_ReturnsFalseForTamperedApps()
    {
        using VestaIdentity relay = VestaIdentity.Generate();
        ServerDescriptor signed = DescriptorSigner.Sign(CreateDescriptor(), relay);

        ServerDescriptor tampered = signed with
        {
            Apps = [new DiscoverableApp("chess", "attacker-owner-id")]
        };

        Assert.False(DescriptorSigner.Verify(tampered));
    }

    [Fact]
    public void Verify_ReturnsFalseWhenSignatureSwappedToDifferentKey()
    {
        // A descriptor signed by one relay, then re-labelled with another relay's public key,
        // must not verify — the signature no longer matches the declared key.
        using VestaIdentity relay = VestaIdentity.Generate();
        using VestaIdentity attacker = VestaIdentity.Generate();
        ServerDescriptor signed = DescriptorSigner.Sign(CreateDescriptor(), relay);

        ServerDescriptor forged = signed with { RelayPublicKey = Base64Url.Encode(attacker.PublicKey) };

        Assert.False(DescriptorSigner.Verify(forged));
    }

    [Fact]
    public void IsExpired_TrueAfterTtl()
    {
        ServerDescriptor descriptor = CreateDescriptor();
        DateTimeOffset within = descriptor.IssuedAt.AddSeconds(descriptor.TtlSeconds - 1);
        DateTimeOffset after = descriptor.IssuedAt.AddSeconds(descriptor.TtlSeconds + 1);

        Assert.False(descriptor.IsExpired(within));
        Assert.True(descriptor.IsExpired(after));
    }
}
