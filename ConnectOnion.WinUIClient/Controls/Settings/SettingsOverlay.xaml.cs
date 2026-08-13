using System;
using System.Linq;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ConnectOnion.WinUIClient.Controls;

public sealed record SettingsSearchResult(string Category, string Title);

public sealed partial class SettingsOverlay : UserControl
{
    private static readonly string[] SearchCategories =
        ["General", "Agents", "Notifications", "Keyboard", "Identity", "Usage"];

    private FrameworkElement? _focusReturnTarget;
    private string _selectedCategory = "General";
    private bool _syncingCategorySelection;
    private bool _syncingSearch;
    private bool _initialFocusPending;
    public event EventHandler? CloseRequested;

    public SettingsOverlay()
    {
        InitializeComponent();
        Loaded += SettingsOverlay_Loaded;
    }

    /// <summary>Whether the settings modal is currently shown.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Exposes this overlay to UI Automation as a dialog. Without a peer the control is
    /// invisible to UIA entirely — no dialog boundary for a screen reader, and its
    /// AutomationId unreachable from a UI test. See <see cref="ModalOverlayAutomationPeer"/>.</summary>
    protected override Microsoft.UI.Xaml.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
        => new ModalOverlayAutomationPeer(this);


    public void Show(FrameworkElement? focusReturnTarget)
    {
        _focusReturnTarget = focusReturnTarget;
        SearchBox.Text = string.Empty;
        CompactSearchBox.Text = string.Empty;

        // Go through SelectCategory rather than setting the title by hand: it is what resets the
        // pane visibility too. Setting only the title left the previously shown standalone pane
        // (Identity, now also Usage) on screen under a "General" heading.
        SelectCategory("General");

        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        QueueInitialFocus();
    }

    public void Hide()
    {
        if (Visibility != Visibility.Visible) return;
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        _focusReturnTarget?.Focus(FocusState.Programmatic);
        _focusReturnTarget = null;
    }

    /// <summary>Disarms child controls whose delayed callbacks must not outlive the window.</summary>
    public void Shutdown() => IdentityContent.Shutdown();

    // No UpdateModalSize. The card's size is MaxWidth/MaxHeight plus the per-state Margin in
    // XAML, which the layout system resolves against the space it actually has.
    //
    // The code this replaced computed a width from this control's ActualWidth using the same
    // 860/640 breakpoints the AdaptiveTriggers use — but those measure the *window*, while
    // ActualWidth is the window divided by EffectiveContentScale, because MainWindow.ViewMenu
    // scales FloatingOverlayLayer for zoom and OS text scaling. Away from 100% the two disagreed
    // and the card was sized for one bucket while its navigation column and margin came from
    // another. It also read ActualWidth synchronously right after setting Visibility, before the
    // layout pass that produces it had run, so the first frame used the previous size.

    private void SettingsOverlay_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialFocusPending)
        {
            QueueInitialFocus();
        }
    }

    private void QueueInitialFocus()
    {
        _initialFocusPending = true;
        if (!IsLoaded)
        {
            return;
        }

        // Showing the lazily-created overlay and resolving its adaptive visual state happen in
        // separate layout passes. Focus only after those passes, and select the target from the
        // resolved state rather than from the previous frame's ActualWidth.
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (!IsOpen)
                {
                    _initialFocusPending = false;
                    return;
                }

                var focusTarget = CompactNavigation.Visibility == Visibility.Visible
                    ? (Control)CompactCategoryPicker
                    : SearchBox;
                _initialFocusPending = !focusTarget.Focus(FocusState.Programmatic);

                if (_initialFocusPending)
                {
                    DispatcherQueue.TryEnqueue(
                        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        () =>
                        {
                            if (IsOpen)
                            {
                                var retryTarget = CompactNavigation.Visibility == Visibility.Visible
                                    ? (Control)CompactCategoryPicker
                                    : SearchBox;
                                retryTarget.Focus(FocusState.Programmatic);
                            }

                            _initialFocusPending = false;
                        });
                }
            });
    }

    private void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void CloseButton_Click(object sender, RoutedEventArgs e) => RequestClose();
    private void Backdrop_Tapped(object sender, TappedRoutedEventArgs e) => RequestClose();
    private void ModalContainer_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;
    private void OverlayRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        RequestClose();
    }
    private void GeneralNav_Click(object sender, RoutedEventArgs e) => SelectCategory("General");
    private void AgentsNav_Click(object sender, RoutedEventArgs e) => SelectCategory("Agents");
    private void NotificationsNav_Click(object sender, RoutedEventArgs e) => SelectCategory("Notifications");
    private void IdentityNav_Click(object sender, RoutedEventArgs e) => SelectCategory("Identity");
    private void UsageNav_Click(object sender, RoutedEventArgs e) => SelectCategory("Usage");
    private void KeyboardNav_Click(object sender, RoutedEventArgs e) => SelectCategory("Keyboard");

    /// <summary>
    /// Swaps the right-hand pane. Identity, Usage and Keyboard are standalone controls; every other
    /// category is a section of <see cref="SettingsContent"/>, which stays hidden while one of them
    /// is shown.
    /// </summary>
    private void SelectCategory(string category)
    {
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)
            || !string.IsNullOrWhiteSpace(CompactSearchBox.Text))
        {
            ClearSearch();
        }
        _selectedCategory = category;
        SyncCategorySelection(category);
        CategoryTitle.Text = LocalizedCategoryName(category);

        var isIdentity = category == "Identity";
        var isAgents = category == "Agents";
        var isUsage = category == "Usage";
        var isKeyboard = category == "Keyboard";
        var isSettingsPage = !isIdentity && !isAgents && !isUsage && !isKeyboard;

        SettingsContent.Visibility = isSettingsPage ? Visibility.Visible : Visibility.Collapsed;
        SettingsSearchResultsScrollViewer.Visibility = Visibility.Collapsed;
        IdentityScrollViewer.Visibility = isIdentity ? Visibility.Visible : Visibility.Collapsed;
        AgentsScrollViewer.Visibility = isAgents ? Visibility.Visible : Visibility.Collapsed;
        UsageScrollViewer.Visibility = isUsage ? Visibility.Visible : Visibility.Collapsed;
        KeyboardScrollViewer.Visibility = isKeyboard ? Visibility.Visible : Visibility.Collapsed;

        if (isSettingsPage)
            SettingsContent.SelectCategory(category);

        // Re-read the ledger on entry: a turn may well have finished while Settings sat open.
        if (isUsage)
            UsageContent.Refresh();

        if (isAgents)
            AgentsContent.Refresh();

        // Bindings can have moved since this pane was last shown — the dialog cannot change them,
        // but a second window's Settings can.
        if (isKeyboard)
            KeyboardContent.Refresh();
    }

    public Task RefreshAgentsAsync() => AgentsContent.RefreshAsync();
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => ApplySettingsSearch(SearchBox.Text, CompactSearchBox);
    private void CompactSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => ApplySettingsSearch(CompactSearchBox.Text, SearchBox);

    private void ApplySettingsSearch(string query, TextBox peer)
    {
        if (_syncingSearch) return;

        _syncingSearch = true;
        peer.Text = query;
        _syncingSearch = false;

        if (string.IsNullOrWhiteSpace(query))
        {
            SelectCategory(_selectedCategory);
            return;
        }

        // Search the complete category index. The old implementation only filtered SettingsPage,
        // silently excluding Agents, Keyboard, Identity and Usage even though the search box sits
        // above all six categories.
        SettingsContent.Visibility = Visibility.Collapsed;
        IdentityScrollViewer.Visibility = Visibility.Collapsed;
        AgentsScrollViewer.Visibility = Visibility.Collapsed;
        UsageScrollViewer.Visibility = Visibility.Collapsed;
        KeyboardScrollViewer.Visibility = Visibility.Collapsed;
        SettingsSearchResultsScrollViewer.Visibility = Visibility.Visible;

        var results = SearchCategories
            .Where(category => CategoryMatches(category, query))
            .Select(category => new SettingsSearchResult(category, LocalizedCategoryName(category)))
            .ToList();
        SettingsSearchResultsList.ItemsSource = results;
        SettingsSearchResultsEmpty.Visibility = results.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        CategoryTitle.Text = LocalizedStrings.Get(
            "SettingsSearchResultsHeading",
            "Search results");
    }

    private static bool CategoryMatches(string category, string query)
    {
        var searchable = category switch
        {
            "General" => LocalizedStrings.Get("SettingsSearchKeywordsGeneral", "general appearance theme language window startup chat audio microphone"),
            "Agents" => LocalizedStrings.Get("SettingsSearchKeywordsAgents", "agents agent address connection icon avatar delete rename"),
            "Notifications" => LocalizedStrings.Get("SettingsSearchKeywordsNotifications", "notifications alerts sound banner toast approval reply"),
            "Keyboard" => LocalizedStrings.Get("SettingsSearchKeywordsKeyboard", "keyboard shortcuts hotkeys keys bindings"),
            "Identity" => LocalizedStrings.Get("SettingsSearchKeywordsIdentity", "identity address recovery phrase mnemonic private key seed backup restore"),
            "Usage" => LocalizedStrings.Get("SettingsSearchKeywordsUsage", "usage tokens model cost history heatmap clear"),
            _ => category,
        };
        searchable = $"{LocalizedCategoryName(category)} {searchable}";
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(token => searchable.Contains(token, StringComparison.CurrentCultureIgnoreCase));
    }

    private void SettingsSearchResult_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SettingsSearchResult result) return;
        ClearSearch();
        SelectCategory(result.Category);
    }

    private void ClearSearch()
    {
        _syncingSearch = true;
        SearchBox.Text = string.Empty;
        CompactSearchBox.Text = string.Empty;
        _syncingSearch = false;
        SettingsContent.Filter(string.Empty);
    }

    private void CompactCategoryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCategorySelection) return;

        if (CompactCategoryPicker.SelectedItem is ComboBoxItem { Tag: string category })
            SelectCategory(category);
    }

    private void SyncCategorySelection(string category)
    {
        _syncingCategorySelection = true;
        try
        {
            GeneralNav.IsChecked = category == "General";
            AgentsNav.IsChecked = category == "Agents";
            NotificationsNav.IsChecked = category == "Notifications";
            KeyboardNav.IsChecked = category == "Keyboard";
            IdentityNav.IsChecked = category == "Identity";
            UsageNav.IsChecked = category == "Usage";

            var compactIndex = category switch
            {
                "Agents" => 1,
                "Notifications" => 2,
                "Keyboard" => 3,
                "Identity" => 4,
                "Usage" => 5,
                _ => 0,
            };

            if (CompactCategoryPicker.SelectedIndex != compactIndex)
                CompactCategoryPicker.SelectedIndex = compactIndex;
        }
        finally
        {
            _syncingCategorySelection = false;
        }
    }

    private static string LocalizedCategoryName(string category) => category switch
    {
        "Agents" => LocalizedStrings.Get("SettingsCategoryAgentsName", "Agents"),
        "Notifications" => LocalizedStrings.Get("SettingsCategoryNotificationsName", "Notifications"),
        "Keyboard" => LocalizedStrings.Get("SettingsCategoryKeyboardName", "Keyboard"),
        "Identity" => LocalizedStrings.Get("SettingsCategoryIdentityName", "Identity"),
        "Usage" => LocalizedStrings.Get("SettingsCategoryUsageName", "Usage"),
        _ => LocalizedStrings.Get("SettingsCategoryGeneralName", "General"),
    };
}
