using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Small wrapper over the WinRT clipboard so the
/// <c>new DataPackage()</c> → <c>SetText</c> → <c>Clipboard.SetContent</c>
/// dance lives in one place instead of being copy-pasted per call site.
/// </summary>
public static class ClipboardService
{
    private static readonly object SensitiveCopyGate = new();
    private static CancellationTokenSource? _sensitiveClearCancellation;

    public static void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    /// <summary>Copies a secret without history/roaming, then clears it after a short interval if
    /// it is still the value this app placed there.</summary>
    public static void CopySensitiveText(string? text, TimeSpan? clearAfter = null)
    {
        if (string.IsNullOrEmpty(text)) return;

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContentWithOptions(package, new ClipboardContentOptions
        {
            IsAllowedInHistory = false,
            IsRoamable = false,
        });
        Clipboard.Flush();

        CancellationTokenSource cancellation;
        lock (SensitiveCopyGate)
        {
            _sensitiveClearCancellation?.Cancel();
            _sensitiveClearCancellation?.Dispose();
            cancellation = _sensitiveClearCancellation = new CancellationTokenSource();
        }

        _ = ClearSensitiveTextLaterAsync(text, clearAfter ?? TimeSpan.FromSeconds(60), cancellation.Token);
    }

    private static async Task ClearSensitiveTextLaterAsync(
        string copiedText,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;

            var currentText = await content.GetTextAsync();
            if (string.Equals(currentText, copiedText, StringComparison.Ordinal))
                Clipboard.Clear();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Clipboard ownership can change between copy and expiry. Cleanup is best effort.
        }
    }
}
