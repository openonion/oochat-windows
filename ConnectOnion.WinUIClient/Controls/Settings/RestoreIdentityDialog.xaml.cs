using System;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// Settings → Identity: restores a previously backed-up identity from its BIP39 recovery phrase
/// (or, for one created before phrases existed, its raw private key).
///
/// <para><b>The address preview is the point of this dialog.</b> A phrase with one word wrong is
/// usually still a <i>valid</i> phrase — it just belongs to someone else's address — so a
/// checksum pass alone cannot tell a user whether they typed their own backup. Deriving the address
/// as they type, next to the address they currently have, is what makes the mistake visible before
/// the current identity is overwritten.</para>
/// </summary>
public sealed partial class RestoreIdentityDialog : ContentDialog
{
    /// <summary>The address that was restored, set only after the import actually succeeded.</summary>
    public string? RestoredAddress { get; private set; }

    // What the currently-typed input would restore to, or null when it is not (yet) a valid backup.
    // Held so the primary click imports exactly what the preview promised.
    private string? _pendingInput;
    private bool _pendingIsMnemonic;

    public RestoreIdentityDialog()
    {
        InitializeComponent();
        CurrentAddressText.Text = IdentityStore.EnsureIdentity().Address;
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private void Phrase_TextChanged(object sender, TextChangedEventArgs e)
    {
        var input = PhraseBox.Text;
        _pendingInput = null;
        StatusText.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(input))
        {
            PreviewBlock.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = false;
            return;
        }

        // Deriving on every keystroke is 2048 rounds of HMAC-SHA512 — a couple of milliseconds,
        // and only once the input is already a complete, checksum-valid phrase, so a half-typed
        // phrase costs nothing but a wordlist lookup.
        if (TryPreview(input, out var address, out var isMnemonic, out var error))
        {
            _pendingInput = input;
            _pendingIsMnemonic = isMnemonic;
            RestoredAddressText.Text = address;
            PreviewBlock.Visibility = Visibility.Visible;
            IsPrimaryButtonEnabled = true;
            return;
        }

        PreviewBlock.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = false;
        // Silent while the user is still mid-phrase: nagging "invalid" at word four is noise, not
        // feedback. Only a complete-looking input that still fails is worth reporting.
        if (error is not null)
        {
            StatusText.Text = error;
            StatusText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Works out what <paramref name="input"/> would restore to. <paramref name="error"/> is set
    /// only when the input looks finished and is still wrong; an obviously incomplete one returns
    /// false with no message.
    /// </summary>
    private static bool TryPreview(string input, out string address, out bool isMnemonic, out string? error)
    {
        address = "";
        isMnemonic = false;
        error = null;

        var trimmed = input.Trim();
        var hex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? trimmed[2..] : trimmed;
        var looksHex = hex.Length > 0 && System.Linq.Enumerable.All(hex, Uri.IsHexDigit);

        if (looksHex)
        {
            if (hex.Length is 64 or 128)
            {
                try
                {
                    var decoded = Convert.FromHexString(hex);
                    address = AgentIdentity.FromSeed(decoded.Length == 32 ? decoded : decoded[..32]).Address;
                    return true;
                }
                catch (FormatException)
                {
                    error = LocalizedStrings.Get("RestoreInvalidPrivateKey", "That is not a valid private key.");
                    return false;
                }
            }

            // Long enough to have been meant as a key, and the wrong length — worth saying.
            if (hex.Length > 128 || hex.Length is > 64 and < 128)
                error = LocalizedStrings.Get(
                    "RestorePrivateKeyLength",
                    "A private key must be 64 or 128 hex characters.");
            return false;
        }

        var words = Bip39.Normalize(trimmed).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is not (12 or 15 or 18 or 21 or 24)) return false;

        if (!Bip39.Validate(trimmed))
        {
            error = LocalizedStrings.Get(
                "RestoreInvalidPhrase",
                "That phrase is not valid — check the spelling and the order of the words.");
            return false;
        }

        address = AgentIdentity.FromMnemonic(trimmed).Address;
        isMnemonic = true;
        return true;
    }

    private void BackupCurrent_Click(object sender, RoutedEventArgs e)
    {
        // Chaining ContentDialogs is not allowed (only one can be open per XamlRoot), so this
        // closes to let the caller show the backup — a restore is not something to complete on
        // autopilot anyway, and reopening this dialog costs one click.
        Hide();
        BackupRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the user asked to see the current identity's backup instead of
    /// restoring. The dialog has already closed by then.</summary>
    public event EventHandler? BackupRequested;

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_pendingInput is not { } input)
        {
            args.Cancel = true;
            return;
        }

        // Checked here rather than only before the dialog opened: a turn can start while it is up,
        // and swapping the identity out from under a live socket would fail the run in a way that
        // looks like an agent problem.
        if (AppServices.RunManager.GetActiveRuns().Count > 0)
        {
            args.Cancel = true;
            StatusText.Text = LocalizedStrings.Get(
                "RestoreWaitForConversation",
                "Wait for the running conversation to finish before restoring an identity.");
            StatusText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            RestoredAddress = _pendingIsMnemonic
                ? IdentityStore.ImportMnemonic(input).Address
                : IdentityStore.ImportSeed(input).Address;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // ImportMnemonic/ImportSeed leave the stored identity untouched when they throw, so
            // keeping the dialog open lets the user correct the input against an intact identity.
            args.Cancel = true;
            StatusText.Text = ex.Message;
            StatusText.Visibility = Visibility.Visible;
        }
    }
}
