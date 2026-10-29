using System.Security.Cryptography;
using System.Text.Json;
using Vesta;

namespace VestaCore.Tests;

/// <summary>
/// Shared #33 phase-B vectors (generated with Python <c>cryptography</c>; pinned identically in
/// <c>channel-crypto.test.mjs</c> and <c>test_channel_crypto.py</c>). Any SDK must reproduce every value.
/// </summary>
public class ChannelCryptoTests
{
    private static readonly byte[] SeedA = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] SeedB = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
    private const string X25519PrivA = "bWmf93nEz6Ln3iFyUFjrQzEo5HTi4hcLCL4rNXcQgSY";
    private const string X25519PubA = "FOghQ_QoWARIegjdmUQMvxDlo52xNSuxywtywgqy-BU";
    private const string X25519PrivB = "0lw_0gknxoNrVg9GMMaRk4nNGGtsc0at7L-sn1cUXI4";
    private const string X25519PubB = "N1z9KAjpCNOuEulV1jAnYyChQxyuXMaA5ILsIluV7ms";
    private const string ClientIdB = "JPbtasv-EAnAMNfKVnwzyk";

    private static readonly ChannelKey Key = new(1, Enumerable.Repeat((byte)0x42, 32).ToArray());
    private const string ChannelId = "notes/secret";
    private static readonly Guid EventId = Guid.Parse("0190f7e2-0000-7000-8000-000000000001");
    private const string EventType = "app.note";
    private static readonly byte[] Nonce = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
    private const string ExpectedCt = "fvz__4sd3inpYzxn8-d5U1fxogWCNBPNbm8s9i1AGezwveYgVlM";

    private static readonly byte[] EphPriv = Enumerable.Repeat((byte)0x07, 32).ToArray();
    private const string EphPub = "E75P6uryBMf9M1j8nAByGIHRdCeBKCJ-xnTzf3_pe20";
    private static readonly byte[] SealNonce = Enumerable.Range(16, 12).Select(i => (byte)i).ToArray();
    private const string SealedCt = "9unGHrePRVJRMfa0kInth8NBHJHdikR2Bs9so3BpuVmbUrQja7pEObBBpmmLyB96";

    [Fact]
    public void FromSeed_DerivesTheSharedX25519Vectors()
    {
        using ChannelKeyPair a = ChannelKeyPair.FromSeed(SeedA);
        using ChannelKeyPair b = ChannelKeyPair.FromSeed(SeedB);

        Assert.Equal(X25519PrivA, Base64Url.Encode(a.ExportPrivateKey()));
        Assert.Equal(X25519PubA, Base64Url.Encode(a.PublicKey));
        Assert.Equal(X25519PrivB, Base64Url.Encode(b.ExportPrivateKey()));
        Assert.Equal(X25519PubB, Base64Url.Encode(b.PublicKey));
    }

    [Fact]
    public void FromIdentity_MatchesFromSeed_AndClientIdVector()
    {
        using VestaIdentity identity = VestaIdentity.FromPrivateKey(SeedB);
        using ChannelKeyPair pair = ChannelKeyPair.FromIdentity(identity);

        Assert.Equal(ClientIdB, identity.ClientId);
        Assert.Equal(X25519PubB, Base64Url.Encode(pair.PublicKey));
    }

    [Fact]
    public void EncryptPayload_WithFixedNonce_MatchesVector_AndCanonicalisesTheJson()
    {
        // Keys deliberately out of canonical order: the plaintext is the JCS form {"n":1,"text":"hello"}.
        JsonElement payload = JsonDocument.Parse("""{"text":"hello","n":1}""").RootElement;

        JsonElement envelope = ChannelCrypto.EncryptPayload(Key, ChannelId, EventId, EventType, payload, Nonce);

        Assert.True(EncryptedPayload.IsEnvelope(envelope));
        Assert.Equal(1, envelope.GetProperty("enc").GetInt32());
        Assert.Equal(1, envelope.GetProperty("kid").GetInt32());
        Assert.Equal("AAECAwQFBgcICQoL", envelope.GetProperty("n").GetString());
        Assert.Equal(ExpectedCt, envelope.GetProperty("ct").GetString());
    }

    [Fact]
    public void DecryptPayload_RoundTrips_AndRejectsAnyAadChange()
    {
        JsonElement payload = JsonDocument.Parse("""{"text":"hello","n":1}""").RootElement;
        JsonElement envelope = ChannelCrypto.EncryptPayload(Key, ChannelId, EventId, EventType, payload, Nonce);

        JsonElement decrypted = ChannelCrypto.DecryptPayload(Key, ChannelId, EventId, EventType, envelope);
        Assert.Equal("""{"n":1,"text":"hello"}""", decrypted.GetRawText());

        Assert.ThrowsAny<CryptographicException>(() => ChannelCrypto.DecryptPayload(Key, "notes/other", EventId, EventType, envelope));
        Assert.ThrowsAny<CryptographicException>(() => ChannelCrypto.DecryptPayload(Key, ChannelId, Guid.NewGuid(), EventType, envelope));
        Assert.ThrowsAny<CryptographicException>(() => ChannelCrypto.DecryptPayload(Key, ChannelId, EventId, "app.other", envelope));
        Assert.ThrowsAny<CryptographicException>(() => ChannelCrypto.DecryptPayload(Key with { Key = new byte[32] }, ChannelId, EventId, EventType, envelope));
        Assert.ThrowsAny<CryptographicException>(() => ChannelCrypto.DecryptPayload(Key with { Epoch = 2 }, ChannelId, EventId, EventType, envelope));
    }

    [Fact]
    public void EncryptPayload_RandomNonce_ProducesDistinctEnvelopesThatBothDecrypt()
    {
        JsonElement payload = JsonDocument.Parse("""{"x":true}""").RootElement;

        JsonElement first = ChannelCrypto.EncryptPayload(Key, ChannelId, EventId, EventType, payload);
        JsonElement second = ChannelCrypto.EncryptPayload(Key, ChannelId, EventId, EventType, payload);

        Assert.NotEqual(first.GetProperty("n").GetString(), second.GetProperty("n").GetString());
        Assert.Equal("""{"x":true}""", ChannelCrypto.DecryptPayload(Key, ChannelId, EventId, EventType, first).GetRawText());
        Assert.Equal("""{"x":true}""", ChannelCrypto.DecryptPayload(Key, ChannelId, EventId, EventType, second).GetRawText());
    }

    [Fact]
    public void ReadEpoch_ReturnsKid_AndRejectsNonEnvelopes()
    {
        JsonElement envelope = ChannelCrypto.EncryptPayload(Key with { Epoch = 7 }, ChannelId, EventId, EventType, JsonDocument.Parse("""{"a":1}""").RootElement, Nonce);
        Assert.Equal(7, ChannelCrypto.ReadEpoch(envelope));
        Assert.Throws<FormatException>(() => ChannelCrypto.ReadEpoch(JsonDocument.Parse("""{"text":"hi"}""").RootElement));
    }

    [Fact]
    public void EncryptPayload_ScalarAndArrayPayloads_RoundTrip()
    {
        foreach (string json in new[] { "1", "\"text\"", "true", "null", "[3,1,2]" })
        {
            JsonElement envelope = ChannelCrypto.EncryptPayload(Key, ChannelId, EventId, EventType, JsonDocument.Parse(json).RootElement);
            Assert.Equal(json, ChannelCrypto.DecryptPayload(Key, ChannelId, EventId, EventType, envelope).GetRawText());
        }
    }

    [Fact]
    public void SealKey_WithFixedEphemeralAndNonce_MatchesVector()
    {
        using ChannelKeyPair ephemeral = ChannelKeyPair.FromPrivateKey(EphPriv);
        byte[] recipientPub = Base64Url.Decode(X25519PubB);

        SealedChannelKey sealedKey = ChannelCrypto.SealKey(Key, ChannelId, ClientIdB, recipientPub, ephemeral, SealNonce);

        Assert.Equal(1, sealedKey.Epoch);
        Assert.Equal(EphPub, sealedKey.Epk);
        Assert.Equal("EBESExQVFhcYGRob", sealedKey.N);
        Assert.Equal(SealedCt, sealedKey.Ct);
    }

    [Fact]
    public void OpenKey_RecoversTheChannelKey_OnlyForTheRecipient()
    {
        SealedChannelKey sealedKey = new(1, EphPub, "EBESExQVFhcYGRob", SealedCt);
        using ChannelKeyPair b = ChannelKeyPair.FromSeed(SeedB);
        using ChannelKeyPair a = ChannelKeyPair.FromSeed(SeedA);

        ChannelKey opened = ChannelCrypto.OpenKey(sealedKey, ChannelId, ClientIdB, b);
        Assert.Equal(Key.Key, opened.Key);
        Assert.Equal(1, opened.Epoch);

        Assert.ThrowsAny<CryptographicException>(() => ChannelCrypto.OpenKey(sealedKey, ChannelId, ClientIdB, a));
        Assert.ThrowsAny<CryptographicException>(() => ChannelCrypto.OpenKey(sealedKey, "notes/other", ClientIdB, b));
        Assert.ThrowsAny<CryptographicException>(() => ChannelCrypto.OpenKey(sealedKey, ChannelId, "someone-else", b));
        Assert.ThrowsAny<CryptographicException>(() => ChannelCrypto.OpenKey(sealedKey with { Epoch = 2 }, ChannelId, ClientIdB, b));
    }

    [Fact]
    public void SealKey_RandomEphemeral_RoundTrips_AndSurvivesJson()
    {
        using ChannelKeyPair recipient = ChannelKeyPair.Generate();
        ChannelKey key = ChannelKey.Generate(3);

        SealedChannelKey sealedKey = ChannelCrypto.SealKey(key, ChannelId, "rcpt", recipient.PublicKey);
        SealedChannelKey parsed = SealedChannelKey.FromJson(JsonSerializer.SerializeToElement(sealedKey.ToJson()));

        ChannelKey opened = ChannelCrypto.OpenKey(parsed, ChannelId, "rcpt", recipient);
        Assert.Equal(3, opened.Epoch);
        Assert.Equal(key.Key, opened.Key);
    }
}
