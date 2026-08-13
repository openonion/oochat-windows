using ConnectOnion.WinUIClient.Services.Speech;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class VoiceTranscriptTests
{
    [Theory]
    [InlineData("", "spoken", "spoken")]
    [InlineData("typed", "", "typed")]
    [InlineData("typed", "spoken", "typed spoken")]
    [InlineData("typed ", " spoken", "typed spoken")]
    [InlineData("", "", "")]
    [InlineData("typed", "   ", "typed")]
    public void Append_PreservesTheDraftAndUsesOneSeparatingSpace(
        string existing,
        string transcript,
        string expected)
        => Assert.Equal(expected, VoiceTranscript.Append(existing, transcript));

    [Theory]
    [InlineData("hello world", "there", 6, 5, "hello there", 11)]
    [InlineData("hello world", "new", 6, 0, "hello new world", 10)]
    [InlineData("hello,", "world", 6, 0, "hello, world", 12)]
    [InlineData("hello", ", world", 5, 0, "hello, world", 12)]
    [InlineData("你好世界", "美丽", 2, 0, "你好美丽世界", 4)]
    public void Insert_ReplacesTheSelectionAndKeepsNaturalWordBoundaries(
        string existing,
        string transcript,
        int selectionStart,
        int selectionLength,
        string expected,
        int expectedCaret)
    {
        var result = VoiceTranscript.Insert(existing, transcript, selectionStart, selectionLength);

        Assert.Equal(expected, result.Text);
        Assert.Equal(expectedCaret, result.CaretPosition);
    }
}
