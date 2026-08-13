using System;
using System.IO;
using ConnectOnion.WinUIClient.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace ConnectOnion.WinUIClient.Controls;

public sealed partial class DiffPreviewCard : UserControl
{
    public DiffPreviewCard() => InitializeComponent();
    public ChatMessage Message { get => (ChatMessage)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(ChatMessage), typeof(DiffPreviewCard),
        new PropertyMetadata(null, OnMessageChanged));
    private void FileHeader_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is DiffFileModel file) file.IsExpanded = !file.IsExpanded; }
    private void ToggleExpanded_Click(object sender, RoutedEventArgs e) => Message.ToggleDiffExpanded();
    private void ToggleWrap_Click(object sender, RoutedEventArgs e)
    {
        Message.ToggleDiffWrap();
        ApplyWrapMode(Message.IsDiffWrapped);
    }
    private void CopyDiff_Click(object sender, RoutedEventArgs e)
        => CopyText(Message.DiffPreview.RawText);

    private void CopyPath_Click(object sender, RoutedEventArgs e)
        => CopyText(Message.EventTitle);

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(Message.EventTitle)) return;
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(Message.EventTitle));
        await Launcher.LaunchFileAsync(file);
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static void OnMessageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not DiffPreviewCard card) return;

        var message = args.NewValue as ChatMessage;
        if (card.OpenFileButton is not null)
        {
            card.OpenFileButton.Visibility = message is not null
                && Path.IsPathFullyQualified(message.EventTitle)
                && File.Exists(message.EventTitle)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
        card.ApplyWrapMode(message?.IsDiffWrapped == true);
    }

    private void ApplyWrapMode(bool isWrapped)
    {
        if (DiffScrollViewer is null) return;

        // A horizontally enabled ScrollViewer measures its content at infinite width, so merely
        // setting TextWrapping=Wrap cannot wrap anything. Disable horizontal scrolling while
        // wrapped to give the diff line grid the finite viewport width it needs.
        DiffScrollViewer.HorizontalScrollMode = isWrapped
            ? ScrollMode.Disabled
            : ScrollMode.Enabled;
        DiffScrollViewer.HorizontalScrollBarVisibility = isWrapped
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
    }
}
