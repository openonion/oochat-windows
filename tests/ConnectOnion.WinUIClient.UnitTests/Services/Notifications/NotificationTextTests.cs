using ConnectOnion.WinUIClient.Services.Notifications;

namespace ConnectOnion.WinUIClient.UnitTests.Services.Notifications;

public sealed class NotificationTextTests
{
    [Fact]
    public void Preview_MarkdownContent_StripsFormattingAndCollapsesWhitespace()
    {
        var preview = NotificationText.Preview("# Result\n**bold** [link](https://example.com) `code`");

        Assert.Equal("Result bold link code", preview);
    }

    [Fact]
    public void Preview_FencedCode_ReplacesCodeWithPlaceholder()
    {
        var preview = NotificationText.Preview("Before\n```json\n{ \"secret\": true }\n```\nAfter");

        Assert.Equal("Before [code] After", preview);
    }

    [Fact]
    public void Preview_OverLimit_TruncatesToLimitAndAddsEllipsis()
    {
        var preview = NotificationText.Preview("abcdefghij", max: 5);

        Assert.Equal("abcde\u2026", preview);
    }
}
