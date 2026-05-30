using System.Text.Json;
using VestaCore.Events;
using VestaCore.Identity;

namespace VestaCore.Tests.Identity;

public class VestaIdentityTests
{
    [Fact]
    public void Generate_CreatesValidIdentity()
    {
        using VestaIdentity identity = VestaIdentity.Generate();

        Assert.NotNull(identity.PublicKey);
        Assert.Equal(32, identity.PublicKey.Length);
        Assert.NotNull(identity.ClientId);
        Assert.Equal(22, identity.ClientId.Length);
    }

    [Fact]
    public void Generate_ProducesDifferentIdentitiesEachTime()
    {
        using VestaIdentity identity1 = VestaIdentity.Generate();
        using VestaIdentity identity2 = VestaIdentity.Generate();

        Assert.NotEqual(identity1.ClientId, identity2.ClientId);
        Assert.NotEqual(identity1.PublicKey, identity2.PublicKey);
    }

    [Fact]
    public void FromPrivateKey_RestoresIdentity()
    {
        using VestaIdentity original = VestaIdentity.Generate();
        byte[] privateKey = original.ExportPrivateKey();

        using VestaIdentity restored = VestaIdentity.FromPrivateKey(privateKey);

        Assert.Equal(original.ClientId, restored.ClientId);
        Assert.Equal(original.PublicKey, restored.PublicKey);
    }

    [Fact]
    public void ExportPrivateKey_Returns32Bytes()
    {
        using VestaIdentity identity = VestaIdentity.Generate();

        byte[] privateKey = identity.ExportPrivateKey();

        Assert.Equal(32, privateKey.Length);
    }

    [Fact]
    public void Sign_ProducesValid64ByteSignature()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        byte[] data = "hello world"u8.ToArray();

        byte[] signature = identity.Sign(data);

        Assert.Equal(64, signature.Length);
    }

    [Fact]
    public void Verify_ReturnsTrueForValidSignature()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        byte[] data = "test message"u8.ToArray();
        byte[] signature = identity.Sign(data);

        bool result = identity.Verify(data, signature);

        Assert.True(result);
    }

    [Fact]
    public void Verify_ReturnsFalseForTamperedData()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        byte[] data = "original"u8.ToArray();
        byte[] signature = identity.Sign(data);
        byte[] tampered = "modified"u8.ToArray();

        bool result = identity.Verify(tampered, signature);

        Assert.False(result);
    }

    [Fact]
    public void Verify_ReturnsFalseForTamperedSignature()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        byte[] data = "test"u8.ToArray();
        byte[] signature = identity.Sign(data);
        signature[0] ^= 0xFF; // flip a byte

        bool result = identity.Verify(data, signature);

        Assert.False(result);
    }

    [Fact]
    public void VerifyWithPublicKey_WorksWithoutPrivateKey()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        byte[] data = "verify with public key"u8.ToArray();
        byte[] signature = identity.Sign(data);

        bool result = VestaIdentity.VerifyWithPublicKey(identity.PublicKey, data, signature);

        Assert.True(result);
    }

    [Fact]
    public void VerifyWithPublicKey_ReturnsFalseForWrongKey()
    {
        using VestaIdentity identity1 = VestaIdentity.Generate();
        using VestaIdentity identity2 = VestaIdentity.Generate();
        byte[] data = "signed by identity1"u8.ToArray();
        byte[] signature = identity1.Sign(data);

        bool result = VestaIdentity.VerifyWithPublicKey(identity2.PublicKey, data, signature);

        Assert.False(result);
    }

    [Fact]
    public void DeriveClientId_IsDeterministic()
    {
        using VestaIdentity identity = VestaIdentity.Generate();

        string clientId1 = VestaIdentity.DeriveClientId(identity.PublicKey);
        string clientId2 = VestaIdentity.DeriveClientId(identity.PublicKey);

        Assert.Equal(clientId1, clientId2);
        Assert.Equal(identity.ClientId, clientId1);
    }

    [Fact]
    public void ClientId_IsUrlSafe()
    {
        using VestaIdentity identity = VestaIdentity.Generate();

        // Should not contain +, /, or =
        Assert.DoesNotContain("+", identity.ClientId);
        Assert.DoesNotContain("/", identity.ClientId);
        Assert.DoesNotContain("=", identity.ClientId);
    }
}
