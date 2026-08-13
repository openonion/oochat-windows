using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

public sealed class PendingAttachmentTests
{
    [Theory]
    [InlineData(AttachmentStatus.Pending, false, false, false)]
    [InlineData(AttachmentStatus.Encoding, true, false, false)]
    [InlineData(AttachmentStatus.Ready, false, true, false)]
    [InlineData(AttachmentStatus.Sending, true, false, false)]
    [InlineData(AttachmentStatus.Sent, false, false, false)]
    [InlineData(AttachmentStatus.Failed, false, false, true)]
    public void Status_DerivedFlagsMatchLifecycle(
        AttachmentStatus status,
        bool isBusy,
        bool isReady,
        bool isFailed)
    {
        var attachment = new PendingAttachment { Status = status };

        Assert.Equal(isBusy, attachment.IsBusy);
        Assert.Equal(isReady, attachment.IsReady);
        Assert.Equal(isFailed, attachment.IsFailed);
    }

    [Fact]
    public void Status_WhenChanged_RaisesAllDependentPropertyNotifications()
    {
        var attachment = new PendingAttachment();
        var changed = new List<string?>();
        attachment.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        attachment.Status = AttachmentStatus.Encoding;

        Assert.Equal(
            [
                nameof(PendingAttachment.Status),
                nameof(PendingAttachment.IsFailed),
                nameof(PendingAttachment.IsBusy),
                nameof(PendingAttachment.IsReady),
            ],
            changed);
    }

    [Fact]
    public void Error_WhenCleared_UpdatesHasError()
    {
        var attachment = new PendingAttachment { Error = "too large" };
        var changed = new List<string?>();
        attachment.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        attachment.Error = null;

        Assert.False(attachment.HasError);
        Assert.Equal([nameof(PendingAttachment.Error), nameof(PendingAttachment.HasError)], changed);
    }

    [Theory]
    [InlineData("briefing.pptx", 2048, "PPTX · 2 KB")]
    [InlineData("README", 512, "512 B")]
    [InlineData("archive.tar.gz", 0, "GZ")]
    public void MetadataLabel_CombinesExtensionAndSize(string fileName, long sizeBytes, string expected)
    {
        var attachment = new PendingAttachment { FileName = fileName, SizeBytes = sizeBytes };

        Assert.Equal(expected, attachment.MetadataLabel);
    }
}
