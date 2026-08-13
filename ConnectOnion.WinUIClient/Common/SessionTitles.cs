using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Common;

/// <summary>
/// Localized wording for conversation titles the app generates for itself.
///
/// <see cref="SessionSummary"/> lives in Core and has no resource map, so the placeholder's
/// wording is passed in from here rather than baked into the model. Whether a title is *still*
/// a placeholder is answered by <see cref="SessionSummary.HasCustomTitle"/>, never by matching
/// this text — that is the whole reason the flag exists (see schema migration v7).
/// </summary>
public static class SessionTitles
{
    /// <summary>Composite format for a new conversation's placeholder title; <c>{0}</c> is the
    /// conversation's number within its agent.</summary>
    public static string PlaceholderFormat
        => LocalizedStrings.Get("SessionPlaceholderTitleFormat", SessionSummary.DefaultTitleFormat);
}
