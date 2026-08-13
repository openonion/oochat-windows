using System;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// Settings → Identity: shows this installation's agent address, public key, and database
/// path, each with a copy button. Read-only by design — the address is derived from the
/// Ed25519 seed in <c>identity_keys</c>, so there is nothing here a user could edit, and
/// regenerating it would invalidate every authorization the agents have granted.
/// </summary>
public sealed partial class IdentitySettingsContent : UserControl, System.IDisposable
{
    // Kept as fields rather than re-read from the TextBlocks on copy: the displayed text may
    // be trimmed or styled, and the clipboard must get the exact value.
    private string _address = "";
    private string _publicKey = "";
    private bool _showApiKey;
    private readonly System.Threading.CancellationTokenSource _shutdownCts = new();
    private int _disposed;

    public IdentitySettingsContent()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshIdentity();
        await RefreshAccountAsync(forceAuthentication: false);
    }

    private void RefreshIdentity()
    {
        // EnsureIdentity is cheap after first run (it reads the already-decrypted seed) and
        // never generates here in practice — startup has long since created the identity.
        var identity = IdentityStore.EnsureIdentity();
        _address = identity.Address;
        _publicKey = Hex.ToLowerString(identity.PublicKey);
        AddressText.Text = _address;
        PublicKeyText.Text = _publicKey;
        StorageText.Text = AppDatabase.DatabasePath;

        // The two kinds of identity get different wording because they genuinely differ: one has a
        // phrase you can type into any ConnectOnion client, the other only a hex key. Labelling
        // both "recovery phrase" would send someone hunting for words that do not exist.
        var hasPhrase = IdentityStore.ExportBackup().HasMnemonic;
        ShowBackupButtonText.Text = hasPhrase
            ? LocalizedStrings.Get("IdentityShowRecoveryPhrase", "Show recovery phrase")
            : LocalizedStrings.Get("IdentityShowPrivateKey", "Show private key");
        // Set alongside the label, not once in XAML: a static name would have a screen reader
        // announce "recovery phrase" on a button that reveals a key, for exactly the users least
        // able to notice the mismatch.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ShowBackupButton,
            hasPhrase
                ? LocalizedStrings.Get("IdentityShowRecoveryPhrase", "Show recovery phrase")
                : LocalizedStrings.Get("IdentityShowPrivateKey", "Show private key"));
        BackupDescriptionText.Text = hasPhrase
            ? LocalizedStrings.Get(
                "IdentityRecoveryPhraseDescription",
                "Your identity is backed by a 12-word recovery phrase. It restores this address on another machine, in the CLI, or in the web client — it is the only copy that survives losing this device.")
            : LocalizedStrings.Get(
                "IdentityPrivateKeyDescription",
                "This identity predates recovery phrases, so it is backed up as a raw private key. Keep a copy: it is the only thing that restores this address elsewhere.");
    }

    /// <summary>
    /// Reveals the backup. Deliberately behind a button rather than shown on the page: this panel
    /// is one click from anywhere in Settings, and a secret that is on screen by default is one
    /// screen-share away from being someone else's.
    /// </summary>
    private async void ShowBackup_Click(object sender, RoutedEventArgs e)
    {
        await ShowBackupDialogAsync(isFirstReveal: false);
    }

    private async System.Threading.Tasks.Task ShowBackupDialogAsync(bool isFirstReveal)
    {
        var dialog = new RecoveryPhraseDialog(IdentityStore.ExportBackup(), isFirstReveal)
        {
            XamlRoot = XamlRoot,
        };
        await dialog.ShowThemedAsync();
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RestoreIdentityDialog { XamlRoot = XamlRoot };

        var backupRequested = false;
        void OnBackupRequested(object? s, System.EventArgs args) => backupRequested = true;
        dialog.BackupRequested += OnBackupRequested;
        try
        {
            await dialog.ShowThemedAsync();
        }
        finally
        {
            dialog.BackupRequested -= OnBackupRequested;
        }

        // The restore dialog closes itself to show the backup (two ContentDialogs cannot be open
        // at once), so finish that errand and put the user back where they were.
        if (backupRequested)
        {
            await ShowBackupDialogAsync(isFirstReveal: false);
            Restore_Click(sender, e);
            return;
        }

        if (dialog.RestoredAddress is null) return;

        // Every open socket authenticated as the old address, and the host will not accept it as
        // this one. Dropping them all means the next send re-handshakes under the new identity;
        // leaving them would fail the next turn with an authentication error that looks like the
        // agent's fault. The dialog refuses to restore while a run is active, so nothing in flight
        // is being cut off here.
        await AppServices.RunManager.Connections.DisposeAllAsync();

        RefreshIdentity();
        AppServices.OpenOnionAccount.Clear();
        await RefreshAccountAsync(forceAuthentication: true);
        MainWindow.FromXamlRoot(XamlRoot)?.ShowInAppToast(new Models.Notifications.InAppToastModel(
            "Identity restored",
            $"This device now signs as {dialog.RestoredAddress}. Agents that authorized the previous "
                + "address will need to authorize this one.",
            Models.Notifications.NotificationType.TaskCompleted,
            AgentId: null,
            ConversationId: null,
                ActionId: null));
    }

    private async void GenerateIdentity_Click(object sender, RoutedEventArgs e)
    {
        IdentityActionErrorBar.IsOpen = false;
        if (AppServices.RunManager.GetActiveRuns().Count > 0)
        {
            IdentityActionErrorBar.Message = LocalizedStrings.Get(
                "IdentityGenerateWait",
                "Wait for the running conversation to finish before generating a new identity.");
            IdentityActionErrorBar.IsOpen = true;
            return;
        }

        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizedStrings.Get("IdentityGenerateTitle", "Generate a new identity?"),
            Content = LocalizedStrings.Get(
                "IdentityGenerateWarning",
                "This replaces your current identity and creates a different OpenOnion account on first sign-in. "
                + "Your current balance, agent authorizations, and account history do not move to the new address. "
                + "Back up the current recovery phrase first."),
            PrimaryButtonText = LocalizedStrings.Get("IdentityGenerateConfirm", "Generate"),
            CloseButtonText = LocalizedStrings.Get("CommonCancel", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowThemedAsync() != ContentDialogResult.Primary) return;

        // Check again after the confirmation: a run may have started while the dialog was open.
        if (AppServices.RunManager.GetActiveRuns().Count > 0)
        {
            IdentityActionErrorBar.Message = LocalizedStrings.Get(
                "IdentityGenerateConversationStarted",
                "A conversation started while the confirmation was open. Wait for it to finish.");
            IdentityActionErrorBar.IsOpen = true;
            return;
        }

        try
        {
            // Idle sockets still authenticate as the old identity. Drop them before replacing the
            // key so the next connection cannot accidentally reuse an old authenticated channel.
            await AppServices.RunManager.Connections.DisposeAllAsync();
            var (identity, _) = IdentityStore.GenerateNewIdentity();

            RefreshIdentity();
            AppServices.OpenOnionAccount.Clear();

            // The recovery phrase is the only portable copy. Show it before making the optional
            // network request that creates/signs in to the corresponding OpenOnion account.
            await ShowBackupDialogAsync(isFirstReveal: true);
            await RefreshAccountAsync(forceAuthentication: true);

            MainWindow.FromXamlRoot(XamlRoot)?.ShowInAppToast(
                new Models.Notifications.InAppToastModel(
                    "New identity created",
                    $"This device now signs as {identity.Address}. The OpenOnion account is linked "
                        + "to that address.",
                    Models.Notifications.NotificationType.TaskCompleted,
                    AgentId: null,
                    ConversationId: null,
                    ActionId: null));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            IdentityActionErrorBar.Message = ex.Message;
            IdentityActionErrorBar.IsOpen = true;
        }
    }

    /// <summary>Copies whichever value the clicked button is tagged with, and flashes
    /// "Copied" on it for ~1s as the only confirmation (there is no toast for this).</summary>
    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        // The Tag is the discriminator, so both buttons share this one handler; an unknown
        // tag copies nothing rather than copying the wrong secret-adjacent value.
        var value = (sender as FrameworkElement)?.Tag switch
        {
            "address" => _address,
            "publicKey" => _publicKey,
            "apiKey" => AppServices.OpenOnionAccount.ApiKey ?? "",
            _ => "",
        };
        if (string.IsNullOrWhiteSpace(value)) return;
        ClipboardService.CopyText(value);
        if (sender is Button button)
        {
            // Restoring the caption means capturing it first — the label differs per button
            // and is localized, so it cannot be re-derived from a literal here.
            var original = button.Content;
            button.IsHitTestVisible = false;
            button.Content = LocalizedStrings.Get("CommonCopied", "Copied");
            var shutdownToken = _shutdownCts.Token;
            try
            {
                await System.Threading.Tasks.Task.Delay(900, shutdownToken);
            }
            catch (System.OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                return;
            }
            button.Content = original;
            button.IsHitTestVisible = true;
        }
    }

    private async void RefreshAccount_Click(object sender, RoutedEventArgs e)
        => await RefreshAccountAsync(forceAuthentication: false);

    private async System.Threading.Tasks.Task RefreshAccountAsync(bool forceAuthentication)
    {
        AccountProgress.IsActive = true;
        AccountProgress.Visibility = Visibility.Visible;
        AccountErrorBar.IsOpen = false;
        try
        {
            var profile = await AppServices.OpenOnionAccount.RefreshAsync(
                forceAuthentication, _shutdownCts.Token);
            BalanceText.Text = $"${profile.BalanceUsd:0.00}";
            PurchasedText.Text = $"${profile.CreditsUsd:0.00}";
            SpentText.Text = $"${profile.TotalCostUsd:0.00}";
            AccountSummaryPanel.Visibility = Visibility.Visible;
            UpdateApiKeyPresentation();
        }
        catch (System.OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AccountErrorBar.Message = ex.Message;
            AccountErrorBar.IsOpen = true;
            AccountSummaryPanel.Visibility = AppServices.OpenOnionAccount.Profile is null
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        finally
        {
            AccountProgress.IsActive = false;
            AccountProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void RevealApiKey_Click(object sender, RoutedEventArgs e)
    {
        _showApiKey = !_showApiKey;
        UpdateApiKeyPresentation();
    }

    private void UpdateApiKeyPresentation()
    {
        var key = AppServices.OpenOnionAccount.ApiKey ?? "";
        ApiKeyText.Text = _showApiKey ? key : MaskApiKey(key);
        RevealApiKeyButton.Content = _showApiKey
            ? LocalizedStrings.Get("IdentityHideLabel", "Hide")
            : LocalizedStrings.Get("IdentityRevealLabel", "Reveal");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ApiKeyText,
            _showApiKey
                ? LocalizedStrings.Get("IdentityApiKeyVisible", "OpenOnion API key")
                : LocalizedStrings.Get("IdentityApiKeyMasked", "Masked OpenOnion API key"));
    }

    private static string MaskApiKey(string value)
        => value.Length <= 12 ? "••••••••" : $"{value[..6]}••••••{value[^4..]}";

    private async void TopUp_Click(object sender, RoutedEventArgs e)
    {
        if (!await AppServices.UriLauncher.LaunchAsync(
                new Uri("https://o.openonion.ai/purchase")))
        {
            AccountErrorBar.Message = LocalizedStrings.Get(
                "IdentityPurchaseOpenFailure",
                "Windows could not open the OpenOnion purchase page.");
            AccountErrorBar.IsOpen = true;
        }
    }

    /// <summary>Cancels delayed visual feedback before the window's XAML tree is torn down.</summary>
    public void Shutdown() => Dispose();

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
    }
}
