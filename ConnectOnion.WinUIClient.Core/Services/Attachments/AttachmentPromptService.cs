using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services.Attachments;

/// <summary>
/// Supplies a small, visible prompt for attachment-only submissions. The composer deliberately
/// allows a user to send an image or file without typing, while ConnectOnion hosts still require
/// a non-empty prompt on INPUT. Keeping the fallback here makes that contract explicit and keeps
/// the optimistic user bubble, persisted message, session title, and wire prompt identical.
/// </summary>
public static class AttachmentPromptService
{
    /// <summary>The prompt to actually send. Returns the user's own text whenever there is any —
    /// the generated wording is strictly a fallback for the empty-text-plus-attachment case, and
    /// must never quietly replace or extend something the user typed.</summary>
    /// <remarks>The generated text is phrased as an instruction rather than a placeholder
    /// because the agent reads it as one; wording it "(no message)" would have the model
    /// respond to the absence instead of to the attachment.</remarks>
    public static string Resolve(
        string? prompt,
        IReadOnlyList<PendingAttachment>? attachments)
    {
        var trimmed = prompt?.Trim() ?? "";
        // Note the ordering: no attachments means the empty string passes straight through.
        // Rejecting a genuinely empty submission is the composer's call, not this one's.
        if (trimmed.Length > 0 || attachments is not { Count: > 0 }) return trimmed;

        // Everything that is not an image counts as a file, so a new AttachmentKind needs no
        // change here — it just gets summarized rather than described.
        var imageCount = attachments.Count(attachment => attachment.Kind == AttachmentKind.Image);
        var fileCount = attachments.Count - imageCount;

        if (imageCount > 0 && fileCount > 0)
        {
            return $"Briefly describe the attached {Noun(imageCount, "image")} and summarize the attached {Noun(fileCount, "file")}.";
        }

        if (imageCount > 0)
        {
            return $"Briefly describe the attached {Noun(imageCount, "image")}.";
        }

        return $"Briefly summarize the attached {Noun(fileCount, "file")}.";
    }

    private static string Noun(int count, string singular)
        => count == 1 ? singular : $"{singular}s";
}
