using System;
using ConnectOnion.Protocol;

namespace ConnectOnion.Protocol.Tests;

/// <summary>
/// BIP39 conformance. The vectors below were produced by the reference implementations this
/// client has to interoperate with — the standard Trezor vectors as emitted by Python's
/// <c>mnemonic</c> package (the same library the ConnectOnion Python SDK's <c>address.py</c>
/// uses), and the addresses by <c>pynacl</c> over the derivation <c>address.py</c> performs.
/// They are not hand-transcribed, and they are the actual contract: if one of these changes,
/// a recovery phrase written down in the CLI or oo-chat stops restoring the same identity here.
/// </summary>
public sealed class Bip39Tests
{
    // entropy hex, phrase, BIP39 seed hex under the spec's "TREZOR" passphrase.
    public static TheoryData<string, string, string> OfficialVectors => new()
    {
        {
            "00000000000000000000000000000000",
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
            "c55257c360c07c72029aebc1b53c05ed0362ada38ead3e3e9efa3708e53495531f09a6987599d18264c1e1c92f2cf141630c7a3c4ab7c81b2f001698e7463b04"
        },
        {
            "7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f",
            "legal winner thank year wave sausage worth useful legal winner thank yellow",
            "2e8905819b8723fe2c1d161860e5ee1830318dbf49a83bd451cfb8440c28bd6fa457fe1296106559a3c80937a1c1069be3a3a5bd381ee6260e8d9739fce1f607"
        },
        {
            "80808080808080808080808080808080",
            "letter advice cage absurd amount doctor acoustic avoid letter advice cage above",
            "d71de856f81a8acc65e6fc851a38d4d7ec216fd0796d0a6827a3ad6ed5511a30fa280f12eb2e47ed2ac03b5c462a0358d18d69fe4f985ec81778c1b370b652a8"
        },
        {
            "ffffffffffffffffffffffffffffffff",
            "zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo wrong",
            "ac27495480225222079d7be181583751e86f571027b0497b5b5d11218e0a8a13332572917f0f8e5a589620c6f15b11c61dee327651a14c34e18231052e48c069"
        },
        {
            "0000000000000000000000000000000000000000000000000000000000000000",
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon "
                + "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon art",
            "bda85446c68413707090a52022edd26a1c9462295029f2e60cd7c4f2bbd3097170af7a4d73245cafa9c3cca8d561a7c3de6f5d4a10be8ed2a5e608d68f92fcc8"
        },
        {
            "f585c11aec520db57dd353c69554b21a89b20fb0650966fa0a9d6f74fd989d8f",
            "void come effort suffer camp survey warrior heavy shoot primary clutch crush "
                + "open amazing screen patrol group space point ten exist slush involve unfold",
            "01f5bced59dec48e362f2c45b5de68b9fd6c92c6634f44d6d40aab69056506f0e35524a518034ddc1192e1dacd32c1ed3eaa3c3b131c88ed8e7e54c49a5d0998"
        },
    };

    [Theory]
    [MemberData(nameof(OfficialVectors))]
    public void FromEntropy_ProducesTheStandardPhrase(string entropyHex, string expectedPhrase, string _)
    {
        Assert.Equal(expectedPhrase, Bip39.FromEntropy(Convert.FromHexString(entropyHex)));
    }

    [Theory]
    [MemberData(nameof(OfficialVectors))]
    public void TryToEntropy_RoundTripsThePhraseBackToItsEntropy(string entropyHex, string phrase, string _)
    {
        Assert.True(Bip39.TryToEntropy(phrase, out var entropy));
        Assert.Equal(entropyHex, Convert.ToHexString(entropy).ToLowerInvariant());
    }

    [Theory]
    [MemberData(nameof(OfficialVectors))]
    public void ToSeed_MatchesTheSpecVectorUnderTheTrezorPassphrase(string _, string phrase, string expectedSeedHex)
    {
        var seed = Bip39.ToSeed(phrase, "TREZOR");
        Assert.Equal(expectedSeedHex, Convert.ToHexString(seed).ToLowerInvariant());
    }

    /// <summary>
    /// The cross-client contract that actually matters: no passphrase, first 32 bytes of the BIP39
    /// seed used directly as the Ed25519 seed. Expected addresses come from pynacl over Python's
    /// <c>Mnemonic.to_seed(phrase)[:32]</c> — exactly what <c>address.py</c> and oo-chat's
    /// <c>keysFromMnemonic</c> do.
    /// </summary>
    [Theory]
    [InlineData(
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
        "0xc5785e1865b708938aff8161d573006496663b1aa10834e396dc566869a2c66a")]
    [InlineData(
        "legal winner thank year wave sausage worth useful legal winner thank yellow",
        "0xc6f2ac5598970c79633714d3eb5c34d7bfc3e92da58c7354b37996d9a4af3ab2")]
    public void FromMnemonic_DerivesTheSameAddressAsThePythonSdk(string phrase, string expectedAddress)
    {
        Assert.Equal(expectedAddress, AgentIdentity.FromMnemonic(phrase).Address);
    }

    [Fact]
    public void FromMnemonic_IsForgivingAboutCasingAndWhitespace()
    {
        // What a phrase looks like after a round trip through a password manager or a PDF.
        const string canonical =
            "legal winner thank year wave sausage worth useful legal winner thank yellow";
        const string messy =
            "  Legal   winner\tthank year\nwave sausage worth useful legal winner thank YELLOW ";

        Assert.Equal(canonical, Bip39.Normalize(messy));
        Assert.Equal(
            AgentIdentity.FromMnemonic(canonical).Address,
            AgentIdentity.FromMnemonic(messy).Address);
    }

    [Fact]
    public void Generate_ProducesAValidPhraseThatRoundTrips()
    {
        for (var i = 0; i < 16; i++)
        {
            var phrase = Bip39.Generate();
            Assert.Equal(12, phrase.Split(' ').Length);
            Assert.True(Bip39.Validate(phrase), $"generated phrase failed validation: {phrase}");
            Assert.Equal(
                AgentIdentity.FromMnemonic(phrase).Address,
                AgentIdentity.FromMnemonic(phrase).Address);
        }
    }

    [Theory]
    [InlineData(12)]
    [InlineData(15)]
    [InlineData(18)]
    [InlineData(21)]
    [InlineData(24)]
    public void Generate_SupportsEveryLegalLength(int wordCount)
    {
        var phrase = Bip39.Generate(wordCount);
        Assert.Equal(wordCount, phrase.Split(' ').Length);
        Assert.True(Bip39.Validate(phrase));
    }

    [Fact]
    public void Generate_ProducesADistinctPhraseEachTime()
    {
        // A constant phrase here would mean every install shares one identity — worth an
        // explicit assertion rather than trusting the RNG call site stays correct.
        var phrases = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 32; i++) Assert.True(phrases.Add(Bip39.Generate()));
    }

    [Theory]
    // A single mistyped word breaks the checksum — the case the whole checksum exists for.
    [InlineData("legal winner thank year wave sausage worth useful legal winner thank zebra")]
    // Right word count, but "notaword" is not in the list at all.
    [InlineData("legal winner thank year wave sausage worth useful legal winner thank notaword")]
    // Correct words, wrong count.
    [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about")]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsPhrasesThatWouldSilentlyDeriveTheWrongIdentity(string phrase)
    {
        Assert.False(Bip39.Validate(phrase));
        Assert.Throws<ArgumentException>(() => AgentIdentity.FromMnemonic(phrase));
    }

    [Fact]
    public void Validate_RejectsNull() => Assert.False(Bip39.Validate(null));

    [Fact]
    public void EnglishWords_IsTheFullWordlist()
    {
        Assert.Equal(2048, Bip39.EnglishWords.Count);
        Assert.Equal("abandon", Bip39.EnglishWords[0]);
        Assert.Equal("zoo", Bip39.EnglishWords[2047]);
    }

    [Fact]
    public void GenerateWithMnemonic_ReturnsAPhraseThatRestoresTheSameIdentity()
    {
        var (identity, mnemonic) = AgentIdentity.GenerateWithMnemonic();

        Assert.True(Bip39.Validate(mnemonic));
        Assert.Equal(identity.Address, AgentIdentity.FromMnemonic(mnemonic).Address);
        Assert.True(identity.PrivateSeed.SequenceEqual(AgentIdentity.FromMnemonic(mnemonic).PrivateSeed));
    }

    [Fact]
    public void FromMnemonic_SeedIsTheFirstHalfOfTheBip39Seed()
    {
        // Pins the one derivation decision that is a convention rather than a spec requirement:
        // truncate to 32 bytes, never hash or SLIP-0010 the seed.
        const string phrase = "zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo wrong";
        var expected = Bip39.ToSeed(phrase).AsSpan(0, 32).ToArray();

        Assert.True(AgentIdentity.FromMnemonic(phrase).PrivateSeed.SequenceEqual(expected));
    }
}
