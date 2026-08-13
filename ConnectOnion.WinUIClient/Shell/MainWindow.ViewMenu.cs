using System;
using System.Diagnostics;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace ConnectOnion.WinUIClient;

/// <summary>
/// The View menu: zoom, full screen, sidebar visibility, navigation, and the window-level find
/// overlay.
///
/// Two things here belong to the window rather than to any page, and that is why they live in
/// this partial. Zoom is applied as a render transform over the whole content frame, so it is
/// not a page's business; and there is exactly one find UI, which attaches to whatever page is
/// currently loaded through <see cref="Views.IFindHost"/> — a page implements that interface
/// rather than building a find bar of its own.
/// </summary>
public sealed partial class MainWindow
{
    // Zoom scales the whole content area, and because UpdateZoomLayout divides the logical size
    // by the factor before scaling up, a larger zoom reflows rather than clips. The old ceiling
    // of 1.4 was well short of what someone actually enlarging the UI for readability needs;
    // 2.0 matches what a browser offers, and 0.67 is the matching step down.
    private const double ZoomStep = 0.1;
    private const double MinZoom = 0.67;
    private const double MaxZoom = 2.0;
    private const double DefaultZoom = 1.0;
    private const double ZoomPopupMarginFromCaption = 8;
    private const double ZoomPopupTopOffset = 18;
    private const double FindOverlayWidth = 380;
    private const double FindOverlayMinWidth = 280;
    private const double FindOverlayMarginFromCaption = 8;
    private const double FindOverlayTopOffset = 18;
    /// <summary>The standard Windows caption height, and the height of the title-bar row at
    /// 100% OS text scale. Mirrors the literal in MainWindow.xaml's RowDefinition.</summary>
    private const double TitleBarBaseHeight = 32;
    private static readonly TimeSpan ZoomPopupIdleTimeout = TimeSpan.FromSeconds(3);

    private double _zoomFactor = 1.0;
    /// <summary>Whether the title bar's caption inset and scaled column width are current.
    /// Cleared by a text-scale or DPI change — never by a resize, which cannot affect them.</summary>
    private bool _titleBarMetricsValid;
    private bool _isFullScreen;
    /// <summary>Guards against the feedback loop when the window writes the query back into the
    /// find box (restoring a remembered search): the resulting TextChanged would otherwise
    /// re-drive the page's search as though the user had typed it.</summary>
    private bool _isUpdatingFindText;
    /// <summary>The page currently wired to the shared find UI, or null when the loaded page
    /// does not support in-content search. Re-resolved on navigation, which is what lets one
    /// overlay serve every page.</summary>
    private Views.IFindHost? _activeFindHost;
    private readonly DispatcherTimer _zoomPopupIdleTimer = new()
    {
        Interval = ZoomPopupIdleTimeout,
    };

    private void RegisterViewMenuAccelerators()
    {
        RootGrid.KeyDown += ViewMenuShortcut_KeyDown;
        _zoomPopupIdleTimer.Tick += ZoomPopupIdleTimer_Tick;
    }

    /// <summary>Called when the window closes: a pending idle tick would hide the zoom popup —
    /// i.e. touch the visual tree — after teardown has started. Stop it before that can happen.</summary>
    private void DetachViewMenu()
    {
        _zoomPopupIdleTimer.Stop();
        _zoomPopupIdleTimer.Tick -= ZoomPopupIdleTimer_Tick;
        // TextScaleService is a singleton that outlives this window, and its Changed handler
        // re-lays out the visual tree. Left attached, an OS text-size change during teardown
        // would run UpdateZoomLayout against a tree the framework is already disposing.
        _textScale.Changed -= OnTextScaleChanged;
    }

    private void ToggleSidebarMenu_Click(object sender, RoutedEventArgs e)
        => ToggleSidebarCommand();

    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
        => OpenTerminalCommand();

    private void Find_Click(object sender, RoutedEventArgs e)
        => FindCommand();

    private void BackMenu_Click(object sender, RoutedEventArgs e)
        => BackCommand();

    private void ForwardMenu_Click(object sender, RoutedEventArgs e)
        => ForwardCommand();

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
        => ZoomInCommand();

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
        => ZoomOutCommand();

    private void ActualSize_Click(object sender, RoutedEventArgs e)
        => ShowActualSizeCommand();

    private void ActualSizeZoomIn_Click(object sender, RoutedEventArgs e)
    {
        ZoomInCommand();
        RestartZoomPopupIdleTimer();
    }

    private void ActualSizeZoomOut_Click(object sender, RoutedEventArgs e)
    {
        ZoomOutCommand();
        RestartZoomPopupIdleTimer();
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        ResetZoomCommand();
        RestartZoomPopupIdleTimer();
    }

    private void ToggleFullScreen_Click(object sender, RoutedEventArgs e)
        => ToggleFullScreenCommand();

    /// <summary>Dispatches the View menu's keyboard shortcuts.
    /// <para>Note it never compares key codes: <c>MatchShortcut</c> asks
    /// <c>KeyboardShortcutService</c>, which resolves the catalog default against the user's
    /// override — so a rebound shortcut works here with no change to this file. Falling through
    /// (rather than marking Handled) on an unrecognized id is what lets the File/Help partials
    /// see the same keystroke.</para></summary>
    private void ViewMenuShortcut_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        // A modal overlay owns the keyboard while it is up; firing menu commands underneath it
        // would act on a window the user cannot currently see or reach.
        if (IsModalOverlayOpen) return;

        var id = MatchShortcut(e);
        if (id is null) return;

        // Full screen and Find are the two that stay live while the caret is in a text box;
        // the rest would fight the editor for keys it legitimately owns, so they stand down.
        // (This split predates rebinding and is about focus, not about which keys are bound.)
        if (id is not (KeyboardShortcutCatalog.Ids.ToggleFullScreen or KeyboardShortcutCatalog.Ids.Find)
            && IsEditableInputFocused())
        {
            return;
        }

        switch (id)
        {
            case KeyboardShortcutCatalog.Ids.ToggleFullScreen:
                ToggleFullScreenCommand();
                break;
            case KeyboardShortcutCatalog.Ids.Find:
                FindCommand();
                break;
            case KeyboardShortcutCatalog.Ids.ToggleSidebar:
                ToggleSidebarCommand();
                break;
            case KeyboardShortcutCatalog.Ids.OpenTerminal:
                OpenTerminalCommand();
                break;
            case KeyboardShortcutCatalog.Ids.GoBack:
                BackCommand();
                break;
            case KeyboardShortcutCatalog.Ids.GoForward:
                ForwardCommand();
                break;
            case KeyboardShortcutCatalog.Ids.ZoomIn:
                ZoomInCommand();
                break;
            case KeyboardShortcutCatalog.Ids.ZoomOut:
                ZoomOutCommand();
                break;
            default:
                return;   // a shortcut another handler owns
        }

        e.Handled = true;
    }

    /// <summary>Opens a terminal at the app data directory. Prefers Windows Terminal and
    /// falls back to PowerShell, which is always present — <c>wt.exe</c> is not installed on
    /// every Windows build. Both go through the shell (<c>UseShellExecute</c>) so PATH
    /// resolution and the app-execution aliases work.</summary>
    private void OpenTerminalCommand()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "wt.exe",
                WorkingDirectory = AppStorage.RootDir,
                UseShellExecute = true,
            });
        }
        catch
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = AppStorage.RootDir,
                UseShellExecute = true,
            });
        }
    }

    /// <summary>Ctrl+F. The command is disabled outside pages that implement
    /// <see cref="Views.IFindHost"/>; keep the guard for shortcut races during navigation.</summary>
    private void FindCommand()
    {
        if (ContentFrame.Content is not Views.IFindHost findHost) return;

        AttachFindHost(findHost);
        UpdateFindOverlayPosition();
        FindOverlayPanel.Visibility = Visibility.Visible;
        findHost.OpenFind();
        // Re-run the previous query: reopening find should show the search you had, not a blank
        // bar, and the SelectAll below means typing still replaces it in one keystroke.
        findHost.SetFindQuery(FindTextBox.Text);
        UpdateFindOverlayState();

        // Focus is deferred: the overlay was made visible microseconds ago and cannot take
        // focus until the framework has laid it out.
        DispatcherQueue.TryEnqueue(() =>
        {
            FindTextBox.Focus(FocusState.Programmatic);
            FindTextBox.SelectAll();
        });
    }

    private void ZoomInCommand()
        => SetZoom(Math.Min(MaxZoom, _zoomFactor + ZoomStep));

    private void ZoomOutCommand()
        => SetZoom(Math.Max(MinZoom, _zoomFactor - ZoomStep));

    private void ResetZoomCommand()
        => SetZoom(DefaultZoom);

    private void ShowActualSizeCommand()
    {
        UpdateZoomPopupPosition();
        UpdateZoomPercentText();
        ZoomPopupPanel.Visibility = Visibility.Visible;
        RestartZoomPopupIdleTimer();
    }

    /// <summary>Applies zoom while preserving the visible viewport. The logical content size is
    /// divided by the scale before the transform is applied, so a larger zoom reflows the page
    /// instead of clipping its right and bottom edges.</summary>
    private void SetZoom(double zoomFactor, bool persist = true)
    {
        // Rounded to 2dp so repeated ±0.1 steps land back exactly on 1.0 rather than
        // accumulating float error and leaving "Reset" permanently enabled at 100%.
        var clamped = Math.Round(Math.Clamp(zoomFactor, MinZoom, MaxZoom), 2);
        var changed = Math.Abs(clamped - _zoomFactor) > 0.001;
        _zoomFactor = clamped;
        UpdateZoomPercentText();
        UpdateZoomLayout();
        ApplySidebarWidth(EffectiveContentWidth);

        // Every other view preference (theme, language, message size) survives a restart; zoom
        // silently did not, so a user who enlarged the UI for readability had to redo it on every
        // launch. Fire-and-forget: a failed write must not block the visual change.
        if (persist && changed) _ = SaveZoomAsync(clamped);
    }

    private async System.Threading.Tasks.Task SaveZoomAsync(double zoomFactor)
    {
        try
        {
            var preferences = await _preferencesRepository.LoadAsync();
            preferences.ZoomFactor = zoomFactor;
            await _preferencesRepository.SaveAsync(preferences);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not persist the zoom level");
        }
    }

    /// <summary>Restores the saved zoom at startup. Applied with <c>persist: false</c> so
    /// restoring the stored value does not immediately write it back.</summary>
    internal System.Threading.Tasks.Task RestoreZoomAsync()
    {
        try
        {
            var preferences = AppServices.StartupState.Preferences;
            _textScale.ApplyInterfaceTextSize(preferences.InterfaceTextSize);
            var saved = preferences.ZoomFactor;
            if (Math.Abs(saved - DefaultZoom) > 0.001)
                SetZoom(saved, persist: false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not restore the saved zoom level");
        }
        return System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>The scale the content area actually renders at: window zoom multiplied by the
    /// OS accessibility scale and the app-specific interface text-size preset.
    ///
    /// <para>Each input remains independent: Ctrl +/- controls window zoom, Settings controls
    /// the interface text preset, and Windows controls its accessibility scale. Multiplication
    /// preserves all three choices instead of silently overriding one of them.</para></summary>
    private double EffectiveContentScale => _zoomFactor * _textScale.Effective;

    private void UpdateZoomLayout()
    {
        if (RootGrid.ActualWidth <= 0 || RootGrid.ActualHeight <= 0)
            return;

        // The title bar scales with the OS and app text settings, never with Ctrl+/- window zoom.
        // That split is deliberate and matches every browser: Ctrl+ enlarges the page, not the
        // window chrome. It is also forced — the caption buttons beside this strip are drawn by
        // Windows and follow the system setting, so zooming the app's half alone would leave the
        // two halves of one title bar at different sizes.
        //
        // Guarded because this method runs on every SizeChanged — i.e. every frame of a window
        // drag — while nothing inside UpdateTitleBarScale depends on the window's *size*. It
        // reads the caption inset and the display DPI, which change only with the text scale or
        // a DPI switch, and both of those invalidate the flag explicitly.
        if (!_titleBarMetricsValid) UpdateTitleBarScale();

        var contentHeight = Math.Max(0, RootGrid.ActualHeight - TitleBarArea.ActualHeight);

        // The content area starts below the title bar; the overlay layer and the toast host both
        // span the full window (they are declared Grid.Row="0" Grid.RowSpan="2"), so they get the
        // window's own height rather than the content height.
        //
        // Scaling all three matters: zoom used to apply to MainContentArea alone, which left the
        // settings modal, the About and shortcuts dialogs, the find bar and every toast rendering
        // at 100% while the chat behind them was at 140%. They are siblings of MainContentArea,
        // not children, so nothing carried the transform to them.
        ApplyZoomTo(MainContentArea, RootGrid.ActualWidth, contentHeight);
        ApplyZoomTo(FloatingOverlayLayer, RootGrid.ActualWidth, RootGrid.ActualHeight);
        ApplyZoomTo(InAppNotifications, RootGrid.ActualWidth, RootGrid.ActualHeight);
    }

    /// <summary>Grows the app-drawn half of the title bar with the effective text-size setting,
    /// and asks Windows for a taller caption to match so the menu bar is not clipped.
    ///
    /// <para>The scale is a <see cref="ScaleTransform"/>, which does not change the element's
    /// layout slot — WinUI has no WPF-style <c>LayoutTransform</c>. Left alone, the enlarged
    /// content would paint over <c>TitleBarDragRegion</c> and quietly take away the window's
    /// drag area, so the content column is widened by hand to match what the transform actually
    /// draws.</para></summary>
    private void UpdateTitleBarScale()
    {
        var scale = _textScale.Effective;
        // Set before the work, not after: DesiredSize may still be 0 on the first pass (see
        // below), and that case has to be retried on the next layout rather than latched.
        _titleBarMetricsValid = true;

        // Standard is 32 epx; Tall is the ~48 epx caption Windows offers, which is what keeps
        // the system's own buttons in step with an enlarged strip instead of leaving a 32 epx
        // hit target next to 48 epx of app content. Requested before the row height is read
        // back, since it is what changes that height.
        try
        {
            _appWindow.TitleBar.PreferredHeightOption = scale > 1.2
                ? TitleBarHeightOption.Tall
                : TitleBarHeightOption.Standard;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Caption height could not be adjusted for the text scale");
        }

        if (Math.Abs(scale - 1.0) < 0.001)
        {
            TitleBarContent.ClearValue(UIElement.RenderTransformProperty);
            TitleBarContentColumn.Width = GridLength.Auto;
            TitleBarRow.Height = new GridLength(TitleBarBaseHeight);
        }
        else
        {
            TitleBarContent.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
            TitleBarContent.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };

            // DesiredSize is the *untransformed* measurement, which is exactly what has to be
            // multiplied. It is zero before the first measure pass; leaving the column on Auto
            // until then is correct rather than pinning it to nothing — and the metrics stay
            // invalid so the next layout pass computes the real width.
            var natural = TitleBarContent.DesiredSize.Width;
            if (natural > 0)
            {
                TitleBarContentColumn.Width = new GridLength(natural * scale);
            }
            else
            {
                TitleBarContentColumn.Width = GridLength.Auto;
                _titleBarMetricsValid = false;
            }

            // The row has to clear both the scaled content and whatever caption height Windows
            // settled on, or the two halves of one title bar disagree.
            var captionHeight = SystemCaptionHeight();
            TitleBarRow.Height = new GridLength(
                Math.Max(TitleBarBaseHeight * scale, captionHeight));
        }

        UpdateCaptionButtonsColumn();
    }

    /// <summary>The caption height Windows is currently drawing, in epx, or 0 if unavailable.</summary>
    private double SystemCaptionHeight()
    {
        try
        {
            var height = _appWindow.TitleBar.Height;
            return height > 0 ? height / GetDisplayScale() : 0;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Caption height could not be measured");
            return 0;
        }
    }

    /// <summary>Sizes the spacer that keeps the app's title-bar content clear of the system
    /// caption buttons.
    ///
    /// <para>This used to be a literal <c>144</c> in the XAML — the width of three buttons at
    /// 100%. The real width is none of this app's business: it changes with DPI, with the
    /// caption height chosen above, and with whether the shell is showing the snap-layout
    /// affordance. <see cref="AppWindowTitleBar"/> reports it exactly, in device pixels, as the
    /// inset on whichever side the buttons are on — which is <see cref="AppWindowTitleBar.LeftInset"/>
    /// under a right-to-left layout.</para></summary>
    private void UpdateCaptionButtonsColumn()
    {
        try
        {
            var titleBar = _appWindow.TitleBar;
            var inset = RootGrid.FlowDirection == FlowDirection.RightToLeft
                ? titleBar.LeftInset
                : titleBar.RightInset;
            if (inset <= 0) return;   // Not yet measured; the XAML fallback still applies.

            CaptionButtonsColumn.Width = new GridLength(inset / GetDisplayScale());
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Caption button inset could not be measured");
        }
    }

    /// <summary>Re-lays out the window after either text-size input changes. OS notifications are
    /// already marshalled to the UI thread by <see cref="Services.TextScaleService"/>.</summary>
    private void OnTextScaleChanged(object? sender, EventArgs e)
    {
        _titleBarMetricsValid = false;
        UpdateZoomLayout();
        ApplySidebarWidth(EffectiveContentWidth);
        UpdateZoomPopupPosition();
        UpdateFindOverlayPosition();
    }

    /// <summary>
    /// Scales one top-level layer, sizing its logical box to <paramref name="logicalWidth"/> /
    /// <paramref name="logicalHeight"/> divided by the factor so the scaled result fills exactly
    /// the same physical area. At 1.0 the explicit size and transform are cleared rather than set
    /// to identity, so normal Stretch layout resumes.
    /// </summary>
    private void ApplyZoomTo(FrameworkElement layer, double logicalWidth, double logicalHeight)
    {
        var scale = EffectiveContentScale;

        // The identity case first, and it is the common one: no zoom, no OS text scaling, every
        // frame of every window drag. This used to allocate a ScaleTransform and a Point,
        // assign them, and then immediately ClearValue the transform away again — three layers
        // × two allocations, per resize frame, to end up exactly where it started.
        if (Math.Abs(scale - DefaultZoom) < 0.001)
        {
            layer.ClearValue(FrameworkElement.WidthProperty);
            layer.ClearValue(FrameworkElement.HeightProperty);
            layer.HorizontalAlignment = HorizontalAlignment.Stretch;
            layer.VerticalAlignment = VerticalAlignment.Stretch;
            layer.ClearValue(UIElement.RenderTransformProperty);
            return;
        }

        // Reuse the existing transform when there is one: a resize at a non-default zoom changes
        // the layer's size, not its scale, so the transform object itself rarely needs replacing.
        if (layer.RenderTransform is ScaleTransform existing)
        {
            if (Math.Abs(existing.ScaleX - scale) > 0.0001) existing.ScaleX = scale;
            if (Math.Abs(existing.ScaleY - scale) > 0.0001) existing.ScaleY = scale;
        }
        else
        {
            layer.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
            layer.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
        }

        layer.HorizontalAlignment = HorizontalAlignment.Left;
        layer.VerticalAlignment = VerticalAlignment.Top;
        layer.Width = logicalWidth / scale;
        layer.Height = logicalHeight / scale;
    }

    private void UpdateZoomPercentText()
    {
        ZoomPercentText.Text = $"{_zoomFactor:P0}";
        ResetZoomButton.IsEnabled = Math.Abs(_zoomFactor - DefaultZoom) > 0.001;
    }

    /// <summary>Positions the zoom popup clear of the window's caption buttons. This app draws
    /// its own title bar, so the popup shares that strip and would otherwise sit under
    /// minimize/maximize/close and swallow those clicks.</summary>
    private void UpdateZoomPopupPosition()
    {
        // 144 is the standard three-button caption width, used only before the column has
        // measured (first show); after that the real width wins.
        var captionWidth = CaptionButtonsColumn.ActualWidth > 0
            ? CaptionButtonsColumn.ActualWidth
            : 144;

        ZoomPopupPanel.Margin = new Thickness(
            0,
            ZoomPopupTopOffset,
            captionWidth + ZoomPopupMarginFromCaption,
            0);
    }

    private void UpdateFindOverlayPosition()
    {
        var captionWidth = CaptionButtonsColumn.ActualWidth > 0
            ? CaptionButtonsColumn.ActualWidth
            : 144;
        // Shrinks toward the minimum on a narrow window rather than being clipped by the caption
        // buttons; below FindOverlayMinWidth it stops shrinking, since a find box narrower than
        // that can't show its counter and buttons anyway.
        var availableWidth = Math.Max(FindOverlayMinWidth, RootGrid.ActualWidth - captionWidth - 16);

        FindOverlayPanel.Width = Math.Min(FindOverlayWidth, availableWidth);
        FindOverlayPanel.Margin = new Thickness(
            0,
            FindOverlayTopOffset,
            captionWidth + FindOverlayMarginFromCaption,
            0);
    }

    /// <summary>Points the shared find UI at a page, detaching from the previous one. Without
    /// the swap, a navigated-away page would keep pushing match counts into an overlay that is
    /// now searching something else — and the window would hold it alive through the handler.</summary>
    private void AttachFindHost(Views.IFindHost findHost)
    {
        if (ReferenceEquals(_activeFindHost, findHost)) return;

        if (_activeFindHost is not null)
            _activeFindHost.FindStateChanged -= FindHost_FindStateChanged;

        _activeFindHost = findHost;
        _activeFindHost.FindStateChanged += FindHost_FindStateChanged;
    }

    // Marshalled: the page may raise this from a debounce timer or a background continuation,
    // and everything UpdateFindOverlayState touches is UI.
    private void FindHost_FindStateChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateFindOverlayState);

    private void UpdateFindOverlayState()
    {
        var host = _activeFindHost;
        var hasMatches = host?.HasFindMatches == true;

        FindCounterText.Text = host?.FindStatusText
            ?? LocalizedStrings.Get("FindNoResults", "0 / 0 results");
        PreviousFindButton.IsEnabled = hasMatches;
        NextFindButton.IsEnabled = hasMatches;
    }

    private void CloseFindOverlay()
    {
        FindOverlayPanel.Visibility = Visibility.Collapsed;
        _activeFindHost?.CloseFind();
        // Guarded: this assignment raises TextChanged, which would otherwise push an empty
        // query back into the page we just told to close.
        _isUpdatingFindText = true;
        FindTextBox.Text = "";
        _isUpdatingFindText = false;
        UpdateFindOverlayState();
    }

    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingFindText) return;
        _activeFindHost?.SetFindQuery(FindTextBox.Text);
    }

    /// <summary>Enter / Shift+Enter step through matches, Esc closes — the conventional find-bar
    /// keys, handled here rather than through the shortcut catalog because they only apply while
    /// the find box has focus and would otherwise collide with the composer's own Enter.</summary>
    private void FindTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            // Shift read live from the keyboard source: KeyRoutedEventArgs carries no
            // modifier state, so there is nothing on `e` to test.
            if ((Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
                _activeFindHost?.SelectPreviousFindMatch();
            else
                _activeFindHost?.SelectNextFindMatch();

            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            CloseFindOverlay();
            e.Handled = true;
        }
    }

    private void PreviousFind_Click(object sender, RoutedEventArgs e)
        => _activeFindHost?.SelectPreviousFindMatch();

    private void NextFind_Click(object sender, RoutedEventArgs e)
        => _activeFindHost?.SelectNextFindMatch();

    private void CloseFind_Click(object sender, RoutedEventArgs e)
        => CloseFindOverlay();

    /// <summary>Restarts the popup's auto-hide countdown. Called after every zoom interaction so
    /// the popup stays up while the user is adjusting and dismisses itself once they stop.
    /// No-ops when the popup is hidden — arming a timer whose only job is to hide something
    /// already hidden is how a stray tick ends up running against a torn-down tree.</summary>
    private void RestartZoomPopupIdleTimer()
    {
        if (ZoomPopupPanel.Visibility != Visibility.Visible) return;

        _zoomPopupIdleTimer.Stop();
        _zoomPopupIdleTimer.Start();
    }

    private void ZoomPopupIdleTimer_Tick(object? sender, object e)
    {
        _zoomPopupIdleTimer.Stop();
        ZoomPopupPanel.Visibility = Visibility.Collapsed;
    }

    private void ToggleFullScreenCommand()
    {
        _isFullScreen = !_isFullScreen;
        _appWindow.SetPresenter(_isFullScreen
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Default);
    }

    /// <summary>Whether the caret is in something the user types into. Used to stand shortcuts
    /// down so they don't steal keys an editor legitimately owns.</summary>
    private bool IsEditableInputFocused()
    {
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;

        // Walks up the tree rather than testing the focused element alone: focus often lands on
        // a part *inside* a text control's template, which is not itself a TextBox.
        while (focused is not null)
        {
            if (focused is TextBox or RichEditBox or PasswordBox or AutoSuggestBox or NumberBox)
                return true;

            focused = VisualTreeHelper.GetParent(focused);
        }

        return false;
    }
}
