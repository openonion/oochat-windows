using System;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Controls;
using ConnectOnion.WinUIClient.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace ConnectOnion.WinUIClient.Views;

public sealed partial class AgentDetailPage : Page, IReloadablePage, IShutdownDisarmable
{
    public AgentDetailViewModel Vm { get; } = App.GetService<AgentDetailViewModel>();

    public AgentDetailPage()
    {
        InitializeComponent();

        // Reuse this one instance across navigations rather than rebuilding it — this page also
        // hosts a ChatComposer (Win2D canvas) — every time the user crosses page types (e.g.
        // ChatPage → AgentDetailPage). MainWindow.NavigateTo's same-type reuse only covers
        // agent→agent, so a chat→agent switch was constructing and discarding a fresh tree per
        // click. The presence hook stays attached for this single reused instance (see OnLoaded);
        // there is no per-view state to tear down, so there is no Unloaded teardown.
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
        => await LoadPageAsync();

    /// <summary>Width at which the page uses its roomier padding and larger agent name. Measured
    /// against the page, not the window: the sidebar takes up to 288px that an
    /// <c>AdaptiveTrigger MinWindowWidth</c> would have counted as available here.</summary>
    private const double WidePageMinWidth = 720;

    private void AgentRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        => VisualStateManager.GoToState(
            this,
            e.NewSize.Width >= WidePageMinWidth ? "Wide" : "Narrow",
            useTransitions: false);

    /// <summary>Re-points this page at whichever agent is now selected, without rebuilding it —
    /// this page carries a <see cref="ChatComposer"/> (Win2D canvas and all), so clicking through
    /// the sidebar's agents was constructing and discarding one per click.</summary>
    public System.Threading.Tasks.Task ReloadAsync() => LoadPageAsync();

    /// <summary>Synchronously releases the cached composer's Win2D, timer, audio, and speech resources.</summary>
    public void DisarmForShutdown()
    {
        Vm.Cleanup();
        Composer.Dispose();
    }

    private async System.Threading.Tasks.Task LoadPageAsync()
    {
        await Vm.LoadAsync();

        // Rapid switching can navigate away — and unload this cached page — during the await
        // above. Touching the composer's dependency properties while it is detached from the
        // visual tree throws E_UNEXPECTED (0x8000FFFF). Bail if we are no longer loaded; the next
        // OnLoaded applies the current selection.
        if (!IsLoaded) return;

        Composer.CanSubmit = Vm.CanStartConversation;
        Composer.FocusInput();
        // Account balance is useful secondary information, never a gate on the first interactive
        // frame. The resilient HTTP pipeline may retry, so refresh only after the composer works.
        _ = RefreshBalanceAsync();
    }

    // This composer starts a fresh conversation before navigating to ChatPage.
    // The whole submission (text + attachments) is carried across the
    // navigation via the Frame parameter so an image/file sent from here
    // behaves exactly like one sent from ChatPage's own composer: it creates
    // the session, navigates, and is actually sent as the first turn.
    private async void Composer_SubmitRequested(object? sender, ComposerSubmission submission)
        => await StartConversationAsync(submission);

    /// <summary>Records the picked mode locally. Unlike <c>ChatPage</c>'s handler there is nothing
    /// to send: a <c>mode_change</c> frame is only forwarded while a turn is running, and this page
    /// has neither a turn nor a conversation. The choice travels on the submission and is applied
    /// to the conversation the first message creates.</summary>
    private void Composer_ModeChangeRequested(object? sender, string mode)
    {
        if (AgentModes.IsValid(mode)) Vm.CurrentMode = mode;
    }

    private async void OfflineNotice_RecheckRequested(object? sender, EventArgs e)
        => await Vm.RecheckPresenceAsync();

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        if (!Vm.CanShareAgent || string.IsNullOrWhiteSpace(Vm.AgentAddress)) return;
        var dialog = new AgentShareDialog(Vm.AgentAddress) { XamlRoot = XamlRoot };
        await dialog.ShowThemedAsync();
    }

    private async System.Threading.Tasks.Task RefreshBalanceAsync()
    {
        try
        {
            var profile = await Services.AppServices.OpenOnionAccount.RefreshAsync();
            if (!IsLoaded) return;
            BalanceButton.Content = LocalizedStrings.Format(
                "AgentBalanceTopUp",
                "Balance {0:C2} · Top up",
                profile.BalanceUsd);
            BalanceButton.Visibility = Visibility.Visible;
        }
        catch
        {
            if (IsLoaded) BalanceButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void Balance_Click(object sender, RoutedEventArgs e)
    {
        var address = Uri.EscapeDataString(Vm.AgentAddress);
        await Services.AppServices.UriLauncher.LaunchAsync(
            new Uri($"https://o.openonion.ai/purchase?agent={address}"));
    }

    private async System.Threading.Tasks.Task StartConversationAsync(ComposerSubmission submission)
    {
        var initialPrompt = submission.Text.Trim();
        var hasAttachments = submission.Attachments.Count > 0;
        if (initialPrompt.Length == 0 && !hasAttachments) return;

        if (await Vm.StartConversationAsync(initialPrompt, hasAttachments))
        {
            MainWindow.FromXamlRoot(XamlRoot)?.NavigateTo(
                typeof(ChatPage),
                forceReload: true,
                // The prompt is re-trimmed here, so the submission is rebuilt rather than passed
                // through — carry the mode across with it or the new conversation opens at the
                // default whatever the picker said.
                parameter: new ComposerSubmission(initialPrompt, submission.Attachments, submission.Mode),
                transitionInfo: new DrillInNavigationTransitionInfo());
        }
    }
}
