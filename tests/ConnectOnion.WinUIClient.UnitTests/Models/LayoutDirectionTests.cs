using ConnectOnion.WinUIClient.Common;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

public sealed class LayoutDirectionTests
{
    [Theory]
    [InlineData("ar")]
    [InlineData("ar-SA")]
    [InlineData("he-IL")]
    [InlineData("fa-IR")]
    [InlineData("ur-PK")]
    public void RightToLeftLocales_AreDetected(string tag)
        => Assert.True(LayoutDirection.IsRightToLeft(tag));

    /// <summary>Both languages the app ships today. They are left-to-right, so the mirroring path
    /// is inert — which is the point of testing it: the direction is derived from the locale,
    /// not assumed, so adding a locale to <c>Strings/</c> needs no second change here.</summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    [InlineData("ja-JP")]
    [InlineData("de-DE")]
    public void LeftToRightLocales_AreNotMirrored(string tag)
        => Assert.False(LayoutDirection.IsRightToLeft(tag));

    /// <summary>A missing or unparseable tag must not mirror the whole shell.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-locale-tag")]
    public void UnknownOrMissingTag_FallsBackToLeftToRight(string? tag)
        => Assert.False(LayoutDirection.IsRightToLeft(tag));
}
