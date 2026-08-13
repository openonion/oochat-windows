using System;
using System.IO;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;

namespace ConnectOnion.WinUIClient.Controls;

public sealed partial class AgentShareDialog : ContentDialog
{
    private readonly string _address;
    private readonly Uri _webUri;

    public AgentShareDialog(string address)
    {
        _address = address;
        _webUri = new Uri($"https://chat.openonion.ai/{Uri.EscapeDataString(address)}");
        InitializeComponent();
        AddressText.Text = address;
        WebUrlText.Text = _webUri.AbsoluteUri;
        Opened += OnOpened;
    }

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        Opened -= OnOpened;
        try
        {
            using var qrData = QRCodeGenerator.GenerateQrCode(
                _webUri.AbsoluteUri, QRCodeGenerator.ECCLevel.Q);
            using var qr = new PngByteQRCode(qrData);
            var pngBytes = qr.GetGraphic(8);

            // MemoryStream.AsRandomAccessStream keeps ownership with this scope. The former
            // DataWriter path disposed its underlying InMemoryRandomAccessStream before
            // BitmapImage read it, leaving the dialog with a permanently blank image.
            await using var memory = new MemoryStream(pngBytes, writable: false);
            using var stream = memory.AsRandomAccessStream();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            QrImage.Source = bitmap;
        }
        catch
        {
            QrErrorText.Text = LocalizedStrings.Get(
                "AgentShareQrError",
                "The QR code could not be generated. Copy the web link instead.");
            QrErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            QrProgress.IsActive = false;
            QrProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void OpenWebChat_Click(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        if (await Services.AppServices.UriLauncher.LaunchAsync(_webUri)) return;
        args.Cancel = true;
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var value = (sender as FrameworkElement)?.Tag as string == "address"
            ? _address
            : _webUri.AbsoluteUri;
        Services.ClipboardService.CopyText(value);
        if (sender is not Button button) return;
        var original = button.Content;
        button.Content = LocalizedStrings.Get("CommonCopied", "Copied");
        button.IsEnabled = false;
        await Task.Delay(800);
        button.Content = original;
        button.IsEnabled = true;
    }
}
