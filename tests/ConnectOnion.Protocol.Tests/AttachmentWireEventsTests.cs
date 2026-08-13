using System.Text.Json;

namespace ConnectOnion.Protocol.Tests;

/// <summary>
/// Receive-side tests: the two real attachment wire events (<c>agent_image</c>,
/// <c>files_received</c>) plus the "malformed payload / unknown event never
/// crashes the receive loop" guarantees the task requires.
/// </summary>
public class AttachmentWireEventsTests
{
    [Fact]
    public void TryGetAgentImageDataUrl_ParsesImageField()
    {
        var msg = WireMessage.Parse("""{"type":"agent_image","image":"data:image/png;base64,AAAA"}""");

        var ok = AttachmentWireEvents.TryGetAgentImageDataUrl(msg, out var dataUrl);

        Assert.True(ok);
        Assert.Equal("data:image/png;base64,AAAA", dataUrl);
    }

    [Fact]
    public void TryGetAgentImageDataUrl_WrongEventType_ReturnsFalse()
    {
        var msg = WireMessage.Parse("""{"type":"tool_call","name":"read_file"}""");

        Assert.False(AttachmentWireEvents.TryGetAgentImageDataUrl(msg, out var dataUrl));
        Assert.Equal("", dataUrl);
    }

    [Fact]
    public void TryGetAgentImageDataUrl_MissingImageField_ReturnsFalse()
    {
        var msg = WireMessage.Parse("""{"type":"agent_image"}""");
        Assert.False(AttachmentWireEvents.TryGetAgentImageDataUrl(msg, out _));
    }

    [Fact]
    public void TryGetAgentImageDataUrl_ImageFieldWrongJsonType_ReturnsFalseNotThrow()
    {
        var msg = WireMessage.Parse("""{"type":"agent_image","image":12345}""");
        Assert.False(AttachmentWireEvents.TryGetAgentImageDataUrl(msg, out _));
    }

    [Fact]
    public void TryGetFilesReceived_ParsesNameAndPath()
    {
        var msg = WireMessage.Parse("""
            {"type":"files_received","files":[{"name":"report.pdf","path":"/srv/.co/uploads/1_report.pdf"}]}
            """);

        var ok = AttachmentWireEvents.TryGetFilesReceived(msg, out var files);

        Assert.True(ok);
        Assert.Single(files);
        Assert.Equal("report.pdf", files[0].Name);
        Assert.Equal("/srv/.co/uploads/1_report.pdf", files[0].Path);
    }

    [Fact]
    public void TryGetFilesReceived_MultipleFiles_PreservesOrder()
    {
        var msg = WireMessage.Parse("""
            {"type":"files_received","files":[{"name":"a.txt","path":"/x/a.txt"},{"name":"b.txt","path":"/x/b.txt"}]}
            """);

        AttachmentWireEvents.TryGetFilesReceived(msg, out var files);

        Assert.Equal(2, files.Count);
        Assert.Equal("a.txt", files[0].Name);
        Assert.Equal("b.txt", files[1].Name);
    }

    [Fact]
    public void TryGetFilesReceived_SkipsMalformedEntriesWithoutFailingWholeEvent()
    {
        var msg = WireMessage.Parse("""
            {"type":"files_received","files":[{"path":"/x/no-name.txt"},"not-an-object",{"name":"ok.txt","path":"/x/ok.txt"}]}
            """);

        var ok = AttachmentWireEvents.TryGetFilesReceived(msg, out var files);

        Assert.True(ok);
        Assert.Single(files);
        Assert.Equal("ok.txt", files[0].Name);
    }

    [Fact]
    public void TryGetFilesReceived_MissingPath_DefaultsToEmptyStringNotNull()
    {
        var msg = WireMessage.Parse("""{"type":"files_received","files":[{"name":"ok.txt"}]}""");
        AttachmentWireEvents.TryGetFilesReceived(msg, out var files);

        Assert.Single(files);
        Assert.Equal("", files[0].Path);
    }

    [Fact]
    public void TryGetFilesReceived_NotAnArray_ReturnsFalse()
    {
        var msg = WireMessage.Parse("""{"type":"files_received","files":"oops"}""");
        Assert.False(AttachmentWireEvents.TryGetFilesReceived(msg, out var files));
        Assert.Empty(files);
    }

    [Fact]
    public void TryGetFilesReceived_WrongEventType_ReturnsFalse()
    {
        var msg = WireMessage.Parse("""{"type":"agent_image","image":"data:image/png;base64,AAAA"}""");
        Assert.False(AttachmentWireEvents.TryGetFilesReceived(msg, out _));
    }

    [Fact]
    public void UnknownEventType_BothParsersReturnFalse_NeverThrow()
    {
        var msg = WireMessage.Parse("""{"type":"some_future_event","payload":{"nested":[1,2,3]}}""");

        var imageOk = AttachmentWireEvents.TryGetAgentImageDataUrl(msg, out _);
        var filesOk = AttachmentWireEvents.TryGetFilesReceived(msg, out _);

        Assert.False(imageOk);
        Assert.False(filesOk);
    }

    [Fact]
    public void MalformedJsonFrame_WireMessageParseFailure_IsCatchableAndDoesNotCorruptState()
    {
        // Mirrors AgentConnectionService.HandleMessageAsync's `try { WireMessage.Parse(json) } catch { return; }`
        // guard around every inbound frame — a single malformed frame must be a
        // no-op, not a crash of the receive loop.
        var threw = false;
        try
        {
            WireMessage.Parse("{not valid json");
        }
        catch (JsonException)
        {
            threw = true;
        }

        Assert.True(threw, "WireMessage.Parse is expected to throw JsonException on malformed input; callers must catch it (as AgentConnectionService already does)");
    }
}
