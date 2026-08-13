using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ConnectOnion.PortableLauncher;

internal static partial class Program
{
    private const string RelativeApplicationPath = @"app\ConnectOnion.WinUIClient.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        var applicationPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, RelativeApplicationPath));
        if (!File.Exists(applicationPath))
        {
            ShowError("The application files are missing. Extract the complete ZIP before running ConnectOnion Desktop.");
            return 1;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = applicationPath,
                WorkingDirectory = Path.GetDirectoryName(applicationPath)!,
                UseShellExecute = false,
            };
            foreach (var argument in args)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return Process.Start(startInfo) is null ? 1 : 0;
        }
        catch (Exception exception)
        {
            ShowError($"ConnectOnion Desktop could not start.\n\n{exception.Message}");
            return 1;
        }
    }

    private static void ShowError(string message)
        => MessageBoxW(nint.Zero, message, "ConnectOnion Desktop", 0x10);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint window, string text, string caption, uint type);
}
