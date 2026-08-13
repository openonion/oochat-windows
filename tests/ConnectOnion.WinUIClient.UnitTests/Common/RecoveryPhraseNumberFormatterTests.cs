using System.Globalization;
using ConnectOnion.WinUIClient.Common;

namespace ConnectOnion.WinUIClient.UnitTests.Common;

public sealed class RecoveryPhraseNumberFormatterTests
{
    [Theory]
    [InlineData("en-US", 12, "12")]
    [InlineData("ar-EG", 12, "12")]
    public void Format_UsesReviewedActiveCulture(string cultureName, int position, string expected)
    {
        var actual = RecoveryPhraseNumberFormatter.Format(
            position,
            CultureInfo.GetCultureInfo(cultureName));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Format_RejectsNonPositivePositions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RecoveryPhraseNumberFormatter.Format(0, CultureInfo.InvariantCulture));
    }
}
