namespace ConnectOnion.WinUIClient.Services.Speech;

/// <summary>Combines a completed cloud transcript with the draft already in the composer.</summary>
public static class VoiceTranscript
{
    /// <summary>Appends speech with one separating space while preserving the user's draft.</summary>
    public static string Append(string? existingText, string? transcript)
        => Insert(existingText, transcript, existingText?.Length ?? 0, 0).Text.TrimEnd();

    /// <summary>Replaces the current selection with speech and returns the new caret position.</summary>
    public static VoiceTranscriptInsertion Insert(
        string? existingText,
        string? transcript,
        int selectionStart,
        int selectionLength)
    {
        var existing = existingText ?? "";
        var start = Math.Clamp(selectionStart, 0, existing.Length);
        var length = Math.Clamp(selectionLength, 0, existing.Length - start);
        var spoken = transcript?.Trim() ?? "";
        if (spoken.Length == 0) return new VoiceTranscriptInsertion(existing, start + length);

        var prefix = existing[..start];
        var suffix = existing[(start + length)..];
        var leftSeparator = NeedsSeparator(prefix.LastOrDefault(), spoken[0]) ? " " : "";
        var rightSeparator = NeedsSeparator(spoken[^1], suffix.FirstOrDefault()) ? " " : "";
        var text = string.Concat(prefix, leftSeparator, spoken, rightSeparator, suffix);
        return new VoiceTranscriptInsertion(
            text,
            prefix.Length + leftSeparator.Length + spoken.Length + rightSeparator.Length);
    }

    private static bool NeedsSeparator(char left, char right)
    {
        if (left == default || right == default || char.IsWhiteSpace(left) || char.IsWhiteSpace(right))
            return false;
        if (IsCjk(left) || IsCjk(right) || IsOpeningPunctuation(left) || IsClosingPunctuation(right))
            return false;
        return true;
    }

    private static bool IsCjk(char value)
        => value is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF';

    private static bool IsOpeningPunctuation(char value) => "([{（【《“‘".Contains(value);
    private static bool IsClosingPunctuation(char value) => ".,!?;:)]}，。！？；：、）】》”’".Contains(value);
}

public sealed record VoiceTranscriptInsertion(string Text, int CaretPosition);
