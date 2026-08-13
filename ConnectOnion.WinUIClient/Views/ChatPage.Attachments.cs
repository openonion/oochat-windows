using System;
using System.Collections.Generic;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Models;
using Microsoft.UI.Xaml;
using System.IO;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace ConnectOnion.WinUIClient.Views;

/// <summary>
/// <see cref="ChatPage"/>: opening and saving message attachments. Everything here hands off to
/// the shell or a file picker — the page never reads attachment bytes itself, it only ever holds
/// the local cache path the attachment pipeline produced.
/// </summary>
public sealed partial class ChatPage
{
    private async void AttachmentOpen_Click(object sender, RoutedEventArgs e)
        => await OpenAttachmentAsync((sender as FrameworkElement)?.Tag as ChatAttachment);

    /// <summary>Opens a received/sent attachment in its default OS viewer. Best-effort: a
    /// missing or moved cache file must not crash the chat, just silently no-op.</summary>
    private static async System.Threading.Tasks.Task OpenAttachmentAsync(ChatAttachment? attachment)
    {
        if (attachment is null || string.IsNullOrEmpty(attachment.LocalCachePath)) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(attachment.LocalCachePath);
            await Launcher.LaunchFileAsync(file);
        }
        catch
        {
            // Cache file missing/moved/locked — best-effort open only.
        }
    }

    private async void SaveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ChatAttachment attachment ||
            string.IsNullOrEmpty(attachment.LocalCachePath) || App.MainWindow is null)
        {
            return;
        }

        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var ext = Path.GetExtension(attachment.FileName);
        var extLabel = string.IsNullOrEmpty(ext)
            ? LocalizedStrings.Get("AttachmentGenericFileType", "File")
            : ext.TrimStart('.').ToUpperInvariant();
        picker.SuggestedFileName = attachment.FileName;
        picker.FileTypeChoices.Add(extLabel, new List<string> { string.IsNullOrEmpty(ext) ? "." : ext });

        var target = await picker.PickSaveFileAsync();
        if (target is null) return;

        try
        {
            using var source = File.OpenRead(attachment.LocalCachePath);
            using var dest = await target.OpenStreamForWriteAsync();
            await source.CopyToAsync(dest);
        }
        catch
        {
            // Best-effort save; the original attachment stays intact either way.
        }
    }
}
