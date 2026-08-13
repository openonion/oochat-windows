using ConnectOnion.Protocol;

namespace ConnectOnion.Protocol.Tests;

public sealed class AgentIdentityTests
{
    private static readonly byte[] Seed = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void FromSeed_ClonesSecretMaterialAndDerivesAStableAddress()
    {
        var callerSeed = (byte[])Seed.Clone();
        var identity = AgentIdentity.FromSeed(callerSeed);
        var originalAddress = identity.Address;

        callerSeed[0] ^= 0xff;

        Assert.Equal(originalAddress, identity.Address);
        Assert.True(identity.PrivateSeed.SequenceEqual(Seed));
        Assert.Equal(32, identity.PublicKey.Length);
        Assert.Equal("0x" + Convert.ToHexString(identity.PublicKey).ToLowerInvariant(), identity.Address);
        Assert.Equal($"{identity.Address[..6]}...{identity.Address[^4..]}", identity.ShortAddress);
    }

    [Fact]
    public void FromSeed_RejectsNull()
        => Assert.Throws<ArgumentNullException>(() => AgentIdentity.FromSeed(null!));

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void FromSeed_RejectsWrongLength(int length)
        => Assert.Throws<ArgumentException>(() => AgentIdentity.FromSeed(new byte[length]));

    [Fact]
    public void SignAndVerify_RoundTripAndDetectTampering()
    {
        var identity = AgentIdentity.FromSeed(Seed);
        var signature = identity.Sign("hello \u6d4b\u8bd5");

        Assert.Equal(128, signature.Length);
        Assert.True(AgentIdentity.Verify(identity.Address, "hello \u6d4b\u8bd5", signature));
        Assert.False(AgentIdentity.Verify(identity.Address, "hello!", signature));
    }

    [Theory]
    [InlineData(null, "message", "00")]
    [InlineData("0x00", "message", "00")]
    [InlineData("0xzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", "message", "00")]
    [InlineData("0x0000000000000000000000000000000000000000000000000000000000000000", "message", "not-hex")]
    [InlineData("0x0000000000000000000000000000000000000000000000000000000000000000", "message", "00")]
    public void Verify_RejectsMalformedInputs(string? address, string message, string signature)
        => Assert.False(AgentIdentity.Verify(address!, message, signature));

    [Fact]
    public void SignPayload_PreservesTimestampAndSignsCanonicalPayload()
    {
        var identity = AgentIdentity.FromSeed(Seed);
        KeyValuePair<string, object?>[] payload =
        [
            new("timestamp", 1234L),
            new("type", "CONNECT"),
            new("active", true),
        ];

        var envelope = identity.SignPayload(payload);

        Assert.Equal(identity.Address, envelope.From);
        Assert.Equal(1234L, envelope.Timestamp);
        Assert.Equal("CONNECT", envelope.Payload["type"]);
        Assert.True(AgentIdentity.Verify(
            identity.Address, CanonicalJson.Serialize(payload), envelope.Signature));
    }

    [Fact]
    public void SignPayload_RejectsDuplicateKeys()
    {
        var identity = AgentIdentity.FromSeed(Seed);
        KeyValuePair<string, object?>[] payload =
        [
            new("timestamp", 1L),
            new("timestamp", 2L),
        ];

        var error = Assert.Throws<ArgumentException>(() => identity.SignPayload(payload));

        Assert.Contains("Duplicate payload key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SignPayload_RejectsNull()
        => Assert.Throws<ArgumentNullException>(() =>
            AgentIdentity.FromSeed(Seed).SignPayload(null!));

    [Fact]
    public void CanonicalJson_SortsKeysAndSupportsTheWireScalarTypes()
    {
        KeyValuePair<string, object?>[] values =
        [
            new("z", null),
            new("text", "<onion>&"),
            new("long", 9_000_000_000L),
            new("int", 7),
            new("double", 1.5d),
            new("bool", true),
        ];

        Assert.Equal(
            "{\"bool\":true,\"double\":1.5,\"int\":7,\"long\":9000000000,\"text\":\"<onion>&\",\"z\":null}",
            CanonicalJson.Serialize(values));
    }

    [Theory]
    [InlineData(1.5f)]
    [InlineData('x')]
    public void CanonicalJson_RejectsTypesWithoutACrossClientCanonicalForm(object value)
        => Assert.Throws<NotSupportedException>(() => CanonicalJson.Serialize(
            [new KeyValuePair<string, object?>("value", value)]));
}
