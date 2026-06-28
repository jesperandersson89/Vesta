using VestaCore.Identity;
using VestaCore.Relay;

namespace VestaCore.Tests.Relay;

public class ManifestSignerTests
{
    private static RelayManifest CreateManifest(int version = 1)
    {
        return new RelayManifest
        {
            AppId = "chess",
            Version = version,
            IssuedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Relays =
            [
                new RelayEndpoint("wss://relay-a.example/ws", Priority: 0, Label: "primary"),
                new RelayEndpoint("wss://relay-b.example/ws", Priority: 1, Label: "backup")
            ],
            EscapeFallbacks =
            [
                new EscapeFallback("wss://escape.example/ws", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero))
            ],
            OwnerPublicKey = string.Empty
        };
    }

    [Fact]
    public void Sign_PopulatesOwnerKeyAndSignature()
    {
        using VestaIdentity owner = VestaIdentity.Generate();

        RelayManifest signed = ManifestSigner.Sign(CreateManifest(), owner);

        Assert.NotNull(signed.Signature);
        Assert.NotEmpty(signed.Signature);
        Assert.Equal(VestaCore.Utilities.Base64Url.Encode(owner.PublicKey), signed.OwnerPublicKey);
    }

    [Fact]
    public void Sign_ThrowsIfOwnerKeyMismatch()
    {
        using VestaIdentity owner = VestaIdentity.Generate();
        using VestaIdentity other = VestaIdentity.Generate();
        RelayManifest manifest = CreateManifest() with
        {
            OwnerPublicKey = VestaCore.Utilities.Base64Url.Encode(other.PublicKey)
        };

        Assert.Throws<ArgumentException>(() => ManifestSigner.Sign(manifest, owner));
    }

    [Fact]
    public void Verify_ReturnsTrueForValidSignature()
    {
        using VestaIdentity owner = VestaIdentity.Generate();
        RelayManifest signed = ManifestSigner.Sign(CreateManifest(), owner);

        Assert.True(ManifestSigner.Verify(signed, owner.PublicKey));
    }

    [Fact]
    public void Verify_ReturnsFalseForUnsignedManifest()
    {
        using VestaIdentity owner = VestaIdentity.Generate();

        Assert.False(ManifestSigner.Verify(CreateManifest(), owner.PublicKey));
    }

    [Fact]
    public void Verify_ReturnsFalseForWrongKey()
    {
        using VestaIdentity owner = VestaIdentity.Generate();
        using VestaIdentity attacker = VestaIdentity.Generate();
        RelayManifest signed = ManifestSigner.Sign(CreateManifest(), owner);

        Assert.False(ManifestSigner.Verify(signed, attacker.PublicKey));
    }

    [Fact]
    public void Verify_ReturnsFalseForTamperedRelays()
    {
        using VestaIdentity owner = VestaIdentity.Generate();
        RelayManifest signed = ManifestSigner.Sign(CreateManifest(), owner);

        RelayManifest tampered = signed with
        {
            Relays = [new RelayEndpoint("wss://evil.example/ws", Priority: 0)]
        };

        Assert.False(ManifestSigner.Verify(tampered, owner.PublicKey));
    }

    [Fact]
    public void Verify_ReturnsFalseForTamperedVersion()
    {
        using VestaIdentity owner = VestaIdentity.Generate();
        RelayManifest signed = ManifestSigner.Sign(CreateManifest(version: 1), owner);

        RelayManifest tampered = signed with { Version = 99 };

        Assert.False(ManifestSigner.Verify(tampered, owner.PublicKey));
    }

    [Fact]
    public void ChannelFor_UsesReservedRelaysChannel()
    {
        Assert.Equal("chess/vesta/relays", RelayManifest.ChannelFor("chess"));
    }
}
