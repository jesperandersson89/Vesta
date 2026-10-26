using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Vesta;

/// <summary>Thrown when a recovery phrase is malformed or its checksum does not match.</summary>
public sealed class RecoveryPhraseException(string message) : FormatException(message);

/// <summary>
/// Encodes a 32-byte identity seed as a 24-word phrase (BIP-39 English wordlist, 256-bit entropy +
/// 8-bit SHA-256 checksum) so it can be written down. This is BIP-39's entropy-to-mnemonic mapping
/// only: the seed itself is the entropy, there is no PBKDF2 stretching and no BIP-39 passphrase.
/// </summary>
public static class RecoveryPhrase
{
    private const int SeedLength = 32;
    private const int WordCount = 24;

    private static readonly Dictionary<string, int> _indexByWord = Bip39WordList.English
        .Select((word, index) => (word, index))
        .ToDictionary(pair => pair.word, pair => pair.index, StringComparer.Ordinal);

    /// <summary>The 24-word phrase for a 32-byte seed.</summary>
    public static string FromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedLength)
            throw new ArgumentException($"A recovery phrase encodes exactly {SeedLength} bytes.", nameof(seed));

        byte checksum = SHA256.HashData(seed)[0];
        byte[] data = new byte[SeedLength + 1];
        seed.CopyTo(data);
        data[SeedLength] = checksum;

        BigInteger bits = new(data, isUnsigned: true, isBigEndian: true);
        string[] words = new string[WordCount];
        for (int i = WordCount - 1; i >= 0; i--)
        {
            words[i] = Bip39WordList.English[(int)(bits & 0x7FF)];
            bits >>= 11;
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// The seed for a phrase. Case and extra whitespace are ignored. Throws
    /// <see cref="RecoveryPhraseException"/> for a wrong word count, an unknown word or a bad checksum.
    /// </summary>
    public static byte[] ToSeed(string phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        string[] words = phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length != WordCount)
            throw new RecoveryPhraseException($"A recovery phrase has {WordCount} words, got {words.Length}.");

        BigInteger bits = BigInteger.Zero;
        foreach (string raw in words)
        {
            if (!_indexByWord.TryGetValue(raw.ToLowerInvariant(), out int index))
                throw new RecoveryPhraseException($"'{raw}' is not a recovery-phrase word.");
            bits = (bits << 11) | (uint)index;
        }

        byte[] all = bits.ToByteArray(isUnsigned: true, isBigEndian: true);
        byte[] data = new byte[SeedLength + 1];
        all.CopyTo(data, data.Length - all.Length);

        byte[] seed = data[..SeedLength];
        if (SHA256.HashData(seed)[0] != data[SeedLength])
            throw new RecoveryPhraseException("The recovery phrase checksum does not match; check for a mistyped or misordered word.");

        return seed;
    }
}
