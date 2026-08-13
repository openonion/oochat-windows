using System;
using ConnectOnion.WinUIClient.Models;
using FluentIcons.Common;
using Microsoft.UI.Xaml.Data;

namespace ConnectOnion.WinUIClient.Presentation;

/// <summary>
/// A tool-activity or tool-step status as a monochrome FluentIcons glyph.
///
/// <para><b>Why this replaced the literal characters.</b> The card used to print
/// <see cref="ToolActivityViewModel.StatusGlyph"/> — "✓", "⚠", "✕" — into a TextBlock. Most of
/// those are ordinary text and take the brush they are given, but <c>⚠</c> (U+26A0) has
/// <i>emoji</i> presentation by default on Windows: it resolves through Segoe UI Emoji and paints
/// itself in the font's own orange, ignoring <c>Foreground</c> entirely. The result was a warning
/// row whose triangle stayed a fixed orange while the text beside it followed the palette — and it
/// could not be themed, muted, or turned red. <c>⚿</c> (U+26BF, waiting-for-permission) is rare
/// enough to risk falling back to tofu for the same reason.</para>
///
/// <para>FluentIcons glyphs carry no colour of their own, so every state here is tinted by
/// <see cref="ToolStatusToBrushConverter"/> exactly like any other foreground. Pair the two on the
/// same element.</para>
///
/// <para>Takes both status enums for the same reason the brush converter does: the activity
/// footer and the step rows are styled by one set of rules.</para>
/// </summary>
public sealed class ToolStatusToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        ToolActivityStatus.Success or ToolStepStatus.Success => Icon.Checkmark,
        ToolActivityStatus.Failed or ToolStepStatus.Failed => Icon.Dismiss,
        ToolActivityStatus.PartialSuccess or ToolStepStatus.Warning => Icon.Warning,
        ToolActivityStatus.Cancelled or ToolStepStatus.Cancelled => Icon.Prohibited,
        ToolActivityStatus.WaitingForConfirmation => Icon.Question,
        ToolActivityStatus.WaitingForPermission => Icon.LockClosed,
        // Running/Pending, and anything a future enum member adds: a hollow ring, which is what
        // the old "◌" was reaching for.
        _ => Icon.CircleHint,
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
