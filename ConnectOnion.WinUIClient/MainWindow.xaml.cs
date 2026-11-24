using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Presentation;
using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using Windows.System;
using Microsoft.UI.Xaml.Media;

namespace ConnectOnion.WinUIClient;

/// <summary>
/// Shell window with a custom WinUI title bar and a content frame.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private readonly SessionRepository _sessionRepository;
    private readonly PreferencesRepository _preferencesRepository;
    private readonly WindowPlacementStore _windowPlacementStore;
    private readonly TextScaleService _textScale;
    private int _windowServicesDetached;
    private int _windowPlacementSaved;
    private int _exitStarted;
    private bool _isSidebarVisible = true;
    private bool _wasCompactLayout;
    private double _sidebarWidth = SidebarLayoutPolicy.DefaultWidth;
    private double _sidebarResizeStartWidth;
    private double _sidebarResizeStartPointerX;
    private uint _sidebarResizePointerId;
    private bool _isSidebarResizing;
    private bool _skipNextNavigationSidebarRefresh;
    private bool _isHistoryNavigationInProgress;
    private ShellNavigationContext? _currentNavigationContext;
    private bool _isRightWorkspaceExpanded;
    private double _rightWorkspaceWidth = 380;
    private double _rightWorkspaceResizeStartWidth;
    private double _rightWorkspaceResizeStartPointerX;
    private uint _rightWorkspaceResizePointerId;
    private bool _isRightWorkspaceResizing;
    private string? _dashboardAgentId;
    private AgentConfig? _dashboardAgentConfig;
    private string? _dashboardBridgeToken;
    private string? _dashboardDocumentSource;
    private string? _dashboardPendingNavigationSource;
    private Task<bool>? _dashboardWebViewInitialisation;
    private long _dashboardLoadGeneration;

    private const double RightWorkspaceMinimumWidth = 380;
    private const double RightWorkspaceMaximumWidth = 1440;

    public IRelayCommand OpenWindowCommand { get; }
    public IRelayCommand ExitFromTrayCommand { get; }

    public MainWindow()
    {
        OpenWindowCommand = new RelayCommand(RestoreFromTray);
        ExitFromTrayCommand = new RelayCommand(ExitApplication);
        InitializeComponent();
        _savedReconnectSeconds = AppServices.StartupState.Preferences.ReconnectDelaySeconds;
        ReconnectAfterInput.Text = _savedReconnectSeconds.ToString(CultureInfo.CurrentCulture);
        _controlCentreReady = true;
        _sessionRepository = AppServices.Sessions;
        _preferencesRepository = AppServices.Preferences;
        // Capture DI-owned services while the host is alive. Closed can run after App.Exit has
        // already disposed the provider, so teardown code must never resolve a fresh service.
        _windowPlacementStore = AppServices.WindowPlacement;
        _textScale = AppServices.TextScale;
        _textScale.Changed += OnTextScaleChanged;
        AppServices.RunManager.DashboardSnapshotReceived += OnAgentDashboardSnapshotReceived;
        TrackWindowLifetime();
        InitializeNotificationPresence();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = GetAppWindow(_hwnd);
        InitializeWindowPlacement();
        ConfigureCustomTitleBar();
        InitializeTrayBehavior();
        Diagnostics.StartupTelemetry.ArmPerformanceExit(DispatcherQueue, ExitApplication);
        ShellSidebar.NavigationRequested += NavigateTo;
        ShellSidebar.NavigationResetRequested += ResetNavigationTo;
        ShellSidebar.SettingsRequested += ShowSettingsOverlay;
        ShellSidebar.SessionSearchRequested += ShowSessionSearchOverlay;
        ShellSidebar.AddAgentRequested += ShowAddAgentOverlay;
        ContentFrame.Navigated += async (_, _) =>
        {
            try
            {
                UpdateRightWorkspaceForCurrentPage();
                ShellSidebar.SetCurrentPage(ContentFrame.CurrentSourcePageType);
                UpdateNavigationButtons();
                CloseFindOverlay();
                if (_skipNextNavigationSidebarRefresh)
                {
                    _skipNextNavigationSidebarRefresh = false;
                }
                else
                {
                    await ShellSidebar.RefreshAsync();
                }
                await RefreshControlCentreAgentAsync();
                // Navigating does NOT close the sidebar, by request. Picking a conversation used
                // to auto-collapse it at the compact width; the sidebar now stays exactly as the
                // user left it, and the ways to close it are all explicit — the title-bar toggle,
                // Ctrl+B, or tapping the dimmed backdrop (SidebarDismissLayer_Tapped). Note that
                // at the compact width the sidebar is an overlay, so leaving it open does cover
                // the conversation that was just opened.
            }
            catch
            {
                // Best-effort UI refresh; a failed sidebar update should not
                // crash the process.
            }
        };
        ThemeService.ThemeApplied += ApplyTitleBarTheme;
        ThemeService.ThemeApplied += ApplyDashboardTheme;
        _sessionRepository.SessionsChanged += OnSessionsChanged;
        Closed += (_, _) =>
        {
            DisposeTrayBehavior();
            AppServices.RunManager.DashboardSnapshotReceived -= OnAgentDashboardSnapshotReceived;
            DetachDashboardWebView();
            DetachWindowServices();
        };
        ThemeService.RegisterRoot(RootGrid);
        // The user's theme must not queue behind the sidebar load: awaiting it in the Loaded
        // handler below shows the shell and the first page in the default theme before flipping
        // them, and a throwing sidebar refresh would leave the theme unapplied entirely.
        _ = ApplyStartupThemeAsync();
        // Same reasoning as the theme: the saved zoom is a view preference the user set once and
        // expects back, so it must not wait on the sidebar load. It measures RootGrid, so it runs
        // once the layout exists rather than from the constructor.
        RootGrid.Loaded += async (_, _) => await RestoreZoomAsync();
        // XamlRoot only exists once the tree is loaded. This is what makes moving the window to a
        // monitor with a different scale re-measure the caption inset — the metrics are otherwise
        // computed once and cached, deliberately, to keep them off the resize path.
        RootGrid.Loaded += (_, _) => TrackDisplayScaleChanges();
        RootGrid.GotFocus += (_, _) =>
            Diagnostics.StartupTelemetry.Mark(Diagnostics.StartupPhases.FirstInteractive);
        RootGrid.Loaded += async (_, _) =>
        {
            try
            {
                await ShellSidebar.RefreshAsync();
                Diagnostics.StartupTelemetry.Mark(Diagnostics.StartupPhases.SessionListLoaded);
                await RefreshTrayRecentChatsAsync();
                var benchmarkConversation =
                    Environment.GetEnvironmentVariable(
                        Diagnostics.StartupProfiler.OpenConversationEnvironmentVariable) == "1";
                // Home is the product's stable cold-start surface. An active conversation can
                // belong to a different agent than the currently selected one (for example after
                // opening an agent's detail page), so treating that pointer alone as a restore
                // signal can open an unrelated empty ChatPage. Only the benchmark fixture opts
                // into restoring Chat directly.
                _skipNextNavigationSidebarRefresh = true;
                NavigateTo(benchmarkConversation ? typeof(ChatPage) : typeof(HomePage));
                UpdateNavigationButtons();
                EnsureInitialFocus();
                Diagnostics.StartupTelemetry.Mark(Diagnostics.StartupPhases.ShellInitialized);
            }
            catch
            {
                // Initialization is best-effort; a failure here should not
                // prevent the window from being shown.
            }
            finally
            {
                // UI is ready — replay any notification click buffered at cold start, and report an
                // identity that was reset before this window existed to show it.
                AppServices.NotificationActivation.MarkReady();
                ReportIdentityResetIfAny();
                RevealNewRecoveryPhraseIfAny();
            }
        };
    }

    /// <summary>
    /// Stops window callbacks from touching DI-owned services — or the visual tree — during final
    /// teardown. The dispatcher keeps pumping briefly after <c>Closed</c>, so anything still armed
    /// (a timer tick, an animation continuation) can run against a tree the framework is already
    /// tearing down; that surfaces as an access violation inside Microsoft.UI.Xaml.dll, which no
    /// managed catch can see. Disarm the window's own actives here.
    /// </summary>
    private void DetachWindowServices()
    {
        if (System.Threading.Interlocked.Exchange(ref _windowServicesDetached, 1) != 0) return;

        ThemeService.ThemeApplied -= ApplyTitleBarTheme;
        ThemeService.ThemeApplied -= ApplyDashboardTheme;
        ThemeService.UnregisterRoot(RootGrid);
        _sessionRepository.SessionsChanged -= OnSessionsChanged;
        ShellSidebar.NavigationRequested -= NavigateTo;
        ShellSidebar.NavigationResetRequested -= ResetNavigationTo;
        ShellSidebar.SettingsRequested -= ShowSettingsOverlay;
        ShellSidebar.SessionSearchRequested -= ShowSessionSearchOverlay;
        ShellSidebar.AddAgentRequested -= ShowAddAgentOverlay;
        ShellSidebar.Shutdown();
        DetachNotificationPresence();
        DetachShortcutHints();
        DetachViewMenu();
        // XamlRoot outlives this handler's usefulness, and XamlRoot_Changed re-lays out the tree.
        DetachDisplayScaleTracking();
        Diagnostics.StartupTelemetry.DisarmPerformanceExit();
        ShutdownOverlays();
        InAppNotifications.Shutdown();

        // Pages disarm their own timers in Unloaded, which is not guaranteed to fire on window
        // close — so the page currently on screen is asked directly. Wrapped because a page that
        // throws while disarming must not abort the rest of this teardown; the alternative is
        // leaving the notification host and window hooks attached.
        //
        // The outcome is logged rather than left silent because this is the one step whose
        // failure mode is invisible: skipping it does not throw, it leaves a timer armed, and the
        // crash it eventually causes is a native access violation with no managed stack and no
        // connection back to here. The log line is the only evidence that the disarm ran at all.
        var pageName = ContentFrame.Content?.GetType().Name ?? "none";
        try
        {
            if (ContentFrame.Content is Views.IShutdownDisarmable page)
            {
                page.DisarmForShutdown();
                Serilog.Log.Information("Window teardown: disarmed page {Page}", pageName);
            }
            else
            {
                Serilog.Log.Information(
                    "Window teardown: page {Page} has nothing to disarm", pageName);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Page {Page} did not disarm cleanly during shutdown", pageName);
        }
    }

    private static AppWindow GetAppWindow(IntPtr hwnd)
    {
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    private void ConfigureCustomTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        _appWindow.Title = "ConnectOnion";
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.ico");
        if (File.Exists(iconPath))
        {
            _appWindow.SetIcon(iconPath);
        }
        ApplyTitleBarTheme(ElementTheme.Light);
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // EffectiveContentScale, not _zoomFactor. These two must agree with EffectiveContentWidth
        // below, because both feed SidebarLayoutPolicy.IsCompactWindow: with the OS text scale
        // above 100% the two definitions disagreed, so a resize and a navigation could reach
        // opposite conclusions about whether the sidebar should be an overlay.
        var windowWidth = e.NewSize.Width / EffectiveContentScale;
        var isCompact = SidebarLayoutPolicy.IsCompactWindow(windowWidth);
        if (isCompact && !_wasCompactLayout && _isSidebarVisible)
            SetSidebarVisible(false);
        _wasCompactLayout = isCompact;

        ApplySidebarWidth(windowWidth);
        ApplyRightWorkspaceWidth();
        UpdateTitleBarLayout(windowWidth);
        UpdateZoomLayout();
        UpdateZoomPopupPosition();
        UpdateFindOverlayPosition();
    }

    // Divided by the *effective* scale (zoom × OS text size), not the zoom alone: this is what
    // the compact-layout breakpoint is measured against, and at 150% system text the content
    // area really does have a third less room even though the zoom is still 100%.
    private double EffectiveContentWidth => RootGrid.ActualWidth / EffectiveContentScale;

    private void ApplySidebarWidth(double windowWidth)
    {
        if (!_isSidebarVisible)
        {
            ShellSidebarColumn.Width = new GridLength(0);
            SidebarResizeHandle.Visibility = Visibility.Collapsed;
            SidebarDismissLayer.Visibility = Visibility.Collapsed;
            return;
        }

        // Below the compact breakpoint the sidebar becomes an overlay. Keeping a fixed rail in
        // the layout at this width squeezed blocking approval controls and the reading column.
        // The existing title-bar toggle remains the single show/hide interaction.
        if (SidebarLayoutPolicy.IsCompactWindow(windowWidth))
        {
            // ColumnSpan 2 is load-bearing, not tidiness. The drawer keeps Grid.Column 0 while
            // that column is collapsed to zero, and a Grid arranges its child into the cell rect:
            // a zero-wide slot arranges to zero width, and neither HorizontalAlignment.Left nor
            // the explicit Width below makes it overflow. So the drawer took no space and never
            // painted, while the dismiss scrim — which already spanned both columns — did. The
            // symptom was a chat page that dimmed under a scrim with no sidebar over it, and the
            // toggle apparently doing nothing but darkening the page. Spanning both columns gives
            // the drawer the full content width to be arranged in; Left plus the explicit Width is
            // then what actually places it as a drawer.
            Grid.SetColumnSpan(ShellSidebar, 2);
            ShellSidebarColumn.Width = new GridLength(0);
            ShellSidebar.Width = SidebarLayoutPolicy.ClampOverlayWidth(_sidebarWidth, windowWidth);
            ShellSidebar.HorizontalAlignment = HorizontalAlignment.Left;
            SidebarResizeHandle.Visibility = Visibility.Collapsed;
            SidebarDismissLayer.Visibility = Visibility.Visible;
            return;
        }

        // Docked: back to a real column, so the span has to come back with it or the sidebar
        // would stretch across the content area.
        Grid.SetColumnSpan(ShellSidebar, 1);
        SidebarDismissLayer.Visibility = Visibility.Collapsed;
        ShellSidebar.ClearValue(FrameworkElement.WidthProperty);
        ShellSidebar.HorizontalAlignment = HorizontalAlignment.Stretch;
        ShellSidebarColumn.Width = new GridLength(
            SidebarLayoutPolicy.ClampDockedWidth(_sidebarWidth, windowWidth));
        SidebarResizeHandle.Visibility = Visibility.Visible;
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
        => ToggleSidebarCommand();

    private void ToggleSidebarCommand()
    {
        SetSidebarVisible(!_isSidebarVisible);
        if (_isSidebarVisible && SidebarLayoutPolicy.IsCompactWindow(EffectiveContentWidth))
            ShellSidebar.FocusFirstNavigation();
    }

    private void SetSidebarVisible(bool visible)
    {
        _isSidebarVisible = visible;
        ShellSidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SidebarToggleButton.Opacity = visible ? 1 : 0.65;
        ApplySidebarWidth(EffectiveContentWidth);
    }

    private void SidebarDismissLayer_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (!_isSidebarVisible || !SidebarLayoutPolicy.IsCompactWindow(EffectiveContentWidth))
            return;

        SetSidebarVisible(false);
        ContentFrame.Focus(FocusState.Programmatic);
    }

    private void SidebarResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(MainContentArea);
        if (!point.Properties.IsLeftButtonPressed) return;

        _sidebarResizeStartWidth = ShellSidebarColumn.ActualWidth > 0
            ? ShellSidebarColumn.ActualWidth
            : _sidebarWidth;
        _sidebarResizeStartPointerX = point.Position.X;
        _sidebarResizePointerId = e.Pointer.PointerId;
        _isSidebarResizing = SidebarResizeHandle.CapturePointer(e.Pointer);
        e.Handled = _isSidebarResizing;
    }

    private void SidebarResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSidebarResizing
            || e.Pointer.PointerId != _sidebarResizePointerId
            || !_isSidebarVisible
            || SidebarLayoutPolicy.IsCompactWindow(EffectiveContentWidth))
            return;

        var pointerX = e.GetCurrentPoint(MainContentArea).Position.X;
        var requestedWidth = _sidebarResizeStartWidth + pointerX - _sidebarResizeStartPointerX;
        if (SidebarLayoutPolicy.ShouldCollapseFromDrag(requestedWidth))
        {
            _isSidebarResizing = false;
            SidebarResizeHandle.ReleasePointerCaptures();
            SetSidebarVisible(false);
            ContentFrame.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }

        _sidebarWidth = SidebarLayoutPolicy.ClampDockedWidth(requestedWidth, EffectiveContentWidth);
        ApplySidebarWidth(EffectiveContentWidth);
        e.Handled = true;
    }

    private void SidebarResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _sidebarResizePointerId) return;
        EndSidebarResize();
        e.Handled = true;
    }

    private void SidebarResizeHandle_PointerCanceled(object sender, PointerRoutedEventArgs e)
        => EndSidebarResize();

    private void SidebarResizeHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => _isSidebarResizing = false;

    private void EndSidebarResize()
    {
        _isSidebarResizing = false;
        SidebarResizeHandle.ReleasePointerCaptures();
        if (_isSidebarVisible)
            ApplySidebarWidth(EffectiveContentWidth);
    }

    private void SidebarResizeHandle_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        const double keyboardStep = 16;
        if (e.Key == VirtualKey.Left)
        {
            if (_sidebarWidth <= SidebarLayoutPolicy.MinimumWidth + 0.5)
            {
                SetSidebarVisible(false);
                ContentFrame.Focus(FocusState.Programmatic);
            }
            else
            {
                _sidebarWidth = SidebarLayoutPolicy.ClampDockedWidth(
                    _sidebarWidth - keyboardStep,
                    EffectiveContentWidth);
                ApplySidebarWidth(EffectiveContentWidth);
            }

            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Right)
        {
            _sidebarWidth = SidebarLayoutPolicy.ClampDockedWidth(
                _sidebarWidth + keyboardStep,
                EffectiveContentWidth);
            ApplySidebarWidth(EffectiveContentWidth);
            e.Handled = true;
        }
    }

    private void RightWorkspaceResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(MainContentArea);
        if (!point.Properties.IsLeftButtonPressed || !_isRightWorkspaceExpanded) return;

        if (RightWorkspaceColumn.ActualWidth > 0)
            _rightWorkspaceResizeStartWidth = RightWorkspaceColumn.ActualWidth;
        else
            _rightWorkspaceResizeStartWidth = GetClampedRightWorkspaceWidth(_rightWorkspaceWidth);
        _rightWorkspaceResizeStartPointerX = point.Position.X;
        _rightWorkspaceResizePointerId = e.Pointer.PointerId;
        _isRightWorkspaceResizing = RightWorkspaceResizeHandle.CapturePointer(e.Pointer);
        e.Handled = _isRightWorkspaceResizing;
    }

    private void RightWorkspaceResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isRightWorkspaceResizing
            || e.Pointer.PointerId != _rightWorkspaceResizePointerId
            || !_isRightWorkspaceExpanded)
            return;

        var pointerX = e.GetCurrentPoint(MainContentArea).Position.X;
        var isRightToLeft = RootGrid.FlowDirection == FlowDirection.RightToLeft;
        double widthDelta;
        if (isRightToLeft)
            widthDelta = pointerX - _rightWorkspaceResizeStartPointerX;
        else
            widthDelta = _rightWorkspaceResizeStartPointerX - pointerX;
        _rightWorkspaceWidth = GetClampedRightWorkspaceWidth(
            _rightWorkspaceResizeStartWidth + widthDelta);
        ApplyRightWorkspaceWidth();
        e.Handled = true;
    }

    private void RightWorkspaceResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _rightWorkspaceResizePointerId) return;
        EndRightWorkspaceResize();
        e.Handled = true;
    }

    private void RightWorkspaceResizeHandle_PointerCanceled(object sender, PointerRoutedEventArgs e)
        => EndRightWorkspaceResize();

    private void RightWorkspaceResizeHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => _isRightWorkspaceResizing = false;

    private void EndRightWorkspaceResize()
    {
        _isRightWorkspaceResizing = false;
        RightWorkspaceResizeHandle.ReleasePointerCaptures();
        ApplyRightWorkspaceWidth();
    }

    private double GetClampedRightWorkspaceWidth(double requestedWidth)
    {
        var windowWidth = EffectiveContentWidth;
        if (!double.IsFinite(windowWidth) || windowWidth < 0) windowWidth = 0;

        double sidebarWidth;
        if (SidebarLayoutPolicy.IsCompactWindow(windowWidth))
            sidebarWidth = 0;
        else
            sidebarWidth = ShellSidebarColumn.Width.Value;
        var availableWidth = windowWidth - sidebarWidth - SidebarLayoutPolicy.MinimumContentWidth;
        var effectiveMaximum = Math.Clamp(
            availableWidth,
            RightWorkspaceMinimumWidth,
            RightWorkspaceMaximumWidth);
        if (!double.IsFinite(requestedWidth)) requestedWidth = 380;
        return Math.Clamp(requestedWidth, RightWorkspaceMinimumWidth, effectiveMaximum);
    }

    private void ApplyRightWorkspaceWidth()
    {
        if (!_isRightWorkspaceExpanded) return;
        RightWorkspaceColumn.Width = new GridLength(GetClampedRightWorkspaceWidth(_rightWorkspaceWidth));
    }

    private void Back_Click(object sender, RoutedEventArgs e)
        => BackCommand();

    private void BackCommand()
    {
        NavigateHistory(backward: true);
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
        => ForwardCommand();

    private void ForwardCommand()
        => NavigateHistory(backward: false);

    /// <summary>
    /// Moves through Frame history after restoring the entity that the destination represented.
    /// A page type alone is not enough: every conversation is a <see cref="ChatPage"/> and every
    /// agent uses one cached <see cref="AgentDetailPage"/> instance, while their selected ids live
    /// in SQLite. Restoring those ids before Frame creates the page keeps the sidebar, header and
    /// transcript on the same historical destination.
    /// </summary>
    private async void NavigateHistory(bool backward)
    {
        if (_isHistoryNavigationInProgress) return;

        _isHistoryNavigationInProgress = true;
        try
        {
            var outgoingType = ContentFrame.CurrentSourcePageType;
            var outgoingContext = _currentNavigationContext
                ?? await CaptureNavigationContextAsync(outgoingType);
            var history = backward ? ContentFrame.BackStack : ContentFrame.ForwardStack;

            // A non-current agent or conversation can be deleted without resetting the visible
            // page. Drop such stale history entries instead of reopening an entity-less page.
            while (history.Count > 0)
            {
                var target = history[history.Count - 1];
                if (!await RestoreNavigationContextAsync(target.SourcePageType, target.Parameter))
                {
                    history.RemoveAt(history.Count - 1);
                    continue;
                }

                if (backward)
                {
                    ContentFrame.GoBack(new SuppressNavigationTransitionInfo());
                    ReplaceLatestHistoryEntry(ContentFrame.ForwardStack, outgoingType, outgoingContext);
                }
                else
                {
                    ContentFrame.GoForward();
                    ReplaceLatestHistoryEntry(ContentFrame.BackStack, outgoingType, outgoingContext);
                }
                _currentNavigationContext = HistoryContext(target.Parameter);
                return;
            }
        }
        catch
        {
            // History is a convenience. A storage race or stale entry must leave the current page
            // intact rather than surface an async-void exception through the WinUI dispatcher.
        }
        finally
        {
            _isHistoryNavigationInProgress = false;
            UpdateNavigationButtons();
        }
    }

    /// <summary>True while any full-window modal is
    /// showing. The dimmed backdrop already blocks the mouse; the global menu accelerators bubble
    /// up through RootGrid regardless of hit-testing, so they must additionally bail on this to
    /// keep a modal from acting on the content behind it.</summary>
    private bool IsModalOverlayOpen
        => _addAgentOverlay?.IsOpen == true
            || _settingsOverlay?.IsOpen == true
            || _keyboardShortcutsDialog?.IsOpen == true
            || _aboutOverlay?.IsOpen == true
            || _sessionSearchOverlay?.IsOpen == true;

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = ContentFrame.CanGoBack;
        ForwardButton.IsEnabled = ContentFrame.CanGoForward;
        FindMenuItem.IsEnabled = ContentFrame.Content is Views.IFindHost;

        // Disabled Back/Forward keep the title bar's own (transparent) background — see
        // their inline ButtonBackgroundDisabled override in XAML — so the muted arrow color
        // is the only disabled cue.
        BackIcon.Foreground = NavIconBrush(ContentFrame.CanGoBack);
        ForwardIcon.Foreground = NavIconBrush(ContentFrame.CanGoForward);
    }

    private static Microsoft.UI.Xaml.Media.Brush NavIconBrush(bool isEnabled)
        => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            isEnabled ? "TextSecondaryBrush" : "TextDisabledBrush"];

    private void OnSessionsChanged()
        => DispatcherQueue.TryEnqueue(async () =>
        {
            try { await ShellSidebar.RefreshAsync(); }
            catch { /* Best-effort refresh on session change. */ }
            try { await RefreshTrayRecentChatsAsync(); }
            catch { /* Best-effort tray shortcut refresh. */ }
        });

    private void ApplyTitleBarTheme(ElementTheme theme)
    {
        var titleBar = _appWindow.TitleBar;
        titleBar.BackgroundColor = Colors.Transparent;
        titleBar.InactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        var isDark = theme == ElementTheme.Dark;
        var textPrimary = ThemeService.GetColor("TextPrimaryColor", isDark);
        var textSecondary = ThemeService.GetColor("TextSecondaryColor", isDark);

        titleBar.ForegroundColor = textPrimary;
        titleBar.InactiveForegroundColor = textSecondary;
        titleBar.ButtonForegroundColor = textPrimary;
        titleBar.ButtonInactiveForegroundColor = textSecondary;
        titleBar.ButtonHoverBackgroundColor = ThemeService.GetColor("SurfaceHoverColor", isDark);
        titleBar.ButtonHoverForegroundColor = textPrimary;
        titleBar.ButtonPressedBackgroundColor = ThemeService.GetColor("SurfacePressedColor", isDark);
        titleBar.ButtonPressedForegroundColor = textPrimary;
    }

    private void ApplyDashboardTheme(ElementTheme theme)
    {
        // Handle's reconnection
        DispatcherQueue.TryEnqueue(() =>
            SetReconnectErrorVisualState(ReconnectErrorBadge.Visibility == Visibility.Visible));

        var core = ControlCenterWebView.CoreWebView2;
        if (core is null) return;

        core.Profile.PreferredColorScheme = theme == ElementTheme.Dark
            ? CoreWebView2PreferredColorScheme.Dark
            : CoreWebView2PreferredColorScheme.Light;
    }

    private System.Threading.Tasks.Task ApplyStartupThemeAsync()
    {
        ThemeService.Apply(AppServices.StartupState.Preferences.Theme);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>Reduces secondary title-bar chrome before it can collide with the caption buttons.
    /// The compact More menu mirrors Edit/View, so pointer and touch users do not lose commands
    /// merely because the window is narrow.</summary>
    private void UpdateTitleBarLayout(double windowWidth)
    {
        var compact = windowWidth < 720;
        EditMenuBarItem.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ViewMenuBarItem.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactMenuBarItem.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        ForwardButton.Visibility = windowWidth < 560 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Gives the content frame focus only when nothing in the window has taken it yet.
    ///
    /// The check is the whole point: pages focus their own composer from <c>Loaded</c>
    /// (<c>ChatPage</c>, <c>AgentDetailPage</c>), which races this handler, and an unconditional
    /// focus would pull the caret out of the message box the user is about to type into.
    /// <c>HomePage</c> focuses nothing, so without this the window would sit with no focused
    /// element at all — and <c>firstInteractive</c>, which is raised by <c>RootGrid.GotFocus</c>,
    /// would never be reached on the default route.
    /// </summary>
    private void EnsureInitialFocus()
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot);

        // Shell initialization is asynchronous. The user can open a modal before it finishes,
        // and that modal owns focus from then on; moving focus back into the page here would make
        // the first field look ready while sending keystrokes somewhere else.
        if (IsModalOverlayOpen) return;

        // On first activation Windows assigns keyboard focus to the first tab stop before the
        // async shell load completes. In this XAML that is the sidebar toggle, which made the
        // control look selected even though the user had not touched it. Home has no editor that
        // could own a caret, so move only that provisional shell focus into the content frame.
        // Any other focused element reflects a real user/page choice and must be preserved.
        if (ContentFrame.CurrentSourcePageType == typeof(HomePage)
            && (focused is null || ReferenceEquals(focused, SidebarToggleButton)))
        {
            ContentFrame.Focus(FocusState.Programmatic);
            return;
        }

        if (focused is not null) return;

        ContentFrame.Focus(FocusState.Programmatic);
    }

    /// <summary>Two-parameter shape kept because it is what <c>ShellSidebar.NavigationRequested</c>
    /// (an <c>Action&lt;Type, bool&gt;</c>) binds to — a method with an extra optional parameter
    /// does not convert to that delegate.</summary>
    internal void NavigateTo(Type page, bool forceReload = false)
        => NavigateTo(page, forceReload, parameter: null);

    /// <param name="parameter">Optional navigation parameter, read by the target page in
    /// <c>OnNavigatedTo</c>. Only reaches a page that is actually navigated to — the
    /// <see cref="Views.IReloadablePage"/> fast path below reloads the live page instead, and a
    /// page using this must therefore not depend on it arriving on every call.</param>
    internal async void NavigateTo(
        Type page,
        bool forceReload,
        object? parameter,
        NavigationTransitionInfo? transitionInfo = null)
    {
        // Lightweight pages may reload in place. ChatPage intentionally does not implement
        // IReloadablePage: each conversation switch must destroy the old ListView and its native
        // RichTextBlock/DataTemplate recycle pool.
        if (forceReload
            && ContentFrame.CurrentSourcePageType == page
            && ContentFrame.Content is Views.IReloadablePage reloadable)
        {
            var targetContext = await CaptureNavigationContextAsync(page);
            CloseFindOverlay();
            // Selection is persisted before navigation is requested. Paint that new selection
            // before reloading the page: AgentDetailPage.ReloadAsync can wait on a remote /info
            // fetch, and putting this refresh afterwards left the previous agent highlighted for
            // the entire request.
            try { await ShellSidebar.RefreshAsync(); }
            catch { /* best-effort sidebar refresh */ }
            try
            {
                await reloadable.ReloadAsync();
                _currentNavigationContext = targetContext;
            }
            catch { /* a failed reload leaves the page on its previous selection */ }
            return;
        }

        if (forceReload || ContentFrame.CurrentSourcePageType != page)
        {
            var outgoingType = ContentFrame.CurrentSourcePageType;
            // Sidebar clicks persist their new selection before raising NavigationRequested. The
            // repository therefore already describes the target here, not the page still on
            // screen. Keep the visible page's last captured context for the outgoing history row.
            var outgoingContext = _currentNavigationContext
                ?? await CaptureNavigationContextAsync(outgoingType);
            var targetContext = await CaptureNavigationContextAsync(page, parameter);

            // Page changes are instant; default to no transition rather than Frame's slide-up.
            ContentFrame.Navigate(page, targetContext, transitionInfo ?? new SuppressNavigationTransitionInfo());

            // Frame copied the parameter with which the outgoing page was first opened. That can
            // be stale after an in-place agent reload, so replace the new history entry with a
            // snapshot of the entity that was actually visible when the user left.
            ReplaceLatestHistoryEntry(ContentFrame.BackStack, outgoingType, outgoingContext);
            _currentNavigationContext = targetContext with { Payload = null };

            // Keep the entry that Frame just added to BackStack. ChatPage disables navigation
            // caching and disposes its native-heavy visual tree from OnNavigatedFrom, so retaining
            // the lightweight PageStackEntry does not retain the old page. Removing that entry
            // here used to leave CanGoBack/CanGoForward permanently false because every sidebar
            // destination is requested with forceReload.
        }
    }

    private async System.Threading.Tasks.Task<ShellNavigationContext> CaptureNavigationContextAsync(
        Type? page,
        object? payload = null)
    {
        if (page == typeof(ChatPage))
        {
            var conversationId = await AppServices.Sessions.GetActiveSessionIdAsync();
            var session = conversationId is null
                ? null
                : await AppServices.Sessions.GetSessionAsync(conversationId);
            return new ShellNavigationContext(session?.AgentId, session?.Id, payload);
        }

        if (page == typeof(AgentDetailPage))
        {
            var agents = await AppServices.Agents.LoadSummariesAsync();
            return new ShellNavigationContext(agents.SelectedAgentId, null, payload);
        }

        return new ShellNavigationContext(null, null, payload);
    }

    private static void ReplaceLatestHistoryEntry(
        IList<PageStackEntry> history,
        Type? page,
        ShellNavigationContext context)
    {
        if (page is null || history.Count == 0) return;

        var index = history.Count - 1;
        var oldEntry = history[index];
        history[index] = new PageStackEntry(page, context, oldEntry.NavigationTransitionInfo);
    }

    private static ShellNavigationContext HistoryContext(object? parameter)
        => parameter is ShellNavigationContext context
            ? context with { Payload = null }
            : new ShellNavigationContext(null, null, null);

    private async System.Threading.Tasks.Task<bool> RestoreNavigationContextAsync(
        Type page,
        object? parameter)
    {
        if (page == typeof(HomePage)) return true;
        if (parameter is not ShellNavigationContext context) return false;

        if (page == typeof(ChatPage))
        {
            if (context.ConversationId is null) return false;

            var session = await AppServices.Sessions.GetSessionAsync(context.ConversationId);
            if (session is null
                || (context.AgentId is not null && session.AgentId != context.AgentId)) return false;

            await AppServices.Agents.SetSelectedAgentAsync(session.AgentId);
            await AppServices.Sessions.SetActiveSessionAsync(session.Id);
            return true;
        }

        if (page == typeof(AgentDetailPage))
        {
            if (context.AgentId is null) return false;

            var agents = await AppServices.Agents.LoadSummariesAsync();
            if (agents.Agents.All(agent => agent.Id != context.AgentId)) return false;

            await AppServices.Agents.SetSelectedAgentAsync(context.AgentId);
            return true;
        }

        return true;
    }

    /// <summary>
    /// Establishes a fresh navigation root after destructive changes such as deleting the agent
    /// represented by the current page. Entity-backed entries are deliberately removed from both
    /// history stacks so Back/Forward cannot resurrect a page whose storage graph no longer exists.
    /// </summary>
    private void ResetNavigationTo(Type page)
    {
        CloseFindOverlay();
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page, null, new SuppressNavigationTransitionInfo());

        ContentFrame.BackStack.Clear();
        ContentFrame.ForwardStack.Clear();
        _currentNavigationContext = new ShellNavigationContext(null, null, null);
        UpdateNavigationButtons();
    }

    /// <summary>Opens the shell-owned modal without changing the current page.</summary>
    internal void ShowAddAgentOverlay(FrameworkElement? opener = null)
        => EnsureAddAgentOverlay().Show(opener);

    private void AddAgentOverlay_AgentAdded(Models.AgentConfig agent)
    {
        // AddAgentViewModel persists the new agent and selects it in one transaction. Close
        // Settings when that was the entry point so its full-window overlay cannot hide the
        // destination; NavigateTo refreshes the sidebar before a same-page reload, while an actual
        // Frame navigation refreshes it from ContentFrame.Navigated.
        CloseSettingsOverlay();
        NavigateTo(typeof(AgentDetailPage), forceReload: true);
    }

    internal System.Threading.Tasks.Task<bool> DeleteAgentAsync(string agentId)
        => ShellSidebar.DeleteAgentAsync(agentId);

    private void ShowSettingsOverlay(FrameworkElement? opener = null)
        => EnsureSettingsOverlay().Show(opener);

    private void CloseSettingsOverlay()
        => _settingsOverlay?.Hide();

    // Control Centre settings and Dashboard
    private bool _controlCentreReady;
    private string? _controlCentreAgentId;
    private long _controlCentreAgentLoadGeneration;
    private string _savedDisplayName = string.Empty;
    private int _savedReconnectSeconds = 15;
    private bool _hasAppliedUnsavedReconnectSeconds;
    private string _appliedDisplayName = string.Empty;
    private bool _hasAppliedUnsavedDisplayName;


    // Workspace tab switching
    private void UpdateRightWorkspaceForCurrentPage()
    {
        var page = ContentFrame.CurrentSourcePageType;
        bool isAvailable = page == typeof(ChatPage) || page == typeof(AgentDetailPage);
        if (!isAvailable)
            _isRightWorkspaceExpanded = false;

        bool isExpanded = isAvailable && _isRightWorkspaceExpanded;

        if (isExpanded) ApplyRightWorkspaceWidth();
        else RightWorkspaceColumn.Width = new GridLength(0);

        if (isExpanded) RightWorkspacePane.Visibility = Visibility.Visible;
        else RightWorkspacePane.Visibility = Visibility.Collapsed;

        if (isExpanded)
            RightWorkspaceResizeHandle.Visibility = Visibility.Visible;
        else
            RightWorkspaceResizeHandle.Visibility = Visibility.Collapsed;
        if (!isExpanded && _isRightWorkspaceResizing)
        {
            _isRightWorkspaceResizing = false;
            RightWorkspaceResizeHandle.ReleasePointerCaptures();
        }

        if (isAvailable && !isExpanded) ExpandRightPaneButton.Visibility = Visibility.Visible;
        else ExpandRightPaneButton.Visibility = Visibility.Collapsed;
    }

    // Apply, Save, discard
    private void OnControlCentreFieldEdited(object sender, TextChangedEventArgs e)
    {
        if (!_controlCentreReady) return;
        EvaluateControlCentreState(isApplyAttempt: false);
    }

    private void OnApplyControlCentreClicked(object sender, RoutedEventArgs e)
    {
        if (TryValidateReconnectSeconds(out int seconds))
        {
            _hasAppliedUnsavedReconnectSeconds = seconds != _savedReconnectSeconds;
            AppServices.StartupState.Preferences.ReconnectDelaySeconds = seconds;
        }
        EvaluateControlCentreState(isApplyAttempt: true);
    }

    /// <summary>Loads the current agent's name to the control-centre editor </summary>
    private async System.Threading.Tasks.Task RefreshControlCentreAgentAsync()
    {
        var generation = ++_controlCentreAgentLoadGeneration;
        var agent = await AppServices.Agents.GetSelectedAgentAsync();
        if (generation != _controlCentreAgentLoadGeneration) return;

        var selectedAgentChanged = !string.Equals(
            _dashboardAgentId,
            agent?.Id,
            StringComparison.Ordinal);
        _dashboardAgentId = agent?.Id;
        _dashboardAgentConfig = agent;

        if (selectedAgentChanged)
            await LoadAgentDashboardAsync(agent?.Id, forceReload: false);

        // Preserve edits when a navigating between the same agent
        if (agent?.Id == _controlCentreAgentId
            && (!string.Equals(AgentDisplayNameInput.Text.Trim(), _savedDisplayName, StringComparison.Ordinal)
                || _hasAppliedUnsavedDisplayName))
        {
            return;
        }

        _controlCentreAgentId = agent?.Id;
        _savedDisplayName = agent?.DisplayName ?? string.Empty;
        _appliedDisplayName = _savedDisplayName;
        _hasAppliedUnsavedDisplayName = false;

        _controlCentreReady = false;
        AgentDisplayNameInput.Text = _savedDisplayName;
        _controlCentreReady = true;
        DisplayNameUnsavedBadge.Visibility = Visibility.Collapsed;

        EvaluateControlCentreState(isApplyAttempt: false);
    }

    private async void OnSaveControlCentreClicked(object sender, RoutedEventArgs e)
    {
        bool reconnectValid = TryValidateReconnectSeconds(out int seconds);
        bool displayNameValid = _controlCentreAgentId is null
            || Models.AgentConfig.IsValidName(AgentDisplayNameInput.Text);
        if (!reconnectValid || !displayNameValid)
        {
            EvaluateControlCentreState(isApplyAttempt: true);
            return;
        }

        var displayName = AgentDisplayNameInput.Text.Trim();
        var agentId = _controlCentreAgentId;
        bool displayNameChanged = agentId is not null
            && !string.Equals(displayName, _savedDisplayName, StringComparison.Ordinal);

        // Update only this agent's name so other agent changes made at the same time are preserved.
        if (displayNameChanged)
        {
            try
            {
                if (!await AppServices.Agents.UpdateNameAsync(agentId!, displayName))
                {
                    ValidationWarningBannerText.Text = "Could not save the agent display name. The selected agent may have changed.";
                    ValidationWarningBanner.Visibility = Visibility.Visible;
                    return;
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Could not save the agent display name");
                ValidationWarningBannerText.Text = "Could not save the display name. Try again.";
                ValidationWarningBanner.Visibility = Visibility.Visible;
                return;
            }

            if (_controlCentreAgentId == agentId)
            {
                _savedDisplayName = displayName;
                _appliedDisplayName = displayName;
                _hasAppliedUnsavedDisplayName = false;
                DisplayNameUnsavedBadge.Visibility = Visibility.Collapsed;
            }
            await RefreshRenamedAgentViewsAsync(agentId!);
        }

        try
        {
            // Reload before saving because other settings surfaces may have saved newer
            // preferences since startup. This preserves those values while updating only the
            // reconnect delay.
            var preferences = await _preferencesRepository.LoadAsync();
            preferences.ReconnectDelaySeconds = seconds;
            await _preferencesRepository.SaveAsync(preferences);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not save the automatic reconnect delay");
            ValidationWarningBannerText.Text = "Could not save the reconnect delay. Try again.";
            ValidationWarningBanner.Visibility = Visibility.Visible;
            return;
        }

        if (_controlCentreAgentId == agentId)
        {
            _savedDisplayName = displayName;
            _appliedDisplayName = displayName;
            _hasAppliedUnsavedDisplayName = false;
        }
        _savedReconnectSeconds = seconds;
        _hasAppliedUnsavedReconnectSeconds = false;
        AppServices.StartupState.Preferences.ReconnectDelaySeconds = seconds;

        DisplayNameUnsavedBadge.Visibility = Visibility.Collapsed;
        ValidationWarningBanner.Visibility = Visibility.Collapsed;
        SetReconnectErrorVisualState(isError: false);

    }

    private void OnDiscardControlCentreClicked(object sender, RoutedEventArgs e)
    {
        AgentDisplayNameInput.Text = _savedDisplayName;
        ReconnectAfterInput.Text = _savedReconnectSeconds.ToString(CultureInfo.CurrentCulture);
        _hasAppliedUnsavedReconnectSeconds = false;
        AppServices.StartupState.Preferences.ReconnectDelaySeconds = _savedReconnectSeconds;
        _hasAppliedUnsavedDisplayName = false;

        DisplayNameUnsavedBadge.Visibility = Visibility.Collapsed;
        ValidationWarningBanner.Visibility = Visibility.Collapsed;
        SetReconnectErrorVisualState(isError: false);

    }

    private void EvaluateControlCentreState(bool isApplyAttempt)
    {
        bool reconnectValid = TryValidateReconnectSeconds(out _);
        bool displayNameValid = _controlCentreAgentId is null
            || Models.AgentConfig.IsValidName(AgentDisplayNameInput.Text);
        bool displayNameChanged = _controlCentreAgentId is not null
            && !string.Equals(
                AgentDisplayNameInput.Text.Trim(),
                _savedDisplayName,
                StringComparison.Ordinal);

        if (isApplyAttempt && displayNameChanged && displayNameValid)
        {
            _appliedDisplayName = AgentDisplayNameInput.Text.Trim();
            _hasAppliedUnsavedDisplayName = true;
        }

        DisplayNameUnsavedBadge.Visibility = _hasAppliedUnsavedDisplayName
            ? Visibility.Visible
            : Visibility.Collapsed;

        SetReconnectErrorVisualState(isError: !reconnectValid);

        int appliedCount = (_hasAppliedUnsavedDisplayName ? 1 : 0)
            + (_hasAppliedUnsavedReconnectSeconds ? 1 : 0);
        if (isApplyAttempt && !reconnectValid)
        {
            ValidationWarningBannerText.Text =
                $"Could not apply \"Reconnect after\" — Enter a value from 5 to 300 seconds. {appliedCount} change applied.";
            ValidationWarningBanner.Visibility = Visibility.Visible;
        }
        else if (isApplyAttempt && !displayNameValid)
        {
            ValidationWarningBannerText.Text =
                $"Could not apply \"Display name\" — Enter a name from 1 to {Models.AgentConfig.MaxNameLength} characters. {appliedCount} change applied.";
            ValidationWarningBanner.Visibility = Visibility.Visible;
        }
        else if (reconnectValid)
        {
            ValidationWarningBanner.Visibility = Visibility.Collapsed;
        }
    }

    private bool TryValidateReconnectSeconds(out int seconds)
    {
        if (int.TryParse(ReconnectAfterInput.Text.Trim(), out seconds) && seconds >= 5 && seconds <= 300)
        {
            return true;
        }
        seconds = _savedReconnectSeconds;
        return false;
    }

    private void SetReconnectErrorVisualState(bool isError)
    {
        ReconnectAfterCardBorder.Background = ThemeBrushResolver.Resolve(isError ? "DangerSubtleBrush" : "SurfacePrimaryBrush");
        ReconnectAfterCardBorder.BorderBrush = ThemeBrushResolver.Resolve(isError ? "DangerBrush" : "BorderDefaultBrush");
        ReconnectAfterInput.Background = ThemeBrushResolver.Resolve(isError ? "DangerSubtleBrush" : "ChatInputBackgroundBrush");
        ReconnectAfterInput.BorderBrush = ThemeBrushResolver.Resolve(isError ? "DangerBrush" : "BorderStrongBrush");

        ReconnectErrorBadge.Visibility = isError ? Visibility.Visible : Visibility.Collapsed;
        ReconnectInlineErrorRow.Visibility = isError ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnJumpToInvalidFieldClicked(object sender, RoutedEventArgs e)
    {
        ReconnectAfterInput.Focus(FocusState.Programmatic);
        ReconnectAfterInput.SelectAll();
    }

    private void OnEditAllowedToolsClicked(object sender, RoutedEventArgs e)
    {
        // Hook for opening tool permission selector
    }

    private async void OnRefreshDashboardClicked(object sender, RoutedEventArgs e)
    {
        await LoadAgentDashboardAsync(_dashboardAgentId, forceReload: true);
    }

    private async Task LoadAgentDashboardAsync(string? agentId, bool forceReload)
    {
        var generation = ++_dashboardLoadGeneration;
        if (string.IsNullOrWhiteSpace(agentId))
        {
            _dashboardBridgeToken = null;
            SetDashboardStatus("Select an agent to view its dashboard.");
            return;
        }

        var agent = _dashboardAgentConfig;
        if (agent is null || !string.Equals(agent.Id, agentId, StringComparison.Ordinal))
        {
            SetDashboardStatus("Select an agent to view its dashboard.");
            return;
        }

        if (!await EnsureDashboardWebViewAsync()) return;
        if (generation != _dashboardLoadGeneration || agentId != _dashboardAgentId) return;

        var snapshot = AppServices.RunManager.GetLatestDashboardSnapshot(agentId);
        if (snapshot is not null)
        {
            RenderDashboardSnapshot(snapshot);
        }
        else
        {
            SetDashboardStatus("Connecting to the agent to receive its dashboard…");
        }

        try
        {
            var session = await _sessionRepository.ResolveForAgentAsync(agentId);
            if (generation != _dashboardLoadGeneration || agentId != _dashboardAgentId) return;
            await AppServices.RunManager.ConnectDashboardAsync(agent, session?.Id, refresh: forceReload);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not connect to the agent dashboard");
            if (generation == _dashboardLoadGeneration
                && agentId == _dashboardAgentId
                && AppServices.RunManager.GetLatestDashboardSnapshot(agentId) is null)
            {
                SetDashboardStatus("Could not connect to the agent dashboard. Check the agent connection and refresh.");
            }
        }
    }

    private void RenderDashboardSnapshot(DashboardSnapshot snapshot)
    {
        if (ControlCenterWebView.CoreWebView2 is null) return;

        try
        {
            _dashboardBridgeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var document = BuildDashboardDocument(snapshot.Html, _dashboardBridgeToken);
            _dashboardDocumentSource = BuildDashboardDataUri(document);
            _dashboardPendingNavigationSource = _dashboardDocumentSource;
            DashboardStatusOverlay.Visibility = Visibility.Visible;
            DashboardStatusText.Text = "Loading dashboard…";
            ControlCenterWebView.NavigateToString(document);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not render the agent dashboard snapshot");
            SetDashboardStatus("The dashboard could not be displayed. Refresh to try again.");
        }
    }

    private async Task<bool> EnsureDashboardWebViewAsync()
    {
        if (_dashboardWebViewInitialisation is { } initialised)
            return await initialised;

        var initialisation = InitialiseDashboardWebViewAsync();
        _dashboardWebViewInitialisation = initialisation;
        var succeeded = await initialisation;
        if (!succeeded) _dashboardWebViewInitialisation = null;
        return succeeded;
    }

    private async Task<bool> InitialiseDashboardWebViewAsync()
    {
        try
        {
            await ControlCenterWebView.EnsureCoreWebView2Async();
            var core = ControlCenterWebView.CoreWebView2;
            var settings = core.Settings;
            ApplyDashboardTheme(ThemeService.CurrentTheme);
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsWebMessageEnabled = true;
            settings.IsZoomControlEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.IsBuiltInErrorPageEnabled = false;

            core.NavigationStarting += OnDashboardNavigationStarting;
            core.NavigationCompleted += OnDashboardNavigationCompleted;
            core.FrameCreated += OnDashboardFrameCreated;
            core.WebMessageReceived += OnDashboardWebMessageReceived;
            core.NewWindowRequested += OnDashboardNewWindowRequested;
            core.DownloadStarting += OnDashboardDownloadStarting;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnDashboardWebResourceRequested;
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not initialise the agent dashboard WebView2");
            SetDashboardStatus("WebView2 could not be started. Refresh to try again.");
            return false;
        }
    }

    private void OnDashboardNavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (string.Equals(args.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)
            || string.Equals(args.Uri, "about:srcdoc", StringComparison.OrdinalIgnoreCase)) return;

        if (string.Equals(args.Uri, _dashboardPendingNavigationSource, StringComparison.Ordinal))
        {
            _dashboardPendingNavigationSource = null;
            return;
        }

        args.Cancel = true;
        var scheme = Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ? uri.Scheme : "unknown";
        Serilog.Log.Information(
            "Blocked agent dashboard navigation with scheme {Scheme} and length {UriLength}",
            scheme,
            args.Uri.Length);
    }

    private void OnDashboardNavigationCompleted(
        CoreWebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess)
        {
            DashboardStatusOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        Serilog.Log.Warning(
            "Agent dashboard navigation failed for {Source}: {WebErrorStatus} (HTTP {HttpStatusCode})",
            sender.Source,
            args.WebErrorStatus,
            args.HttpStatusCode);
        _dashboardPendingNavigationSource = null;
        SetDashboardStatus("The dashboard could not be displayed. Refresh to try again.");
    }

    private void OnDashboardFrameCreated(
        CoreWebView2 sender,
        CoreWebView2FrameCreatedEventArgs args)
    {
        var frame = args.Frame;
        frame.NavigationStarting += (_, navigation) =>
        {
            if (string.Equals(navigation.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)
                || string.Equals(navigation.Uri, "about:srcdoc", StringComparison.OrdinalIgnoreCase)) return;

            navigation.Cancel = true;
            var scheme = Uri.TryCreate(navigation.Uri, UriKind.Absolute, out var uri) ? uri.Scheme : "unknown";
            Serilog.Log.Information("Blocked agent dashboard frame navigation with scheme {Scheme}", scheme);
        };
        frame.NavigationCompleted += (_, navigation) =>
        {
            if (!navigation.IsSuccess)
            {
                Serilog.Log.Warning(
                    "Agent dashboard frame navigation failed: {WebErrorStatus} (HTTP {HttpStatusCode})",
                    navigation.WebErrorStatus,
                    navigation.HttpStatusCode);
            }
        };
    }

    private void OnDashboardWebMessageReceived(
        CoreWebView2 sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!string.Equals(args.Source, "about:blank", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(args.Source, _dashboardDocumentSource, StringComparison.Ordinal)) return;

        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "run_skill"
                || !root.TryGetProperty("token", out var token)
                || token.ValueKind != JsonValueKind.String
                || token.GetString() != _dashboardBridgeToken
                || !root.TryGetProperty("skill", out var skillElement)
                || skillElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var skill = skillElement.GetString();
            var arguments = root.TryGetProperty("args", out var argsElement)
                && argsElement.ValueKind == JsonValueKind.String
                    ? argsElement.GetString() ?? string.Empty
                    : string.Empty;
            if (skill is null || skill.Length > 128 || arguments.Length > 4096) return;

            _ = RunDashboardSkillSafelyAsync(skill, arguments);
        }
        catch (JsonException)
        {
            // Ignore malformed WebView messages, dashboard is untrusted
        }
    }

    private async Task RunDashboardSkillSafelyAsync(string skill, string arguments)
    {
        try
        {
            await RunDashboardSkillAsync(skill, arguments);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not run a dashboard skill action");
            SetDashboardStatus("The dashboard action could not be sent to Chat.");
        }
    }

    private async Task RunDashboardSkillAsync(string skill, string arguments)
    {
        var agent = _dashboardAgentConfig;
        if (agent is null || !string.Equals(agent.Id, _dashboardAgentId, StringComparison.Ordinal)) return;

        var publishedSkills = EndpointResolver.ParseSkillsFromInfoJson(agent.InfoJson);
        if (ContentFrame.Content is ChatPage currentChat
            && string.Equals(currentChat.Vm.AgentId, agent.Id, StringComparison.Ordinal)
            && currentChat.Vm.Skills is { } chatSkills)
        {
            publishedSkills = chatSkills;
        }
        else if (ContentFrame.Content is AgentDetailPage detailPage
                 && string.Equals(detailPage.Vm.Agent?.Id, agent.Id, StringComparison.Ordinal)
                 && detailPage.Vm.Skills is { } detailSkills)
        {
            publishedSkills = detailSkills;
        }

        if (!publishedSkills.Any(candidate =>
                string.Equals(candidate.Name, skill, StringComparison.Ordinal)))
        {
            SetDashboardStatus($"The agent has not published the “{skill}” skill.");
            return;
        }

        var prompt = $"/{skill}" + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : $" {arguments.Trim()}");
        if (ContentFrame.Content is ChatPage chatPage
            && string.Equals(chatPage.Vm.AgentId, agent.Id, StringComparison.Ordinal))
        {
            if (!chatPage.Vm.CanSend || chatPage.Vm.IsProcessing)
            {
                SetDashboardStatus("Open the chat when the agent is idle to run a dashboard action.");
                return;
            }

            await chatPage.Vm.SendAsync(prompt);
            return;
        }

        if (ContentFrame.Content is AgentDetailPage agentDetailPage
            && string.Equals(agentDetailPage.Vm.Agent?.Id, agent.Id, StringComparison.Ordinal)
            && await agentDetailPage.Vm.StartConversationAsync(prompt))
        {
            NavigateTo(typeof(ChatPage), forceReload: true, parameter: prompt);
            return;
        }

        SetDashboardStatus("Open this agent’s chat to run a dashboard action.");
    }

    private void OnDashboardNewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs args)
        => args.Handled = true;

    private static void OnDashboardDownloadStarting(
        CoreWebView2 sender,
        CoreWebView2DownloadStartingEventArgs args)
        => args.Cancel = true;

    private void OnDashboardWebResourceRequested(
        CoreWebView2 sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        args.Response = sender.Environment.CreateWebResourceResponse(
            new Windows.Storage.Streams.InMemoryRandomAccessStream(),
            403,
            "Blocked",
            "Content-Type: text/plain; charset=utf-8");
    }

    private void SetDashboardStatus(string message)
    {
        DashboardStatusText.Text = message;
        DashboardStatusOverlay.Visibility = Visibility.Visible;
    }

    private void OnAgentDashboardSnapshotReceived(string agentId, DashboardSnapshot snapshot)
    {
        void ShowSnapshot()
        {
            if (!string.Equals(agentId, _dashboardAgentId, StringComparison.Ordinal)) return;
            RenderDashboardSnapshot(snapshot);
        }

        if (DispatcherQueue.HasThreadAccess)
            ShowSnapshot();
        else
            DispatcherQueue.TryEnqueue(ShowSnapshot);
    }

    private static string BuildDashboardDocument(string html, string bridgeToken)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var childHtml = BuildSandboxedDashboardHtml(html, nonce, bridgeToken);
        var encodedHtml = Convert.ToBase64String(Encoding.UTF8.GetBytes(childHtml));
        return DashboardShell
            .Replace("__NONCE__", nonce, StringComparison.Ordinal)
            .Replace("__TOKEN__", bridgeToken, StringComparison.Ordinal)
            .Replace("__HTML__", encodedHtml, StringComparison.Ordinal);
    }

    private static string BuildDashboardDataUri(string document)
        => $"data:text/html;charset=utf-8;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(document))}";

    private static string BuildSandboxedDashboardHtml(string html, string nonce, string bridgeToken)
    {
        var policy = $"""
            <meta http-equiv="Content-Security-Policy"
                  content="default-src 'none';
                           img-src data:;
                           style-src 'unsafe-inline' data:;
                           script-src 'nonce-{nonce}';
                           font-src data:;
                           media-src data:;
                           connect-src 'none';
                           frame-src 'none';
                           object-src 'none';
                           base-uri 'none';
                           form-action 'none'">
            <meta name="color-scheme" content="light dark">
            """;
        var securedHtml = html;
        var headStart = securedHtml.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (headStart >= 0)
        {
            var headEnd = securedHtml.IndexOf('>', headStart);
            if (headEnd >= 0) securedHtml = securedHtml.Insert(headEnd + 1, policy);
        }
        else
        {
            var htmlStart = securedHtml.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
            var htmlEnd = htmlStart < 0 ? -1 : securedHtml.IndexOf('>', htmlStart);
            if (htmlEnd >= 0)
                securedHtml = securedHtml.Insert(htmlEnd + 1, $"<head>{policy}</head>");
            else
                securedHtml = $"<!doctype html><html><head>{policy}</head><body>{securedHtml}</body></html>";
        }

        var bridge = DashboardBridgeScript.Replace("__TOKEN__", bridgeToken, StringComparison.Ordinal);
        var scriptTag = $"<script nonce=\"{nonce}\">{bridge}</script>";
        var bodyEnd = securedHtml.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        var documentEnd = bodyEnd >= 0
            ? bodyEnd
            : securedHtml.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
        return documentEnd >= 0
            ? securedHtml.Insert(documentEnd, scriptTag)
            : securedHtml + scriptTag;
    }

    private static readonly string DashboardShell = ReadDashboardResource(
        "ConnectOnion.WinUIClient.Dashboard.DashboardShell.html");

    private static readonly string DashboardBridgeScript = ReadDashboardResource(
        "ConnectOnion.WinUIClient.Dashboard.DashboardBridge.js");

    private static string ReadDashboardResource(string resourceName)
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource '{resourceName}'.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private void DetachDashboardWebView()
    {
        _dashboardLoadGeneration++;
        if (ControlCenterWebView?.CoreWebView2 is not { } core) return;
        core.NavigationStarting -= OnDashboardNavigationStarting;
        core.NavigationCompleted -= OnDashboardNavigationCompleted;
        core.FrameCreated -= OnDashboardFrameCreated;
        core.WebMessageReceived -= OnDashboardWebMessageReceived;
        core.NewWindowRequested -= OnDashboardNewWindowRequested;
        core.DownloadStarting -= OnDashboardDownloadStarting;
        core.WebResourceRequested -= OnDashboardWebResourceRequested;
    }

    private void OnCollapseRightPaneClicked(object sender, RoutedEventArgs e)
    {
        _isRightWorkspaceExpanded = false;
        UpdateRightWorkspaceForCurrentPage();
    }

    private void OnExpandRightPaneClicked(object sender, RoutedEventArgs e)
    {
        _isRightWorkspaceExpanded = true;
        UpdateRightWorkspaceForCurrentPage();
    }

}
