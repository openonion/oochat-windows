using System.Text.Json;

namespace ConnectOnion.Protocol.Tests;

/// <summary>
/// Verifies the outgoing INPUT frame shape against the exact schema confirmed in
/// <c>connectonion/docs/network/websocket-protocol.md</c> and the TypeScript SDK's
/// <c>remote-agent.ts:132-135</c>. These are the "text-only / image / file / mixed
/// INPUT matches exact schema" tests called out by the task.
/// </summary>
public class InputMessageBuilderTests
{
    private static JsonElement Serialize(Dictionary<string, object?> msg)
    {
        var json = JsonSerializer.Serialize(msg);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void TextOnly_OmitsImagesAndFiles_UnchangedFromOriginalShape()
    {
        var msg = InputMessageBuilder.BuildInput("hello", "input-1", toAddress: null);
        var root = Serialize(msg);

        Assert.Equal("INPUT", root.GetProperty("type").GetString());
        Assert.Equal("input-1", root.GetProperty("input_id").GetString());
        Assert.Equal("hello", root.GetProperty("prompt").GetString());
        Assert.False(root.TryGetProperty("images", out _));
        Assert.False(root.TryGetProperty("files", out _));
        Assert.False(root.TryGetProperty("to", out _));
    }

    [Fact]
    public void ImageOnly_ImagesArrayIsRawDataUrlStrings()
    {
        var images = new[] { "data:image/png;base64,iVBORw0KGgo=" };
        var msg = InputMessageBuilder.BuildInput("look at this", "input-2", null, images: images);
        var root = Serialize(msg);

        var imagesEl = root.GetProperty("images");
        Assert.Equal(JsonValueKind.Array, imagesEl.ValueKind);
        Assert.Equal(1, imagesEl.GetArrayLength());
        Assert.Equal("data:image/png;base64,iVBORw0KGgo=", imagesEl[0].GetString());
        Assert.False(root.TryGetProperty("files", out _));
    }

    [Fact]
    public void MultipleImages_AllPreservedInOrder()
    {
        var images = new[]
        {
            "data:image/png;base64,AAA=",
            "data:image/jpeg;base64,BBB=",
            "data:image/webp;base64,CCC=",
        };
        var msg = InputMessageBuilder.BuildInput("compare these", "input-3", null, images: images);
        var root = Serialize(msg);

        var imagesEl = root.GetProperty("images");
        Assert.Equal(3, imagesEl.GetArrayLength());
        Assert.Equal("data:image/png;base64,AAA=", imagesEl[0].GetString());
        Assert.Equal("data:image/jpeg;base64,BBB=", imagesEl[1].GetString());
        Assert.Equal("data:image/webp;base64,CCC=", imagesEl[2].GetString());
    }

    [Fact]
    public void FileOnly_FilesArrayHasNameAndDataKeysOnly()
    {
        var files = new[] { new OutgoingFileAttachment("report.pdf", "data:application/pdf;base64,JVBER=") };
        var msg = InputMessageBuilder.BuildInput("read this", "input-4", null, files: files);
        var root = Serialize(msg);

        var filesEl = root.GetProperty("files");
        Assert.Equal(1, filesEl.GetArrayLength());
        var entry = filesEl[0];
        Assert.Equal("report.pdf", entry.GetProperty("name").GetString());
        Assert.Equal("data:application/pdf;base64,JVBER=", entry.GetProperty("data").GetString());

        // Exactly two keys — no invented "mime"/"size"/"kind" fields (server never
        // reads them; agent.py only reads f["name"] and f["data"]).
        var keys = new List<string>();
        foreach (var prop in entry.EnumerateObject()) keys.Add(prop.Name);
        Assert.Equal(new[] { "name", "data" }, keys);
        Assert.False(root.TryGetProperty("images", out _));
    }

    [Fact]
    public void TextPlusMultipleFiles_BothPresent()
    {
        var files = new[]
        {
            new OutgoingFileAttachment("a.txt", "data:text/plain;base64,QQ=="),
            new OutgoingFileAttachment("b.csv", "data:text/csv;base64,Qg=="),
        };
        var msg = InputMessageBuilder.BuildInput("summarize both", "input-5", null, files: files);
        var root = Serialize(msg);

        Assert.Equal("summarize both", root.GetProperty("prompt").GetString());
        Assert.Equal(2, root.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public void TextPlusImagePlusFiles_AllThreePresentTogether()
    {
        var images = new[] { "data:image/png;base64,AAA=" };
        var files = new[] { new OutgoingFileAttachment("notes.md", "data:text/markdown;base64,QQ==") };
        var msg = InputMessageBuilder.BuildInput("here's context", "input-6", null, images: images, files: files);
        var root = Serialize(msg);

        Assert.Equal(1, root.GetProperty("images").GetArrayLength());
        Assert.Equal(1, root.GetProperty("files").GetArrayLength());
        Assert.Equal("here's context", root.GetProperty("prompt").GetString());
    }

    [Fact]
    public void EmptyImagesAndFilesLists_AreOmittedNotSentAsEmptyArrays()
    {
        // Matches the TS SDK: `if (options?.images?.length) msg.images = ...`
        var msg = InputMessageBuilder.BuildInput("hi", "input-7", null,
            images: System.Array.Empty<string>(), files: System.Array.Empty<OutgoingFileAttachment>());
        var root = Serialize(msg);

        Assert.False(root.TryGetProperty("images", out _));
        Assert.False(root.TryGetProperty("files", out _));
    }

    [Fact]
    public void RelayPath_IncludesToAddress_DirectPathDoesNot()
    {
        var relayMsg = Serialize(InputMessageBuilder.BuildInput("hi", "id", toAddress: "0xabc"));
        Assert.Equal("0xabc", relayMsg.GetProperty("to").GetString());

        var directMsg = Serialize(InputMessageBuilder.BuildInput("hi", "id", toAddress: null));
        Assert.False(directMsg.TryGetProperty("to", out _));
    }
}
