using System.Text;
using System.Text.Json;

namespace ConnectOnion.Protocol.Tests;

/// <summary>
/// Covers <see cref="WireJson"/>, which replaced <c>JsonSerializer.Serialize</c> on every
/// outgoing frame because the reflection serializer throws under <c>PublishTrimmed=true</c>
/// (see <c>docs/TRIMMING.md</c>).
///
/// <para><b>Most of these assert against <c>JsonSerializer.Serialize</c> as an oracle rather
/// than against a hand-written expected string, and that is the design.</b> The requirement is
/// not "emits reasonable JSON", it is "emits the bytes the agent already receives today" — so
/// the old implementation is exactly the right thing to diff against. Reflection is still
/// enabled in a test host, so the oracle runs here even though it cannot run in a trimmed
/// build; that asymmetry is the whole reason this file exists.</para>
/// </summary>
public class WireJsonTests
{
    private static void AssertMatchesReflectionSerializer(Dictionary<string, object?> frame)
        => Assert.Equal(JsonSerializer.Serialize(frame), WireJson.Serialize(frame));

    [Fact]
    public void EveryScalarKind_MatchesReflectionSerializer()
    {
        AssertMatchesReflectionSerializer(new Dictionary<string, object?>
        {
            ["null"] = null,
            ["string"] = "hello",
            ["true"] = true,
            ["false"] = false,
            ["int"] = 42,
            ["long"] = 9_007_199_254_740_993L,
            ["double"] = 1.5,
            ["negative"] = -17,
            ["zero"] = 0,
        });
    }

    [Fact]
    public void NestedObjectsAndArrays_MatchReflectionSerializer()
    {
        AssertMatchesReflectionSerializer(new Dictionary<string, object?>
        {
            ["type"] = "CONNECT",
            ["payload"] = new Dictionary<string, object?>
            {
                ["timestamp"] = 1_700_000_000L,
                ["to"] = "0xabc",
                ["nested"] = new Dictionary<string, object?> { ["deep"] = true },
            },
            ["list"] = new List<object?> { "a", 1, false, null },
        });
    }

    /// <summary>
    /// The default encoder escapes far more than JSON requires — non-ASCII, <c>+</c>, <c>&amp;</c>,
    /// <c>&lt;</c>, <c>&gt;</c>. That over-escaping is what the agent already receives, so the
    /// writer must keep it. (<see cref="CanonicalJson"/> is the deliberate exception: it uses the
    /// relaxed encoder to match what the JS side signs.)
    /// </summary>
    [Fact]
    public void Escaping_MatchesReflectionSerializer_IncludingNonAsciiAndHtmlCharacters()
    {
        var frame = new Dictionary<string, object?>
        {
            ["prompt"] = "Sydney+weather & <tags> \"quoted\" 中文 🙂\n\ttab",
            ["中文键"] = "value",
        };

        AssertMatchesReflectionSerializer(frame);
        // Guards the intent, not just the equality: if both sides ever switched to the relaxed
        // encoder together the oracle test above would still pass and the wire bytes would change.
        Assert.Contains("\\u002B", WireJson.Serialize(frame), StringComparison.Ordinal);
    }

    [Fact]
    public void PropertyOrder_FollowsInsertionOrder_NotSorted()
    {
        var frame = new Dictionary<string, object?>
        {
            ["zulu"] = 1,
            ["alpha"] = 2,
            ["mike"] = 3,
        };

        Assert.Equal("""{"zulu":1,"alpha":2,"mike":3}""", WireJson.Serialize(frame));
        AssertMatchesReflectionSerializer(frame);
    }

    [Fact]
    public void EmptyFrame_IsEmptyObject()
        => Assert.Equal("{}", WireJson.Serialize(new Dictionary<string, object?>()));

    [Fact]
    public void Utf8Serialization_MatchesStringContractWithoutAJsonStringRoundTrip()
    {
        var frame = InputMessageBuilder.BuildInput("中文 attachment", "input-utf8", null,
            images: new[] { "data:image/png;base64,AAAA" });

        Assert.Equal(
            WireJson.Serialize(frame),
            Encoding.UTF8.GetString(WireJson.SerializeToUtf8Bytes(frame)));
    }

    [Fact]
    public void JsonElementValue_IsCopiedVerbatim()
    {
        using var doc = JsonDocument.Parse("""{"tool":"bash","args":{"cmd":"ls -la"},"n":[1,2]}""");
        var frame = new Dictionary<string, object?>
        {
            ["type"] = "approval_needed",
            ["arguments"] = doc.RootElement.Clone(),
        };

        Assert.Equal(
            """{"type":"approval_needed","arguments":{"tool":"bash","args":{"cmd":"ls -la"},"n":[1,2]}}""",
            WireJson.Serialize(frame));
    }

    /// <summary>
    /// A number that arrives as a <see cref="JsonElement"/> keeps its original text. Round-tripping
    /// it through <c>double</c> would silently reshape the agent's own value.
    /// </summary>
    [Fact]
    public void JsonElementNumber_KeepsOriginalPrecision()
    {
        using var doc = JsonDocument.Parse("""{"amount":1.100}""");
        var frame = new Dictionary<string, object?> { ["a"] = doc.RootElement.Clone() };

        Assert.Equal("""{"a":{"amount":1.100}}""", WireJson.Serialize(frame));
    }

    [Fact]
    public void StringSequences_WriteAsArrays()
    {
        // string[] is what a multi-select ask_user answer produces; IReadOnlyList<string> is
        // what the INPUT builder hands over for images.
        AssertMatchesReflectionSerializer(new Dictionary<string, object?>
        {
            ["array"] = new[] { "a", "b" },
            ["list"] = new List<string> { "c" },
        });
    }

    [Fact]
    public void StringMapValue_WritesAsObject_NotAsKeyValuePairArray()
    {
        var frame = new Dictionary<string, object?>
        {
            ["headers"] = new Dictionary<string, string> { ["k"] = "v" },
        };

        Assert.Equal("""{"headers":{"k":"v"}}""", WireJson.Serialize(frame));
        AssertMatchesReflectionSerializer(frame);
    }

    /// <summary>
    /// The closed value set is the contract. A builder that starts putting a new type on the wire
    /// must fail here rather than emit whatever the writer happens to do with it.
    /// </summary>
    [Fact]
    public void UnsupportedValueType_Throws()
    {
        var frame = new Dictionary<string, object?> { ["when"] = new DateTime(2026, 7, 27) };

        var ex = Assert.Throws<NotSupportedException>(() => WireJson.Serialize(frame));
        Assert.Contains("DateTime", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeStringMap_MatchesReflectionSerializer()
    {
        var values = new Dictionary<string, string>
        {
            ["username"] = "bob",
            ["password"] = "p+ss & <word>",
            ["注释"] = "值",
        };

        Assert.Equal(JsonSerializer.Serialize(values), WireJson.SerializeStringMap(values));
    }

    [Fact]
    public void SerializeStringMap_EmptyIsEmptyObject()
        => Assert.Equal("{}", WireJson.SerializeStringMap(new Dictionary<string, string>()));

    // --- The real frames, end to end through the builders that produce them. ---

    public static TheoryData<string, Dictionary<string, object?>> RealFrames() => new()
    {
        { "INPUT text-only", InputMessageBuilder.BuildInput("hello", "input-1", null) },
        {
            "INPUT with images",
            InputMessageBuilder.BuildInput("look", "input-2", null,
                images: new[] { "data:image/png;base64,iVBORw0KGgo=" })
        },
        {
            "INPUT with files",
            InputMessageBuilder.BuildInput("read", "input-3", null,
                files: new[] { new OutgoingFileAttachment("doc.pdf", "data:application/pdf;base64,AAA=") })
        },
        {
            "INPUT mixed and relayed",
            InputMessageBuilder.BuildInput("both", "input-4", "0xdeadbeef",
                images: new[] { "data:image/png;base64,iVBORw0KGgo=" },
                files: new[] { new OutgoingFileAttachment("a.txt", "data:text/plain;base64,QQ==") })
        },
        { "INTERRUPT", new Dictionary<string, object?> { ["type"] = "INTERRUPT" } },
        { "PONG", new Dictionary<string, object?> { ["type"] = "PONG" } },
        {
            "mode_change",
            new Dictionary<string, object?>
            {
                ["type"] = "mode_change", ["mode"] = "plan", ["turns"] = 3, ["to"] = "0xabc",
            }
        },
        {
            "SESSION_STATUS",
            new Dictionary<string, object?>
            {
                ["type"] = "SESSION_STATUS",
                ["session"] = new Dictionary<string, object?> { ["session_id"] = "s-1" },
                ["to"] = "0xabc",
            }
        },
        {
            "CONNECT resuming a session",
            new Dictionary<string, object?>
            {
                ["type"] = "CONNECT",
                ["payload"] = new Dictionary<string, object?>
                {
                    ["timestamp"] = 1_700_000_000L, ["to"] = "0xabc",
                },
                ["from"] = "0xclient",
                ["signature"] = "sig",
                ["timestamp"] = 1_700_000_000L,
                ["session_id"] = "s-1",
                ["session"] = new Dictionary<string, object?>
                {
                    ["session_id"] = "s-1", ["mode"] = "auto",
                },
                ["last_msg_id"] = "evt-9",
            }
        },
        {
            "ONBOARD_SUBMIT with payment",
            new Dictionary<string, object?>
            {
                ["type"] = "ONBOARD_SUBMIT",
                ["payload"] = new Dictionary<string, object?>
                {
                    ["timestamp"] = 1_700_000_000L, ["payment"] = 2.5,
                },
                ["from"] = "0xclient",
                ["signature"] = "sig",
                ["timestamp"] = 1_700_000_000L,
            }
        },
        { "ask_user answer, single option", new Dictionary<string, object?> { ["answer"] = "Logs" } },
        {
            "ask_user answer, multi-select",
            new Dictionary<string, object?> { ["answer"] = new[] { "Agents", "Status" } }
        },
        {
            "ask_user answer, field form",
            new Dictionary<string, object?> { ["answer"] = """{"username":"bob"}""" }
        },
        {
            "approval approved",
            new Dictionary<string, object?> { ["approved"] = true, ["scope"] = "session" }
        },
        {
            "approval rejected with feedback",
            new Dictionary<string, object?>
            {
                ["approved"] = false,
                ["mode"] = ApprovalRejectModes.Hard,
                ["feedback"] = "don't touch prod",
            }
        },
        { "plan_review response", new Dictionary<string, object?> { ["message"] = "Plan approved" } },
    };

    [Theory]
    [MemberData(nameof(RealFrames))]
    public void RealFrame_MatchesReflectionSerializer(string name, Dictionary<string, object?> frame)
    {
        Assert.Equal(JsonSerializer.Serialize(frame), WireJson.Serialize(frame));
        // Named purely so a failure says which frame broke.
        Assert.False(string.IsNullOrEmpty(name));
    }
}
