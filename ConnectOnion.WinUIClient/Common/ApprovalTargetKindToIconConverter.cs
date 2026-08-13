using System;
using ConnectOnion.WinUIClient.Models;
using FluentIcons.Common;
using Microsoft.UI.Xaml.Data;

namespace ConnectOnion.WinUIClient.Presentation;

/// <summary>
/// The glyph for an approval's target chip, chosen from what the tool is acting on — a file, a
/// folder, a command, a URL, or free text — so the chip reads at a glance without the icon being
/// the <i>only</i> signal (the target name sits right beside it, and a tooltip carries the full
/// value). Kept off the "everything is a file" default the mockup warned against.
///
/// <para>FluentIcons glyphs carry no colour of their own; the chip tints this neutral. All names
/// resolve at Size16 (the small font files ship far fewer glyphs — see the repo notes), which is
/// why the chip requests Size16.</para>
/// </summary>
public sealed class ApprovalTargetKindToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        ApprovalTargetKind.File => Icon.DocumentText,
        ApprovalTargetKind.Directory => Icon.Folder,
        ApprovalTargetKind.Command => Icon.Code,
        ApprovalTargetKind.Url => Icon.Globe,
        ApprovalTargetKind.Text => Icon.Chat,
        _ => Icon.DocumentText,
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
