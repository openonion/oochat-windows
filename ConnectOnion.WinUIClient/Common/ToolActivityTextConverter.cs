using System;
using Microsoft.UI.Xaml.Data;

namespace ConnectOnion.WinUIClient.Common;

public sealed class ToolActivityTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var text = value as string ?? "";
        var key = text switch
        {
            "Tool activity" => "ToolActivityTitle",
            "Running tools…" => "ToolActivityRunning",
            "Done" => "ToolActivityDone",
            "Completed with warnings" => "ToolActivityWarnings",
            "Failed" => "ToolActivityFailed",
            "Cancelled" => "ToolActivityCancelled",
            "Waiting for approval" => "ToolActivityWaitingApproval",
            "Working..." => "ToolActivityWorking",
            "One fixture step failed" => "ToolActivityFixtureStepFailed",
            "Result" => "ToolActivityResult",
            "Error" => "ToolActivityError",
            "Copy" => "CommonCopy",
            "Copied" => "CommonCopied",
            "Expand" => "CommonExpand",
            "Collapse" => "CommonCollapse",
            "Command" => "ToolInvocationCommand",
            "Path" => "ToolInvocationPath",
            "Pattern" => "ToolInvocationPattern",
            "URL" => "ToolInvocationUrl",
            "Target" => "ToolInvocationTarget",
            "Task" => "ToolInvocationTask",
            "Query" => "ToolInvocationQuery",
            "Input" => "ToolInvocationInput",
            _ => null,
        };
        return key is null ? text : LocalizedStrings.Get(key, text);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
