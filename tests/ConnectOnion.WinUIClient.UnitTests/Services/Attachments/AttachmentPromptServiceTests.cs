using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Attachments;

namespace ConnectOnion.WinUIClient.UnitTests.Services.Attachments;

public sealed class AttachmentPromptServiceTests
{
    [Fact]
    public void Resolve_UserPrompt_PreservesTrimmedPrompt()
    {
        var attachments = new[] { Attachment(AttachmentKind.Image) };

        var result = AttachmentPromptService.Resolve("  Inspect the label  ", attachments);

        Assert.Equal("Inspect the label", result);
    }

    [Fact]
    public void Resolve_NoPromptOrAttachments_ReturnsEmpty()
    {
        Assert.Equal("", AttachmentPromptService.Resolve("  ", null));
    }

    [Theory]
    [InlineData(AttachmentKind.Image, 1, "Briefly describe the attached image.")]
    [InlineData(AttachmentKind.Image, 2, "Briefly describe the attached images.")]
    [InlineData(AttachmentKind.File, 1, "Briefly summarize the attached file.")]
    [InlineData(AttachmentKind.File, 2, "Briefly summarize the attached files.")]
    public void Resolve_SingleAttachmentKind_GeneratesDescription(
        AttachmentKind kind,
        int count,
        string expected)
    {
        var attachments = Enumerable.Range(0, count).Select(_ => Attachment(kind)).ToArray();

        var result = AttachmentPromptService.Resolve("", attachments);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_MixedAttachments_DescribesImagesAndSummarizesFiles()
    {
        var attachments = new[]
        {
            Attachment(AttachmentKind.Image),
            Attachment(AttachmentKind.Image),
            Attachment(AttachmentKind.File),
        };

        var result = AttachmentPromptService.Resolve(null, attachments);

        Assert.Equal(
            "Briefly describe the attached images and summarize the attached file.",
            result);
    }

    private static PendingAttachment Attachment(AttachmentKind kind) => new() { Kind = kind };
}
