using System;
using ConnectOnion.WinUIClient.Models;
using FluentIcons.Common;
using Microsoft.UI.Xaml.Data;

namespace ConnectOnion.WinUIClient.Presentation;

/// <summary>
/// The app half of the tool-icon mapping: <see cref="ToolIconKind"/> → a FluentIcons glyph.
///
/// <para>Kept apart from <see cref="ToolIcons"/> so the interesting decision — which tool reads as
/// which kind of action — stays in Core where it is unit-tested, while the part that needs a
/// WinUI-only enum stays here. Adding a tool means editing the Core table; this file only changes
/// when a whole new <i>category</i> of tool appears.</para>
///
/// <para>Every glyph below was checked against the shipped enum (2914 members) rather than
/// guessed. Two substitutions are deliberate: FluentIcons has no <c>Terminal</c>, so a shell step
/// uses <c>WindowDevTools</c>, and no <c>Download</c>/<c>Upload</c>, so transfers use the
/// <c>Arrow*</c> spellings. Render these at <c>Size16</c> — <c>Size12</c> ships 439 of 4372
/// glyphs and several of these have no 12px design, which renders as a silent blank.</para>
/// </summary>
public sealed class ToolIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is ToolIconKind kind ? Glyph(kind) : Icon.Wrench;

    /// <summary>Total over the enum: an unmapped future member falls to the same neutral wrench
    /// <see cref="ToolIconKind.Generic"/> uses, rather than throwing inside a DataTemplate where
    /// the exception would surface as an unexplained blank row.</summary>
    private static Icon Glyph(ToolIconKind kind) => kind switch
    {
        ToolIconKind.Browser => Icon.Globe,
        ToolIconKind.Search => Icon.Search,
        ToolIconKind.FileRead => Icon.Document,
        ToolIconKind.FileWrite => Icon.DocumentEdit,
        ToolIconKind.FileDelete => Icon.Delete,
        ToolIconKind.Folder => Icon.Folder,
        // No Terminal glyph exists; DevTools is the closest "a console ran something" reading.
        ToolIconKind.Terminal => Icon.WindowDevTools,
        ToolIconKind.Code => Icon.Code,
        ToolIconKind.Mail => Icon.Mail,
        ToolIconKind.Image => Icon.Image,
        ToolIconKind.Screenshot => Icon.Screenshot,
        ToolIconKind.Download => Icon.ArrowDownload,
        ToolIconKind.Upload => Icon.ArrowUpload,
        ToolIconKind.Database => Icon.Database,
        ToolIconKind.Http => Icon.Link,
        ToolIconKind.Ask => Icon.ChatBubblesQuestion,
        ToolIconKind.Click => Icon.CursorClick,
        ToolIconKind.Type => Icon.Keyboard,
        _ => Icon.Wrench,
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
