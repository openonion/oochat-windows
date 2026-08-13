using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Last-resort startup error surface. It deliberately uses a native message box because the
/// XAML window, dependency container, resource loader, or data root may be the thing that failed.
/// </summary>
internal static class StartupFailurePresenter
{
    private const uint Ok = 0x00000000;
    private const uint YesNo = 0x00000004;
    private const uint IconError = 0x00000010;
    private const uint SetForeground = 0x00010000;
    private const int Yes = 6;

    public static void Show(Exception exception, string? logDirectory)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var chinese = string.Equals(
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            "zh",
            StringComparison.OrdinalIgnoreCase);
        var detail = RootMessage(exception);
        var hasLogs = !string.IsNullOrWhiteSpace(logDirectory) && Directory.Exists(logDirectory);
        var title = chinese ? "ConnectOnion 启动失败" : "ConnectOnion couldn't start";
        var message = chinese
            ? $"ConnectOnion 无法完成启动。\n\n{detail}\n\n"
              + (hasLogs
                  ? $"日志目录：{logDirectory}\n\n是否打开日志目录？"
                  : "请检查数据目录权限，然后重试。")
            : $"ConnectOnion could not finish starting.\n\n{detail}\n\n"
              + (hasLogs
                  ? $"Logs: {logDirectory}\n\nOpen the logs folder?"
                  : "Check the data-directory permissions, then try again.");

        var result = MessageBox(IntPtr.Zero, message, title, IconError | SetForeground | (hasLogs ? YesNo : Ok));
        if (result != Yes || !hasLogs) return;

        try
        {
            _ = Process.Start(new ProcessStartInfo(logDirectory!) { UseShellExecute = true });
        }
        catch
        {
            // The recovery surface has already shown the actionable path. Explorer failing to
            // open it must never replace the original startup error with a second crash.
        }
    }

    private static string RootMessage(Exception exception)
    {
        while (exception.InnerException is not null) exception = exception.InnerException;
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message.Trim();
        return message.Length <= 600 ? message : message[..600] + "…";
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBox(IntPtr windowHandle, string text, string caption, uint type);
}
