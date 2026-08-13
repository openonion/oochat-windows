using System;
using Microsoft.UI.Xaml.Data;

namespace ConnectOnion.WinUIClient.Common;

public sealed class InteractiveTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var text = value as string ?? "";
        var key = text switch
        {
            "Response submitted" => "InteractiveResponseSubmitted",
            "Response sent to the agent" => "InteractiveResponseSent",
            "Request rejected" => "InteractiveRequestRejected",
            "Request closed" => "InteractiveRequestClosed",
            "No response was submitted" => "InteractiveNoResponse",
            "Change result could not be confirmed" => "DiffUnconfirmedTitle",
            "Changes could not be applied" => "DiffFailedTitle",
            "Changes may be partially applied" => "DiffPartialTitle",
            "Connection lost while applying changes" => "DiffDisconnectedTitle",
            "Applying file changes" => "DiffApplyingTitle",
            "Review file changes" => "DiffReviewTitle",
            "Proposed changes rejected" => "DiffRejectedTitle",
            "View changes" => "DiffViewChanges",
            "View proposed changes" => "DiffViewProposedChanges",
            "Hide changes" => "DiffHideChanges",
            "Wrap" => "DiffWrap",
            "No wrap" => "DiffNoWrap",
            _ => null,
        };

        if (key is not null)
            return LocalizedStrings.Get(key, text);

        const string appliedPrefix = "Changes applied to ";
        if (text.StartsWith(appliedPrefix, StringComparison.Ordinal))
            return LocalizedStrings.Format(
                "DiffAppliedTitle",
                "Changes applied to {0}",
                text[appliedPrefix.Length..]);

        return text;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
