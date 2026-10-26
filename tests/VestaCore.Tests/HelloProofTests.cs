using Vesta;

namespace VestaCore.Tests;

public class HelloProofTests
{
    [Fact]
    public void SignAndVerify_RoundTrip_Succeeds()
    {
        using VestaIdentity identity = VestaIdentity.Generate();

        string signature = HelloProof.Sign(identity, "nonce-1");

        Assert.True(HelloProof.Verify(identity.PublicKey, "nonce-1", identity.ClientId, signature));
    }

    [Fact]
    public void Verify_WithDifferentNonceClientIdOrKey_Fails()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        using VestaIdentity other = VestaIdentity.Generate();
        string signature = HelloProof.Sign(identity, "nonce-1");

        Assert.False(HelloProof.Verify(identity.PublicKey, "nonce-2", identity.ClientId, signature));
        Assert.False(HelloProof.Verify(identity.PublicKey, "nonce-1", other.ClientId, signature));
        Assert.False(HelloProof.Verify(other.PublicKey, "nonce-1", identity.ClientId, signature));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64url!!")]
    [InlineData("AAAA")]
    public void Verify_WithMalformedSignature_ReturnsFalse(string signature)
    {
        using VestaIdentity identity = VestaIdentity.Generate();

        Assert.False(HelloProof.Verify(identity.PublicKey, "nonce-1", identity.ClientId, signature));
    }

    [Fact]
    public void BuildMessage_IsDomainSeparatedNewlineJoinedUtf8()
    {
        byte[] message = HelloProof.BuildMessage("abc", "client-1");

        Assert.Equal("vesta-hello-v1\nabc\nclient-1", System.Text.Encoding.UTF8.GetString(message));
    }

    [Fact]
    public void Sign_WithFixedSeed_MatchesTheCrossLanguageVector()
    {
        using VestaIdentity identity = VestaIdentity.FromPrivateKey(new byte[32]);

        Assert.Equal("E545QOZLVJFyIIjZoNdBYo", identity.ClientId);
        Assert.Equal(
            "9hGCvE4icwU-17knAeGGHxw0xvyqpm38vbgrC0z8ZREQWCtRk9UoPGNP_o0bp0-ABGhIXJAY5rrDsblaYih_DQ",
            HelloProof.Sign(identity, "bm9uY2Utdm9uY2Utdm9uY2U"));
    }
}
