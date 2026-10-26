using System.Text.Json;
using Vesta;

namespace VestaCore.Tests;

public class RecoveryPhraseTests
{
    private static string Repeat(string word, int count) => string.Join(' ', Enumerable.Repeat(word, count));

    [Fact]
    public void FromSeed_AllZeroSeed_MatchesTheBip39Vector()
    {
        Assert.Equal(Repeat("abandon", 23) + " art", RecoveryPhrase.FromSeed(new byte[32]));
    }

    [Fact]
    public void FromSeed_AllOnesSeed_MatchesTheBip39Vector()
    {
        byte[] seed = Enumerable.Repeat((byte)0xFF, 32).ToArray();

        Assert.Equal(Repeat("zoo", 23) + " vote", RecoveryPhrase.FromSeed(seed));
    }

    [Fact]
    public void ToSeed_RoundTripsRandomSeeds()
    {
        for (int i = 0; i < 20; i++)
        {
            byte[] seed = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

            Assert.Equal(seed, RecoveryPhrase.ToSeed(RecoveryPhrase.FromSeed(seed)));
        }
    }

    [Fact]
    public void ToSeed_IgnoresCaseAndExtraWhitespace()
    {
        string phrase = "  " + (Repeat("abandon", 23) + " art").ToUpperInvariant().Replace(" ", "\n  ") + "\t";

        Assert.Equal(new byte[32], RecoveryPhrase.ToSeed(phrase));
    }

    [Fact]
    public void ToSeed_RejectsWrongWordCountUnknownWordAndBadChecksum()
    {
        Assert.Throws<RecoveryPhraseException>(() => RecoveryPhrase.ToSeed(Repeat("abandon", 12)));
        Assert.Throws<RecoveryPhraseException>(() => RecoveryPhrase.ToSeed(Repeat("abandon", 23) + " notaword"));
        Assert.Throws<RecoveryPhraseException>(() => RecoveryPhrase.ToSeed(Repeat("abandon", 24)));
    }

    [Fact]
    public void Identity_RoundTripsThroughItsRecoveryPhrase()
    {
        using VestaIdentity identity = VestaIdentity.Generate();

        using VestaIdentity restored = VestaIdentity.FromRecoveryPhrase(identity.ToRecoveryPhrase());

        Assert.Equal(identity.ClientId, restored.ClientId);
        Assert.Equal(24, identity.ToRecoveryPhrase().Split(' ').Length);
    }
}

public class IdentityBackupTests
{
    private static readonly IdentityBackupOptions Fast = new(MemoryKiB: 256, Iterations: 1);

    [Fact]
    public void ExportEncrypted_ThenImport_RestoresTheSameIdentity()
    {
        using VestaIdentity identity = VestaIdentity.Generate();

        string blob = identity.ExportEncrypted("correct horse battery staple", Fast);
        using VestaIdentity restored = VestaIdentity.ImportEncrypted(blob, "correct horse battery staple");

        Assert.Equal(identity.ClientId, restored.ClientId);
        Assert.Equal(identity.ExportPrivateKey(), restored.ExportPrivateKey());
    }

    [Fact]
    public void Blob_IsVersionedJson_WithoutThePlaintextSeed()
    {
        using VestaIdentity identity = VestaIdentity.Generate();

        string blob = identity.ExportEncrypted("pw", Fast);

        using JsonDocument doc = JsonDocument.Parse(blob);
        Assert.Equal("vesta-identity-backup", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(identity.ClientId, doc.RootElement.GetProperty("clientId").GetString());
        Assert.DoesNotContain(Base64Url.Encode(identity.ExportPrivateKey()), blob);
    }

    [Fact]
    public void Export_UsesAFreshSaltAndNonceEachTime()
    {
        using VestaIdentity identity = VestaIdentity.Generate();

        Assert.NotEqual(identity.ExportEncrypted("pw", Fast), identity.ExportEncrypted("pw", Fast));
    }

    [Fact]
    public void Import_WithWrongPassword_Fails()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        string blob = identity.ExportEncrypted("right", Fast);

        Assert.Throws<IdentityBackupException>(() => VestaIdentity.ImportEncrypted(blob, "wrong"));
    }

    [Fact]
    public void Import_NormalisesThePasswordSoUnicodeFormsAgree()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        string blob = identity.ExportEncrypted("caf\u00e9", Fast);

        using VestaIdentity restored = VestaIdentity.ImportEncrypted(blob, "cafe\u0301");

        Assert.Equal(identity.ClientId, restored.ClientId);
    }

    [Fact]
    public void Import_WithTamperedClientIdOrCiphertext_Fails()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        using VestaIdentity other = VestaIdentity.Generate();
        string blob = identity.ExportEncrypted("pw", Fast);

        string swappedId = blob.Replace(identity.ClientId, other.ClientId);
        Assert.Throws<IdentityBackupException>(() => VestaIdentity.ImportEncrypted(swappedId, "pw"));

        using JsonDocument doc = JsonDocument.Parse(blob);
        string ciphertext = doc.RootElement.GetProperty("ciphertext").GetString()!;
        string flipped = blob.Replace(ciphertext, (ciphertext[0] == 'A' ? 'B' : 'A') + ciphertext[1..]);
        Assert.Throws<IdentityBackupException>(() => VestaIdentity.ImportEncrypted(flipped, "pw"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"format":"something-else","version":1}""")]
    public void Import_WithMalformedBlob_FailsCleanly(string blob)
    {
        Assert.Throws<IdentityBackupException>(() => VestaIdentity.ImportEncrypted(blob, "pw"));
    }

    [Fact]
    public void Import_OpensABlobMadeByThePythonSdk()
    {
        // Cross-language vector: seed 32 x 0x00, Argon2id 256 KiB / 1 pass, produced by vesta_client.
        const string blob = """{"format":"vesta-identity-backup","version":1,"clientId":"E545QOZLVJFyIIjZoNdBYo","kdf":{"name":"argon2id","memoryKiB":256,"iterations":1,"parallelism":1,"salt":"fLzab7CqHEJ0INUixMow7Q"},"cipher":{"name":"aes-256-gcm","nonce":"wy1fc8r883p6G1jK"},"ciphertext":"ZodfWVd8VFyzpk827tteUJIGiultKBZBoPzSYyD0un4MRmLQSiWSxHQBfrIGtaIa"}""";

        using VestaIdentity restored = VestaIdentity.ImportEncrypted(blob, "correct horse battery staple");

        Assert.Equal("E545QOZLVJFyIIjZoNdBYo", restored.ClientId);
        Assert.Equal(new byte[32], restored.ExportPrivateKey());
    }

    [Fact]
    public void Export_EncryptsWithAnAes256GcmKeyDerivedByArgon2id()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        string blob = identity.ExportEncrypted("pw", Fast);

        using JsonDocument doc = JsonDocument.Parse(blob);
        Assert.Equal("argon2id", doc.RootElement.GetProperty("kdf").GetProperty("name").GetString());
        Assert.Equal("aes-256-gcm", doc.RootElement.GetProperty("cipher").GetProperty("name").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("kdf").GetProperty("parallelism").GetInt32());
    }

    [Fact]
    public void Export_WithTheDefaultCost_RoundTrips()
    {
        using VestaIdentity identity = VestaIdentity.Generate();

        using VestaIdentity restored = VestaIdentity.ImportEncrypted(identity.ExportEncrypted("pw"), "pw");

        Assert.Equal(identity.ClientId, restored.ClientId);
    }

    [Fact]
    public void Import_RefusesAbusiveKdfSettingsBeforeRunningThem()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        string blob = identity.ExportEncrypted("pw", Fast).Replace("\"memoryKiB\":256", "\"memoryKiB\":4000000");

        Assert.Throws<IdentityBackupException>(() => VestaIdentity.ImportEncrypted(blob, "pw"));
    }

    [Fact]
    public void Import_WithAnUnsupportedVersion_Fails()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        string blob = identity.ExportEncrypted("pw", Fast).Replace("\"version\":1", "\"version\":2");

        Assert.Throws<IdentityBackupException>(() => VestaIdentity.ImportEncrypted(blob, "pw"));
    }

    [Fact]
    public void Export_RejectsCostsOutsideTheAllowedRange()
    {
        using VestaIdentity identity = VestaIdentity.Generate();

        Assert.Throws<IdentityBackupException>(() => identity.ExportEncrypted("pw", new IdentityBackupOptions(MemoryKiB: 1, Iterations: 1)));
        Assert.Throws<IdentityBackupException>(() => identity.ExportEncrypted("pw", new IdentityBackupOptions(MemoryKiB: 256, Iterations: 0)));
    }
}

public class RelayBackupTests
{
    private static readonly IdentityBackupOptions Fast = new(MemoryKiB: 256, Iterations: 1);

    [Fact]
    public void Derive_IsDeterministic_AndChannelDependsOnEveryInput()
    {
        string channel = RelayBackup.Derive("game", "alice", "pw", Fast).ChannelId;

        Assert.Equal(channel, RelayBackup.Derive("game", "alice", "pw", Fast).ChannelId);
        Assert.Equal(channel, RelayBackup.Derive("game", "  Alice ", "pw", Fast).ChannelId);
        Assert.NotEqual(channel, RelayBackup.Derive("other", "alice", "pw", Fast).ChannelId);
        Assert.NotEqual(channel, RelayBackup.Derive("game", "bob", "pw", Fast).ChannelId);
        Assert.NotEqual(channel, RelayBackup.Derive("game", "alice", "other", Fast).ChannelId);
        Assert.Matches("^vesta/identity-backup/[0-9a-f]{32}$", channel);
        Assert.True(ChannelId.IsValid(channel));
    }

    [Fact]
    public void AuthSecret_MatchesThePythonSdkVector_AndDependsOnEveryInput()
    {
        string secret = RelayBackup.Derive("game", "alice", "pw", Fast).AuthSecret;

        Assert.Equal("CLrdKDSaRfFotq1kGlEBfAy4qInZPjtKNYE0hzlVjcQ", secret);
        Assert.NotEqual(secret, RelayBackup.Derive("game", "alice", "other", Fast).AuthSecret);
        Assert.NotEqual(secret, RelayBackup.Derive("game", "bob", "pw", Fast).AuthSecret);
        Assert.NotEqual(secret, RelayBackup.Derive("other", "alice", "pw", Fast).AuthSecret);
    }

    [Fact]
    public void AuthSecret_RevealsNeitherTheKeyNorTheChannel()
    {
        RelayBackup backup = RelayBackup.Derive("game", "alice", "pw", Fast);

        Assert.DoesNotContain(backup.AuthSecret, backup.ChannelId);
        Assert.Equal(43, backup.AuthSecret.Length);
    }

    [Fact]
    public void ExportBlob_ThenImportBlob_RestoresTheIdentity_OnlyForTheSameAccount()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        RelayBackup backup = RelayBackup.Derive("game", "alice", "pw", Fast);

        string blob = backup.ExportBlob(identity);

        using VestaIdentity restored = RelayBackup.Derive("game", "alice", "pw", Fast).ImportBlob(blob);
        Assert.Equal(identity.ClientId, restored.ClientId);
        Assert.Throws<IdentityBackupException>(() => RelayBackup.Derive("game", "alice", "wrong", Fast).ImportBlob(blob));
        Assert.Throws<IdentityBackupException>(() => RelayBackup.Derive("game", "bob", "pw", Fast).ImportBlob(blob));
        // A standalone export under a random salt is not an account blob.
        Assert.Throws<IdentityBackupException>(() => backup.ImportBlob(identity.ExportEncrypted("pw", Fast)));
    }

    [Fact]
    public void Derive_MatchesThePythonSdkChannelVector()
    {
        Assert.Equal(
            "vesta/identity-backup/884a1d60332e7f820ac1039b7631223e",
            RelayBackup.Derive("game", "alice", "pw", Fast).ChannelId);
    }

    [Fact]
    public void CreateEvent_IsASignedReplaceEvent_AndRestoreRecoversTheIdentity()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        RelayBackup backup = RelayBackup.Derive("game", "alice", "pw", Fast);

        VestaEvent evt = backup.CreateEvent(identity);

        Assert.True(evt.Replace);
        Assert.Equal(RelayBackup.EventType, evt.EventType);
        Assert.Equal(backup.ChannelId, evt.ChannelId);
        Assert.True(EventSigner.VerifyEvent(evt, identity.PublicKey));
        using VestaIdentity restored = RelayBackup.Derive("game", "alice", "pw", Fast).Restore([evt])!;
        Assert.Equal(identity.ClientId, restored.ClientId);
    }

    [Fact]
    public void Restore_SkipsJunkFromOtherPublishers_AndFindsTheRealBackup()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        using VestaIdentity attacker = VestaIdentity.Generate();
        RelayBackup backup = RelayBackup.Derive("game", "alice", "pw", Fast);
        VestaEvent real = backup.CreateEvent(identity);
        // An attacker who knows the channel can publish a blob under their own key with a different password.
        VestaEvent junk = RelayBackup.Derive("game", "alice", "attacker-pw", Fast).CreateEvent(attacker) with { ChannelId = backup.ChannelId };
        VestaEvent garbage = real with { Id = Guid.NewGuid(), Payload = JsonDocument.Parse("""{"hello":"world"}""").RootElement };

        using VestaIdentity? restored = backup.Restore([junk, garbage, real]);

        Assert.NotNull(restored);
        Assert.Equal(identity.ClientId, restored.ClientId);
    }

    [Fact]
    public void Restore_WithTheWrongPassword_ReturnsNull()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaEvent evt = RelayBackup.Derive("game", "alice", "pw", Fast).CreateEvent(identity);

        Assert.Null(RelayBackup.Derive("game", "alice", "wrong", Fast).Restore([evt]));
    }

    [Fact]
    public void Restore_IgnoresBlobsWithOtherKdfSettings()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaEvent evt = RelayBackup.Derive("game", "alice", "pw", Fast).CreateEvent(identity);

        Assert.Null(RelayBackup.Derive("game", "alice", "pw", new IdentityBackupOptions(512, 1)).Restore([evt]));
    }
}
