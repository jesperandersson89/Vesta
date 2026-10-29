using System.Text.Json;
using Vesta;

namespace VestaCore.Tests;

public class EncryptedPayloadTests
{
    [Theory]
    [InlineData("""{"enc":1,"kid":1,"n":"AAAAAAAAAAAAAAAA","ct":"c2VjcmV0"}""")]
    [InlineData("""{"ct":"c2VjcmV0","n":"AAAAAAAAAAAAAAAA","kid":0,"enc":1}""")]
    [InlineData("""{"enc":1,"kid":2147483647,"n":"_-_-_-_-_-_-_-_-","ct":"AQ"}""")]
    public void IsEnvelope_WellFormed_ReturnsTrue(string json)
    {
        Assert.True(EncryptedPayload.IsEnvelope(Parse(json)));
    }

    [Theory]
    [InlineData("""{"text":"hello"}""", "plaintext object")]
    [InlineData("""{"enc":1,"kid":1,"n":"AAAAAAAAAAAAAAAA","ct":"c2VjcmV0","extra":true}""", "extra member")]
    [InlineData("""{"enc":1,"kid":1,"n":"AAAAAAAAAAAAAAAA"}""", "missing ct")]
    [InlineData("""{"enc":2,"kid":1,"n":"AAAAAAAAAAAAAAAA","ct":"c2VjcmV0"}""", "wrong version")]
    [InlineData("""{"enc":"1","kid":1,"n":"AAAAAAAAAAAAAAAA","ct":"c2VjcmV0"}""", "version as string")]
    [InlineData("""{"enc":1,"kid":-1,"n":"AAAAAAAAAAAAAAAA","ct":"c2VjcmV0"}""", "negative kid")]
    [InlineData("""{"enc":1,"kid":1.5,"n":"AAAAAAAAAAAAAAAA","ct":"c2VjcmV0"}""", "fractional kid")]
    [InlineData("""{"enc":1,"kid":1,"n":"AAAAAAAAAAAAAAA","ct":"c2VjcmV0"}""", "nonce not 12 bytes")]
    [InlineData("""{"enc":1,"kid":1,"n":"AAAAAAAAAAAAAAAA","ct":""}""", "empty ciphertext")]
    [InlineData("""{"enc":1,"kid":1,"n":"AAAAAAAAAAAAAAAA","ct":"not base64url!"}""", "invalid base64url")]
    [InlineData("""{"enc":1,"kid":1,"n":"AAAAAAAAAAAAAAAA","ct":"c2VjcmV0="}""", "padded base64")]
    [InlineData("""[1,2,3]""", "array")]
    [InlineData("""null""", "null")]
    public void IsEnvelope_Malformed_ReturnsFalse(string json, string because)
    {
        Assert.False(EncryptedPayload.IsEnvelope(Parse(json)), because);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
