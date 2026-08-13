using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Runtime;

/// <summary>
/// The second door into the database: a credential the user handed the agent comes back out inside
/// the arguments of whatever tool the agent types it into, and those are persisted.
///
/// <para>Not run in parallel with anything else — the registry is process-wide by design (an agent
/// carries a credential across turns and conversations), so a test that remembers a secret would
/// otherwise leak into a neighbour.</para>
/// </summary>
[Collection(nameof(SessionSecretsTests))]
[CollectionDefinition(nameof(SessionSecretsTests), DisableParallelization = true)]
public sealed class SessionSecretsTests : IDisposable
{
    public SessionSecretsTests() => SessionSecrets.Clear();
    public void Dispose() => SessionSecrets.Clear();

    [Fact]
    public void Redact_MasksARememberedCredential()
    {
        SessionSecrets.Remember("hunter2");

        Assert.Equal(SessionSecrets.Mask, SessionSecrets.Redact("hunter2"));
    }

    [Fact]
    public void Redact_LeavesEverythingElseAlone()
    {
        SessionSecrets.Remember("hunter2");

        // Exact matches only: a substring rule would turn a short password into a censor that
        // fires on ordinary words.
        Assert.Equal("hunter", SessionSecrets.Redact("hunter"));
        Assert.Equal("my hunter2 note", SessionSecrets.Redact("my hunter2 note"));
        Assert.Equal("HUNTER2", SessionSecrets.Redact("HUNTER2"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Too short to be worth the false positives it would cause.
    [InlineData("ab")]
    public void Remember_IgnoresValuesNotWorthMasking(string? value)
    {
        SessionSecrets.Remember(value);

        Assert.True(SessionSecrets.IsEmpty);
    }

    [Fact]
    public void Remember_TrimsBeforeStoring()
    {
        SessionSecrets.Remember("  hunter2  ");

        Assert.Equal(SessionSecrets.Mask, SessionSecrets.Redact("hunter2"));
    }

    [Fact]
    public void SanitizeText_MasksAPasswordTheAgentTypesIntoATool()
    {
        // The actual attack path: nothing in {"text": "hunter2"} names it a secret, so the labelled
        // key=value scrubber cannot see it — only knowing the value gives it away.
        SessionSecrets.Remember("hunter2");

        var sanitized = ToolActivityProjector.SanitizeJson("""{"selector":"#pw","text":"hunter2"}""");

        Assert.DoesNotContain("hunter2", sanitized, StringComparison.Ordinal);
        Assert.Contains(SessionSecrets.Mask, sanitized, StringComparison.Ordinal);
        // The rest of the arguments must survive: a step whose every field is masked says nothing.
        Assert.Contains("#pw", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeText_WithNothingRemembered_IsUnchanged()
    {
        Assert.True(SessionSecrets.IsEmpty);
        Assert.Equal("hunter2", ToolActivityProjector.SanitizeText("hunter2"));
    }

    [Fact]
    public void SanitizeText_StillAppliesTheLabelledFormScrubber()
    {
        // The two passes are complementary, not alternatives.
        Assert.Contains("[hidden]", ToolActivityProjector.SanitizeText("api_key=sk-live-123"), StringComparison.Ordinal);
    }
}

/// <summary>
/// Sanitized arguments are read by a person, so re-encoding them must not mangle them. The default
/// serializer escapes far more than JSON requires, which is how an ordinary search URL reached the
/// timeline as <c>Sydney+weather</c>.
/// </summary>
public sealed class SanitizeJsonEncodingTests
{
    [Theory]
    // Plain ASCII the default encoder escapes anyway — every URL with a query string hit this.
    [InlineData("""{"url":"https://duckduckgo.com/?q=Sydney+weather&ia=web"}""", "?q=Sydney+weather&ia=web")]
    [InlineData("""{"agent":"<Agent>"}""", "<Agent>")]
    // Non-ASCII arguments arrived as a wall of \uXXXX.
    [InlineData("""{"text":"小红书"}""", "小红书")]
    public void SanitizeJson_KeepsArgumentsReadable(string raw, string expectedFragment)
    {
        var sanitized = ToolActivityProjector.SanitizeJson(raw);

        Assert.Contains(expectedFragment, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeJson_StillRedactsSensitiveKeys()
    {
        // Readability must not have cost the redaction it shares a method with.
        var sanitized = ToolActivityProjector.SanitizeJson("""{"url":"https://a.test/?q=x+y","api_key":"sk-live-1"}""");

        Assert.Contains("[hidden]", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-1", sanitized, StringComparison.Ordinal);
    }

    /// <summary>
    /// The redaction recursion now writes straight into a <c>Utf8JsonWriter</c> instead of
    /// building an <c>object?</c> tree for the reflection serializer to walk — that serializer
    /// throws under trimming, and this method runs on every persisted tool step. Structure has to
    /// come out unchanged.
    /// </summary>
    [Fact]
    public void SanitizeJson_PreservesNestedStructureAndValueKinds()
    {
        var sanitized = ToolActivityProjector.SanitizeJson(
            """{"a":{"b":[1,2.5,true,false,null,{"c":"d"}]},"e":[]}""");

        Assert.Equal("""{"a":{"b":[1,2.5,true,false,null,{"c":"d"}]},"e":[]}""", sanitized);
    }

    [Fact]
    public void SanitizeJson_RedactsSecretsNestedInsideArrays()
    {
        var sanitized = ToolActivityProjector.SanitizeJson(
            """{"steps":[{"name":"login","password":"hunter2"}]}""");

        Assert.Contains("[hidden]", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", sanitized, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"login\"", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeJson_NonJsonPayload_FallsBackToTheTextScrubber()
    {
        var sanitized = ToolActivityProjector.SanitizeJson("Traceback: api_key=sk-live-1 at line 3");

        Assert.Contains("[hidden]", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-1", sanitized, StringComparison.Ordinal);
    }
}
