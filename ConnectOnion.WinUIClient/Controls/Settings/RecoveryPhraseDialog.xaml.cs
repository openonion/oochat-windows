using System;
using System.Collections.Generic;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// One word of a recovery phrase, with its 1-based position.
///
/// <para>A plain <c>{ get; set; }</c> class, not a record with <c>init</c> accessors: the type is
/// referenced from an <c>x:Bind</c> <c>DataTemplate</c>, and <c>required</c>/<c>init</c> members
/// break the generated <c>XamlTypeInfo</c> metadata with a confusing CS9035.</para>
/// </summary>
public sealed class RecoveryPhraseWord
{
    public string Position { get; set; } = "";
    public string Word { get; set; } = "";
}

/// <summary>
/// Shows the identity's backup — the BIP39 recovery phrase, or for an identity minted before
/// phrases existed, its raw private seed.
///
/// <para>Used from two places with the same content and different framing: once automatically when
/// this install first mints an identity (the user has a backup they have never seen), and on demand
/// from Settings → Identity. Dismissing it loses nothing — Settings can always show it again — which
/// is why it does not trap the user in an acknowledgement they cannot escape.</para>
///
/// <para>The phrase reaches this dialog as a parameter and is never written anywhere else: not to
/// the log, not to a field that outlives the dialog. The one exception is the clipboard, at the
/// user's explicit request.</para>
/// </summary>
public sealed partial class RecoveryPhraseDialog : ContentDialog
{
    private readonly string _copyValue;

    /// <param name="backup">The identity's backup material.</param>
    /// <param name="isFirstReveal">
    /// True when this install just created the identity and the user has never seen the phrase. Only
    /// changes the wording and the primary button — there is no second, weaker code path.
    /// </param>
    public RecoveryPhraseDialog(IdentityBackup backup, bool isFirstReveal)
    {
        ArgumentNullException.ThrowIfNull(backup);
        InitializeComponent();

        PrimaryButtonText = isFirstReveal
            ? LocalizedStrings.Get("RecoverySaved", "I've saved it")
            : LocalizedStrings.Get("CommonDone", "Done");
        AddressText.Text = backup.Address;

        if (backup.HasMnemonic)
        {
            _copyValue = backup.Mnemonic!;
            // Titled for what is actually on screen. A pre-phrase identity shown under a
            // "Recovery phrase" heading reads as though the words failed to load.
            Title = isFirstReveal
                ? LocalizedStrings.Get("RecoverySaveTitle", "Save your recovery phrase")
                : LocalizedStrings.Get("RecoveryPhraseTitle", "Recovery phrase");
            WarningText.Text = LocalizedStrings.Get(
                "RecoveryPhraseWarning",
                "Anyone with these words controls this identity. Never share them, and never paste them into a chat.");
            IntroText.Text = isFirstReveal
                ? LocalizedStrings.Get(
                    "RecoveryFirstRevealIntro",
                    "This device just created a ConnectOnion identity. Write these words down in order and keep them somewhere offline — they are the only way to restore this address on another machine, and nobody can recover them for you. You can see them again later in Settings → Identity.")
                : LocalizedStrings.Get(
                    "RecoveryPhraseIntro",
                    "Write these words down in order and keep them somewhere offline. Entering them in ConnectOnion on another machine — or in the CLI or the web client — restores this same address.");
            CopyButtonText.Text = LocalizedStrings.Get("RecoveryCopyPhrase", "Copy phrase");
            WordsRepeater.ItemsSource = BuildWords(backup.Mnemonic!);
            WordsRepeater.Visibility = Visibility.Visible;
        }
        else
        {
            // No phrase can be invented for a seed that was never derived from one — BIP39 only
            // runs phrase → seed. Say that plainly instead of implying the user lost something.
            _copyValue = backup.SeedHex;
            Title = LocalizedStrings.Get("RecoveryPrivateKeyTitle", "Private key");
            WarningText.Text = LocalizedStrings.Get(
                "RecoveryPrivateKeyWarning",
                "Anyone with this key controls this identity. Never share it, and never paste it into a chat.");
            IntroText.Text = LocalizedStrings.Get(
                "RecoveryLegacyIdentityIntro",
                "This identity was created before ConnectOnion used recovery phrases, so it has none — a phrase cannot be worked out from an existing key. Back up the private key below instead; it restores this address the same way.");
            CopyButtonText.Text = LocalizedStrings.Get("RecoveryCopyPrivateKey", "Copy private key");
            SeedText.Text = backup.SeedHex;
            SeedBlock.Visibility = Visibility.Visible;
        }
    }

    private static IReadOnlyList<RecoveryPhraseWord> BuildWords(string mnemonic)
    {
        var parts = mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words = new List<RecoveryPhraseWord>(parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            words.Add(new RecoveryPhraseWord
            {
                // The position is a label the user reads off the screen while transcribing, so it
                // follows their locale's digits — same call as the other user-facing counts
                // (UsageViewModel, UsageHeatmapView). The phrase's *words* are the cross-client
                // BIP39 contract; their numbering is not.
                Position = RecoveryPhraseNumberFormatter.Format(i + 1),
                Word = parts[i],
            });
        }
        return words;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        ClipboardService.CopySensitiveText(_copyValue);
        CopyButtonText.Text = LocalizedStrings.Get(
            "RecoveryPhraseCopiedWithExpiry",
            "Copied · clears in 60 seconds");
    }
}
