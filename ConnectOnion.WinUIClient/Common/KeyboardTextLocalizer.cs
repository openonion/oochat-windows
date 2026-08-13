using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Data;

namespace ConnectOnion.WinUIClient.Common;

public static class KeyboardTextLocalizer
{
    private static readonly IReadOnlyDictionary<string, string> Keys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["File"] = "KeyboardGroupFile",
            ["Edit"] = "KeyboardGroupEdit",
            ["View"] = "KeyboardGroupView",
            ["Chat"] = "KeyboardGroupChat",
            ["Find"] = "KeyboardGroupFind",
            ["General"] = "KeyboardGroupGeneral",
            ["New chat"] = "KeyboardNewChat",
            ["Open folder"] = "KeyboardOpenFolder",
            ["Open settings"] = "KeyboardOpenSettings",
            ["Close window"] = "KeyboardCloseWindow",
            ["Exit application"] = "KeyboardExit",
            ["Undo"] = "KeyboardUndo",
            ["Redo"] = "KeyboardRedo",
            ["Cut"] = "KeyboardCut",
            ["Copy"] = "KeyboardCopy",
            ["Paste"] = "KeyboardPaste",
            ["Select all"] = "KeyboardSelectAll",
            ["Toggle sidebar"] = "KeyboardToggleSidebar",
            ["Open terminal"] = "KeyboardOpenTerminal",
            ["Go back"] = "KeyboardGoBack",
            ["Go forward"] = "KeyboardGoForward",
            ["Zoom in"] = "KeyboardZoomIn",
            ["Zoom out"] = "KeyboardZoomOut",
            ["Toggle full screen"] = "KeyboardToggleFullScreen",
            ["Send message"] = "KeyboardSendMessage",
            ["Cycle approval mode"] = "KeyboardCycleChatMode",
            ["Go to pending decision"] = "KeyboardGoToPendingDecision",
            ["Insert new line"] = "KeyboardInsertNewLine",
            ["Next match"] = "KeyboardNextMatch",
            ["Previous match"] = "KeyboardPreviousMatch",
            ["Close find"] = "KeyboardCloseFind",
            ["Keyboard shortcuts"] = "KeyboardShortcutsName",
            ["Close dialog"] = "KeyboardCloseDialog",
            ["Handled by the system text box"] = "KeyboardReasonSystemTextBox",
            ["Set by the Enter key preference"] = "KeyboardReasonEnterPreference",
            ["Fixed while this surface has focus"] = "KeyboardReasonContextual",
            ["Fixed so a dialog can always be dismissed"] = "KeyboardReasonDismiss",
        };

    public static string Localize(string? value)
        => value is not null && Keys.TryGetValue(value, out var key)
            ? LocalizedStrings.Get(key, value)
            : value ?? "";
}

public sealed class KeyboardTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => KeyboardTextLocalizer.Localize(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
