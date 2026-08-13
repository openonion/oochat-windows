using System;
using System.Collections.Generic;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace ConnectOnion.Protocol;

/// <summary>
/// Ed25519 agent identity. Port of the Node path in <c>address.ts</c> plus
/// <c>signPayload</c> in <c>auth.ts</c>.
///
/// Key format matches the SDK: the address is <c>"0x" + hex(publicKey)</c> and
/// the private key is a raw 32-byte seed. Signatures are lowercase hex of the
/// 64-byte Ed25519 signature over the canonical-JSON payload — identical to
/// Node's <c>crypto.sign(null, msg, key)</c> and browser <c>nacl.sign.detached</c>.
/// </summary>
public sealed class AgentIdentity
{
    private readonly byte[] _publicKey;
    private readonly byte[] _privateSeed;

    public string Address { get; }

    /// <summary>The 32-byte Ed25519 public key, as a read-only view (callers cannot mutate it).</summary>
    public ReadOnlySpan<byte> PublicKey => _publicKey;

    /// <summary>
    /// The raw 32-byte private seed, as a read-only view. This is secret material —
    /// exposed as a span so callers cannot hold or mutate the backing array.
    /// </summary>
    public ReadOnlySpan<byte> PrivateSeed => _privateSeed;

    public string ShortAddress =>
        Address.Length >= 10 ? $"{Address[..6]}...{Address[^4..]}" : Address;

    private AgentIdentity(string address, byte[] publicKey, byte[] privateSeed)
    {
        Address = address;
        _publicKey = publicKey;
        _privateSeed = privateSeed;
    }

    /// <summary>Mints a brand-new identity. Called on first run and whenever the stored seed is
    /// unreadable — note that generating a new one changes the app's address, so every agent
    /// that authorized the old one must authorize this one (see <c>IdentityStore</c>).</summary>
    public static AgentIdentity Generate()
    {
        // SecureRandom, not System.Random: this seed *is* the private key, so it must come from
        // a CSPRNG. A predictable seed here is a forgeable identity.
        var random = new SecureRandom();
        var seed = new byte[32];
        random.NextBytes(seed);
        return FromSeed(seed);
    }

    /// <summary>
    /// Mints a new identity together with the BIP39 recovery phrase that reproduces it. This is
    /// the path new identities should take: <see cref="Generate"/> produces a seed that exists
    /// nowhere but the local database, so losing that database loses the address forever, while a
    /// phrase can be written down and re-entered here or in any other ConnectOnion client.
    /// </summary>
    /// <param name="wordCount">12 (the ecosystem default), 15, 18, 21 or 24.</param>
    public static (AgentIdentity Identity, string Mnemonic) GenerateWithMnemonic(int wordCount = 12)
    {
        var mnemonic = Bip39.Generate(wordCount);
        return (FromMnemonic(mnemonic), mnemonic);
    }

    /// <summary>
    /// Recovers the identity a BIP39 phrase encodes.
    ///
    /// <para>The derivation — first 32 bytes of the BIP39 seed, used directly as the Ed25519 seed —
    /// is shared with the Python SDK and oo-chat, so the same phrase yields the same address in all
    /// three. See <see cref="Bip39"/> before changing anything about it.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The phrase is not a valid BIP39 mnemonic.</exception>
    public static AgentIdentity FromMnemonic(string mnemonic, string passphrase = "")
    {
        ArgumentNullException.ThrowIfNull(mnemonic);
        if (!Bip39.Validate(mnemonic))
            throw new ArgumentException(
                "Not a valid BIP39 recovery phrase (check the word count and spelling).", nameof(mnemonic));

        var bip39Seed = Bip39.ToSeed(mnemonic, passphrase);
        try
        {
            var seed = new byte[32];
            Array.Copy(bip39Seed, seed, 32);
            try
            {
                return FromSeed(seed);
            }
            finally
            {
                // FromSeed clones, so this buffer is ours to wipe — key material must not be left
                // for the GC to hand to whoever gets the reused array next.
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(seed);
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bip39Seed);
        }
    }

    /// <summary>Reconstructs an identity from its stored 32-byte private seed.</summary>
    public static AgentIdentity FromSeed(byte[] seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (seed.Length != 32)
            throw new ArgumentException("Ed25519 seed must be 32 bytes.", nameof(seed));

        var privateKey = new Ed25519PrivateKeyParameters(seed, 0);
        var publicKey = privateKey.GeneratePublicKey().GetEncoded();
        // The address is fully derived from the seed — nothing else is stored, and the same
        // seed always reproduces the same identity. That is what makes the seed the one and
        // only thing worth backing up (and the one and only thing worth protecting).
        var address = "0x" + Hex.ToLowerString(publicKey);
        // Cloned so the identity does not alias the caller's array: a caller that zeroes its
        // buffer after handing it over (the correct thing to do with key material) would
        // otherwise quietly zero this identity's key too.
        return new AgentIdentity(address, publicKey, (byte[])seed.Clone());
    }

    /// <summary>Signs a message and returns the lowercase-hex 64-byte signature.</summary>
    public string Sign(string message)
    {
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var privateKey = new Ed25519PrivateKeyParameters(_privateSeed, 0);
        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        signer.BlockUpdate(messageBytes, 0, messageBytes.Length);
        var signature = signer.GenerateSignature();
        return Hex.ToLowerString(signature);
    }

    /// <summary>
    /// Verifies a hex signature against a 0x-address. Port of <c>verify()</c>.
    /// </summary>
    public static bool Verify(string address, string message, string signatureHex)
    {
        if (address is null || message is null || signatureHex is null)
            return false;

        // 66 = "0x" + 64 hex chars for the 32-byte public key. Checked before the hex decode so
        // a wrong-length address is rejected cheaply rather than through an exception.
        if (!address.StartsWith("0x", StringComparison.Ordinal) || address.Length != 66)
            return false;

        try
        {
            var publicKeyBytes = Convert.FromHexString(address[2..]);
            var signature = Convert.FromHexString(signatureHex);

            // An Ed25519 signature is always exactly 64 bytes.
            if (signature.Length != 64)
                return false;

            var messageBytes = Encoding.UTF8.GetBytes(message);
            var publicKey = new Ed25519PublicKeyParameters(publicKeyBytes, 0);
            var verifier = new Ed25519Signer();
            verifier.Init(false, publicKey);
            verifier.BlockUpdate(messageBytes, 0, messageBytes.Length);
            return verifier.VerifySignature(signature);
        }
        catch (FormatException)
        {
            // Malformed hex in the address or signature.
            return false;
        }
        catch (ArgumentException)
        {
            // Wrong-length public key rejected by BouncyCastle.
            return false;
        }
    }

    /// <summary>
    /// Signs a payload and returns the envelope the SDK sends:
    /// <c>{ payload, from, signature, timestamp }</c>. Port of
    /// <c>signPayload</c> in <c>auth.ts</c>. The payload is serialized with
    /// <see cref="CanonicalJson"/> before signing.
    /// </summary>
    public SignedEnvelope SignPayload(IReadOnlyList<KeyValuePair<string, object?>> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // A signed payload maps to a JS object, which cannot hold duplicate keys.
        // Reject them up front so the canonical JSON (and thus the signature) is
        // never silently derived from a malformed, order-dependent payload.
        var payloadMap = new Dictionary<string, object?>(payload.Count);
        foreach (var pair in payload)
        {
            if (!payloadMap.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException($"Duplicate payload key: '{pair.Key}'.", nameof(payload));
        }

        // Signed over the *list*, not the dictionary built above: CanonicalJson sorts the keys
        // itself, and a Dictionary's enumeration order is unspecified. The dictionary exists
        // only for duplicate detection and for the envelope's own payload field.
        var canonical = CanonicalJson.Serialize(payload);
        var signature = Sign(canonical);

        // The timestamp is lifted out for the envelope's convenience field; it stays inside the
        // signed payload as well, which is what actually makes it tamper-evident. A verifier
        // must read the payload's copy, never this one.
        object? timestamp = null;
        foreach (var pair in payload)
        {
            if (pair.Key == "timestamp") { timestamp = pair.Value; break; }
        }

        return new SignedEnvelope
        {
            Payload = payloadMap,
            From = Address,
            Signature = signature,
            Timestamp = timestamp,
        };
    }
}

/// <summary>The signed request envelope spread into CONNECT/ONBOARD messages.</summary>
public sealed class SignedEnvelope
{
    public Dictionary<string, object?> Payload { get; init; } = new();
    public string From { get; init; } = "";
    public string Signature { get; init; } = "";
    public object? Timestamp { get; init; }
}
