using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccountServer;

/// <summary>One account: a salted verifier of the client's login secret, and the opaque encrypted identity.</summary>
public sealed record Account(string Salt, string Verifier, string Blob);

/// <summary>
/// In-memory account table with optional JSON-file persistence. It never sees a password or a private
/// key: it holds <c>SHA-256(salt ‖ authSecret)</c> and the encrypted blob exactly as the client produced it.
/// Replace this with a real database; the shape (username → verifier + blob) is all a game needs.
/// </summary>
public sealed class AccountStore
{
    private static readonly byte[] DummySalt = new byte[16];

    private readonly ConcurrentDictionary<string, Account> _accounts;
    private readonly string? _file;
    private readonly Lock _writeLock = new();

    public AccountStore(string? dataFile = null)
    {
        _file = dataFile;
        _accounts = dataFile is not null && File.Exists(dataFile)
            ? new ConcurrentDictionary<string, Account>(
                JsonSerializer.Deserialize<Dictionary<string, Account>>(File.ReadAllText(dataFile)) ?? [])
            : new ConcurrentDictionary<string, Account>();
    }

    /// <summary>Trim, NFKC and lower-case, so "Alice " and "alice" are one account (matches the SDKs' derivation).</summary>
    public static string NormalizeUsername(string username) => username.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    public bool TryCreate(string username, string authSecret, string blob)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        Account account = new(Convert.ToBase64String(salt), Hash(salt, authSecret), blob);
        if (!_accounts.TryAdd(username, account))
            return false;

        Save();
        return true;
    }

    /// <summary>The account when <paramref name="authSecret"/> matches; always does the same work for unknown users.</summary>
    public Account? Verify(string username, string authSecret)
    {
        bool found = _accounts.TryGetValue(username, out Account? account);
        byte[] salt = found ? Convert.FromBase64String(account!.Salt) : DummySalt;
        byte[] expected = found ? Convert.FromBase64String(account!.Verifier) : new byte[32];
        byte[] actual = Convert.FromBase64String(Hash(salt, authSecret));
        bool match = CryptographicOperations.FixedTimeEquals(expected, actual);
        return found && match ? account : null;
    }

    public void Update(string username, string? newAuthSecret, string blob)
    {
        _accounts.AddOrUpdate(
            username,
            _ => throw new InvalidOperationException("Unknown account."),
            (_, existing) =>
            {
                if (newAuthSecret is null)
                    return existing with { Blob = blob };

                byte[] salt = RandomNumberGenerator.GetBytes(16);
                return new Account(Convert.ToBase64String(salt), Hash(salt, newAuthSecret), blob);
            });
        Save();
    }

    public void Delete(string username)
    {
        _accounts.TryRemove(username, out _);
        Save();
    }

    private static string Hash(byte[] salt, string authSecret)
    {
        byte[] secret = DecodeBase64Url(authSecret);
        return Convert.ToBase64String(SHA256.HashData([.. salt, .. secret]));
    }

    public static byte[] DecodeBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4));
    }

    private void Save()
    {
        if (_file is null)
            return;

        lock (_writeLock)
        {
            string temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Dictionary<string, Account>(_accounts)));
            File.Move(temp, _file, overwrite: true);
        }
    }
}
