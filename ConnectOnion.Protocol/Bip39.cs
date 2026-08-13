using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace ConnectOnion.Protocol;

/// <summary>
/// BIP39 mnemonic codec — the recovery-phrase half of <see cref="AgentIdentity"/>.
///
/// <para><b>This must stay byte-for-byte compatible with the other ConnectOnion clients</b>, because
/// a phrase written down in one is typed into another and has to reproduce the same <c>0x</c>
/// address. Both references derive the identity the same way and this file matches them:
/// the Python SDK's <c>address.py</c> (<c>SigningKey(Mnemonic.to_seed(phrase)[:32])</c>) and
/// oo-chat's <c>use-identity.ts</c> (<c>nacl.sign.keyPair.fromSeed(mnemonicToSeedSync(m).slice(0,32))</c>).
/// So: PBKDF2-HMAC-SHA512, 2048 iterations, salt <c>"mnemonic"</c> + passphrase, take the first
/// 32 bytes of the 64-byte output as the Ed25519 seed. There is no BIP32/SLIP-0010 derivation path
/// anywhere in the ecosystem — do not add one here.</para>
///
/// <para>Only the English wordlist is embedded, for the same reason: it is the only list the other
/// clients can read back. <c>Mnemonic("english")</c> and <c>bip39.generateMnemonic()</c> both
/// default to it and neither exposes a way to pick another.</para>
/// </summary>
public static class Bip39
{
    /// <summary>Iteration count fixed by BIP39. Not a tunable — changing it changes every address.</summary>
    private const int Pbkdf2Iterations = 2048;

    /// <summary>The 64-byte PBKDF2 output length BIP39 specifies. The Ed25519 seed is its first half.</summary>
    private const int SeedLength = 64;

    /// <summary>Word counts BIP39 allows: 128–256 bits of entropy in 32-bit steps.</summary>
    private static readonly int[] AllowedWordCounts = [12, 15, 18, 21, 24];

    // Read once from the embedded resource. 2048 words, ~13 KB — small enough to keep resident,
    // and the index lookup is on the validation path so a per-call parse would be wasteful.
    private static readonly Lazy<(string[] Words, Dictionary<string, int> Index)> Wordlist =
        new(LoadWordlist, isThreadSafe: true);

    /// <summary>The BIP39 English wordlist, in index order.</summary>
    public static IReadOnlyList<string> EnglishWords => Wordlist.Value.Words;

    /// <summary>
    /// Mints a fresh phrase from CSPRNG entropy. 12 words (128 bits) is what every other
    /// ConnectOnion client generates, so it is the default here too.
    /// </summary>
    /// <param name="wordCount">12, 15, 18, 21 or 24.</param>
    public static string Generate(int wordCount = 12)
    {
        var entropyBytes = EntropyBytesForWordCount(wordCount);
        // RandomNumberGenerator, not System.Random: this entropy *becomes* the private key.
        var entropy = RandomNumberGenerator.GetBytes(entropyBytes);
        try
        {
            return FromEntropy(entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    /// <summary>Encodes entropy as a phrase: entropy bits + checksum bits, read 11 at a time.</summary>
    public static string FromEntropy(byte[] entropy)
    {
        ArgumentNullException.ThrowIfNull(entropy);
        if (entropy.Length is not (16 or 20 or 24 or 28 or 32))
            throw new ArgumentException(
                "BIP39 entropy must be 16, 20, 24, 28 or 32 bytes.", nameof(entropy));

        var checksumBits = entropy.Length * 8 / 32;
        // The checksum is at most 8 bits (32 bytes of entropy → 8), so it always fits in the
        // first byte of the digest. That is why one appended byte is enough rather than a splice.
        var digest = SHA256.HashData(entropy);
        var combined = new byte[entropy.Length + 1];
        Buffer.BlockCopy(entropy, 0, combined, 0, entropy.Length);
        combined[^1] = digest[0];

        var words = Wordlist.Value.Words;
        var wordCount = (entropy.Length * 8 + checksumBits) / 11;
        var builder = new StringBuilder(wordCount * 8);
        for (var i = 0; i < wordCount; i++)
        {
            if (i > 0) builder.Append(' ');
            builder.Append(words[ReadBits(combined, i * 11, 11)]);
        }
        return builder.ToString();
    }

    /// <summary>
    /// True when the phrase has a legal word count, uses only wordlist words, and its checksum
    /// matches. A phrase that fails this would still derive *a* seed — which is exactly why the
    /// check exists: a mistyped word must be reported, not silently turned into a stranger's address.
    /// </summary>
    public static bool Validate(string? mnemonic) => TryToEntropy(mnemonic, out _);

    /// <summary>Decodes a phrase back to its entropy, or returns false if it is not a valid phrase.</summary>
    public static bool TryToEntropy(string? mnemonic, out byte[] entropy)
    {
        entropy = [];
        if (string.IsNullOrWhiteSpace(mnemonic)) return false;

        var parts = Normalize(mnemonic).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (Array.IndexOf(AllowedWordCounts, parts.Length) < 0) return false;

        var index = Wordlist.Value.Index;
        var totalBits = parts.Length * 11;
        var bits = new byte[(totalBits + 7) / 8];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!index.TryGetValue(parts[i], out var wordIndex)) return false;
            WriteBits(bits, i * 11, 11, wordIndex);
        }

        var entropyBits = totalBits / 33 * 32;
        var checksumBits = totalBits - entropyBits;
        var decoded = new byte[entropyBits / 8];
        Buffer.BlockCopy(bits, 0, decoded, 0, decoded.Length);

        var expected = ReadBits(SHA256.HashData(decoded), 0, checksumBits);
        var actual = ReadBits(bits, entropyBits, checksumBits);
        if (expected != actual)
        {
            CryptographicOperations.ZeroMemory(decoded);
            return false;
        }

        entropy = decoded;
        return true;
    }

    /// <summary>
    /// Derives the 64-byte BIP39 seed. Callers wanting an identity want
    /// <see cref="AgentIdentity.FromMnemonic"/> instead — it takes the first 32 bytes of this,
    /// which is the ecosystem's convention and not something to re-decide per call site.
    /// </summary>
    /// <param name="passphrase">
    /// The optional BIP39 "25th word". No ConnectOnion client offers one, so this stays empty in
    /// practice; it exists so a phrase created elsewhere with a passphrase can still be imported.
    /// </param>
    public static byte[] ToSeed(string mnemonic, string passphrase = "")
    {
        ArgumentNullException.ThrowIfNull(mnemonic);

        // NFKD on both sides is the spec's requirement (and what Python's normalize_string does).
        // For the English wordlist it is a no-op, but a phrase pasted from a document can carry
        // composed characters or non-breaking spaces, and those must not derive a different seed.
        var password = Encoding.UTF8.GetBytes(Normalize(mnemonic));
        var salt = Encoding.UTF8.GetBytes("mnemonic" + (passphrase ?? "").Normalize(NormalizationForm.FormKD));

        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, SeedLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    /// <summary>
    /// The canonical form a phrase is validated and derived from: NFKD, lowercase, trimmed, with
    /// whitespace runs collapsed to single spaces.
    ///
    /// <para>Being forgiving here is deliberate — people paste phrases out of password managers and
    /// text files, complete with line breaks and stray capitals, and the reference clients do the
    /// same (oo-chat lowercases and trims before it validates). Since every wordlist word is already
    /// lowercase and single-space separated, canonicalizing cannot change the derived seed for any
    /// phrase that was valid to begin with.</para>
    /// </summary>
    public static string Normalize(string mnemonic)
    {
        ArgumentNullException.ThrowIfNull(mnemonic);

        var normalized = mnemonic.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch))
            {
                // Deferred rather than appended: this drops leading/trailing runs for free.
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
    }

    private static int EntropyBytesForWordCount(int wordCount) => wordCount switch
    {
        12 => 16,
        15 => 20,
        18 => 24,
        21 => 28,
        24 => 32,
        _ => throw new ArgumentOutOfRangeException(
            nameof(wordCount), wordCount, "BIP39 phrases are 12, 15, 18, 21 or 24 words."),
    };

    /// <summary>Reads <paramref name="count"/> bits (≤ 32) big-endian from <paramref name="offset"/>.</summary>
    private static int ReadBits(byte[] source, int offset, int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++)
        {
            var bit = offset + i;
            var set = (source[bit / 8] >> (7 - bit % 8)) & 1;
            value = (value << 1) | set;
        }
        return value;
    }

    /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/> big-endian.</summary>
    private static void WriteBits(byte[] target, int offset, int count, int value)
    {
        for (var i = 0; i < count; i++)
        {
            var set = (value >> (count - 1 - i)) & 1;
            if (set == 0) continue;
            var bit = offset + i;
            target[bit / 8] |= (byte)(1 << (7 - bit % 8));
        }
    }

    private static (string[] Words, Dictionary<string, int> Index) LoadWordlist()
    {
        var assembly = typeof(Bip39).Assembly;
        const string resourceName = "ConnectOnion.Protocol.Bip39EnglishWordlist.txt";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded BIP39 wordlist '{resourceName}' is missing from {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var words = new string[2048];
        var index = new Dictionary<string, int>(2048, StringComparer.Ordinal);
        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            var word = line.Trim();
            if (word.Length == 0) continue;
            if (count == words.Length)
                throw new InvalidOperationException("The embedded BIP39 wordlist has too many words.");
            words[count] = word;
            index[word] = count;
            count++;
        }

        // A truncated or duplicated list would produce phrases other clients reject, and the
        // failure would look like a checksum bug rather than a packaging bug. Fail loudly instead.
        if (count != 2048 || index.Count != 2048)
            throw new InvalidOperationException(
                $"The embedded BIP39 wordlist must hold exactly 2048 distinct words (found {count}, {index.Count} distinct).");

        return (words, index);
    }
}
