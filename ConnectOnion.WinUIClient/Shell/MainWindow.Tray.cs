using System;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient;

public sealed partial class MainWindow
{
    private const int WmClose = 0x0010;
    private const int WmCancelMode = 0x001F;
    private const int WmSize = 0x0005;
    private const int WmNcMouseLeave = 0x02A2;
    private const int SizeRestored = 0;
    private const int SizeMinimized = 1;
    private const int SizeMaximized = 2;

    private const int SwHide = 0;
    private const int SwShowMaximized = 3;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint RedrawInvalidate = 0x0001;
    private const uint RedrawUpdateNow = 0x0100;
    private const uint RedrawFrame = 0x0400;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private static bool _isExiting;

    private SUBCLASSPROC? _subclassProc;
    private bool _isTrayDisposed;
    private bool _isHiddenToTray;
    private bool _closeBehaviorLoaded;
    private bool _closePromptOpen;
    private WindowCloseBehavior _closeBehavior = WindowCloseBehavior.Ask;

    private void InitializeTrayBehavior()
    {
        // H.NotifyIcon owns the notification-area icon, callback window, icon handle,
        // context menu, and shell-restart recovery. This remaining subclass only observes
        // the real app window for close-to-tray and notification-presence semantics.
        _subclassProc = WindowSubclassProc;
        if (!SetWindowSubclass(_hwnd, _subclassProc, UIntPtr.Zero, UIntPtr.Zero))
        {
            Serilog.Log.Warning("Could not attach close-to-tray window message observer");
        }

        try
        {
            // Keep the process fully active while background agent turns are running.
            // H.NotifyIcon defaults this call to Windows 11 Efficiency Mode, which is not
            // appropriate for an app maintaining live WebSocket work.
            TrayIcon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch (Exception ex)
        {
            // The XAML control will retry creation when it is loaded. A tray failure must
            // not prevent the primary window from opening.
            Serilog.Log.Warning(ex, "Could not create the system tray icon immediately");
        }

        _ = LoadCloseBehaviorAsync();
    }

    private IntPtr WindowSubclassProc(
        IntPtr hwnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        try
        {
            // Track taskbar minimize/restore. WinUI's Window.Activated does not
            // reliably fire on minimize, so without this the notification layer
            // still thinks this window is actively showing the conversation and
            // suppresses (or in-app-routes) replies that arrive while minimized.
            // WM_SIZE always fires, so key visibility off it.
            if (message == WmSize)
            {
                var sizeType = unchecked((int)wParam.ToUInt64()) & 0xffff;
                if (sizeType == SizeMinimized)
                    _windowPresence?.SetVisible(this, false);
                else if (sizeType is SizeRestored or SizeMaximized)
                    _windowPresence?.SetVisible(this, true);
                return DefSubclassProc(hwnd, message, wParam, lParam);
            }

            if (message == WmClose && !_isExiting)
            {
                // Caption Close and Alt+F4 are both WM_CLOSE. Consume the native close while the
                // UI thread applies the user's explicit choice (or asks on first use).
                RequestWindowClose();
                return IntPtr.Zero;
            }

            return DefSubclassProc(hwnd, message, wParam, lParam);
        }
        catch
        {
            return DefSubclassProc(hwnd, message, wParam, lParam);
        }
    }

    private void RequestWindowClose()
    {
        if (_isExiting || _closePromptOpen)
            return;

        _ = DispatcherQueue.TryEnqueue(async () => await HandleWindowCloseAsync());
    }

    private async Task HandleWindowCloseAsync()
    {
        if (_isExiting || _closePromptOpen)
            return;

        _closePromptOpen = true;
        try
        {
            await LoadCloseBehaviorAsync();
            switch (_closeBehavior)
            {
                case WindowCloseBehavior.HideToTray:
                    MinimizeToTray();
                    return;
                case WindowCloseBehavior.Exit:
                    ExitApplication();
                    return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = LocalizedStrings.Get("CloseBehaviorTitle", "Close ConnectOnion?"),
                Content = LocalizedStrings.Get(
                    "CloseBehaviorDescription",
                    "Keep agent turns running in the background, or exit completely. We’ll remember your choice."),
                PrimaryButtonText = LocalizedStrings.Get("CloseBehaviorKeepRunning", "Keep running"),
                SecondaryButtonText = LocalizedStrings.Get("CommonExit", "Exit"),
                CloseButtonText = LocalizedStrings.Get("CommonCancel", "Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            };

            var result = await dialog.ShowThemedAsync();
            if (result == ContentDialogResult.None)
                return;

            _closeBehavior = result == ContentDialogResult.Primary
                ? WindowCloseBehavior.HideToTray
                : WindowCloseBehavior.Exit;
            await SaveCloseBehaviorAsync(_closeBehavior);

            if (_closeBehavior == WindowCloseBehavior.HideToTray)
                MinimizeToTray();
            else
                ExitApplication();
        }
        catch (Exception ex)
        {
            // Failure to show or persist the choice must never silently turn into either
            // hiding or exiting. Leave the window open so the user remains in control.
            Serilog.Log.Warning(ex, "Could not resolve the window close behavior");
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    /// <summary>
    /// Drops the cached close behavior so the next close re-reads it. Settings writes the
    /// preference straight to the database; without this the live window would keep answering
    /// with whatever it read the first time the user closed it, and the new setting would only
    /// take effect after a restart.
    /// </summary>
    internal static void InvalidateCloseBehaviorCache()
    {
        if (App.MainWindow is MainWindow window)
            window._closeBehaviorLoaded = false;
    }

    private async Task LoadCloseBehaviorAsync()
    {
        if (_closeBehaviorLoaded)
            return;

        try
        {
            _closeBehavior = (await _preferencesRepository.LoadAsync()).CloseBehavior;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not load the window close behavior; asking the user");
            _closeBehavior = WindowCloseBehavior.Ask;
        }
        finally
        {
            _closeBehaviorLoaded = true;
        }
    }

    private async Task SaveCloseBehaviorAsync(WindowCloseBehavior behavior)
    {
        try
        {
            var preferences = await _preferencesRepository.LoadAsync();
            preferences.CloseBehavior = behavior;
            await _preferencesRepository.SaveAsync(preferences);
        }
        catch (Exception ex)
        {
            // Apply the choice for this process even if persistence is temporarily unavailable;
            // the next launch will ask again rather than guessing.
            Serilog.Log.Warning(ex, "Could not persist the window close behavior");
        }
    }

    private void MinimizeToTray()
    {
        if (_isExiting || _isTrayDisposed) return;

        // Logged because "hidden to tray" and "exited" are indistinguishable in a log otherwise —
        // the process simply stops writing. A user reporting "the app closed itself" is answered
        // by whether this line or the exit sequence came last.
        Serilog.Log.Information("Window hidden to tray; app still running");
        ClearCaptionButtonInteraction();
        ShowWindow(_hwnd, SwHide);
        _isHiddenToTray = true;
        _windowPresence?.SetVisible(this, false);
    }

    private void RestoreFromTray()
    {
        if (_isExiting || _isTrayDisposed) return;

        BringToForeground();
    }

    /// <summary>Restores the window from a minimized or hidden (tray) state and brings it
    /// to the foreground. Used when a second app launch is redirected to this — the only —
    /// instance: it must wake the existing window, never create a new one. Safe against
    /// teardown (a redirect arriving while the app is closing) and a temporarily
    /// unavailable window handle.</summary>
    public void BringToForeground()
    {
        if (_isExiting || _hwnd == IntPtr.Zero) return;

        if (_isHiddenToTray)
            ResetCaptionButtonChrome();

        // SW_RESTORE is not a neutral "make visible" operation: when the window is already
        // maximized it changes it back to its restored size. Notification navigation reaches
        // this method even while the window is visible, which is why clicking a toast used to
        // cancel maximization. Only minimized windows need a restore command. If the window was
        // maximized before minimizing, show it maximized directly so there is no restored-state
        // flash (and no transient placement event that records the wrong state). SW_SHOW alone
        // is enough for visible and tray-hidden windows and preserves their presenter state.
        var presenter = _appWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        if (presenter?.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
        {
            ShowWindow(
                _hwnd,
                _lastNonMinimizedWasMaximized ? SwShowMaximized : SwRestore);
        }
        else
        {
            ShowWindow(_hwnd, SwShow);
        }
        SetForegroundWindow(_hwnd);
        try { Activate(); }
        catch { /* Activate can throw during teardown; best-effort. */ }

        // SetForegroundWindow/Activate complete through queued native messages. Refresh once now
        // and once after those messages have settled; otherwise DWM can restore the Close
        // button's pre-hide hover/pressed visual after our first reset.
        RefreshCaptionButtons();
        _ = DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            RefreshCaptionButtons);
        _isHiddenToTray = false;
        if (!_isTrayDisposed) _windowPresence?.SetVisible(this, true);
    }

    private void ResetCaptionButtonChrome()
    {
        try
        {
            // Tear down and recreate WinUI's custom-title-bar integration while the HWND is
            // hidden. Presenter/style toggles retain the same AppWindowTitleBar caption visuals,
            // including their stale Close hover, whereas unregistering the XAML title bar
            // rebuilds that state before the frame becomes visible again.
            SetTitleBar(null);
            ExtendsContentIntoTitleBar = false;
            _appWindow.TitleBar.ResetToDefault();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarDragRegion);
            ApplyTitleBarTheme(RootGrid.ActualTheme);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not rebuild caption button chrome before tray restore");
        }
    }

    private void ClearCaptionButtonInteraction()
    {
        // WM_CLOSE is consumed to keep the process alive, so the normal close sequence never
        // gets a chance to clear the DWM caption button's pressed/hover state.
        ReleaseCapture();
        SendMessage(_hwnd, WmCancelMode, UIntPtr.Zero, IntPtr.Zero);
        SendMessage(_hwnd, WmNcMouseLeave, UIntPtr.Zero, IntPtr.Zero);
    }

    private void RefreshCaptionButtons()
    {
        if (_isExiting || _isTrayDisposed || _hwnd == IntPtr.Zero) return;

        ClearCaptionButtonInteraction();
        SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        RedrawWindow(
            _hwnd,
            IntPtr.Zero,
            IntPtr.Zero,
            RedrawInvalidate | RedrawFrame | RedrawUpdateNow);
    }

    private async void ExitApplication()
    {
        // Every exit surface funnels here. A double click or a tray command racing WM_CLOSE must
        // not run two Application.Exit calls while WinUI is dismantling the same native tree.
        if (System.Threading.Interlocked.Exchange(ref _exitStarted, 1) != 0) return;

        // The single funnel for a real exit (tray menu Exit, File > Exit, sidebar user menu Exit).
        // Logged at entry so the teardown lines that follow — window detach, then the phase
        // timings from App.ShutdownAsync — can be read as one sequence, and so a log that simply
        // stops without this line is recognisable as a crash rather than a clean exit.
        Serilog.Log.Information("Exit requested; tearing down window");
        _isExiting = true;
        // Single window: this instance owns the only tray behavior to dispose.
        DisposeTrayBehavior();
        // Application.Exit closes the HWND after the host has stopped. Remove callbacks first so
        // late Activated/Closed events never resolve services from a disposed IServiceProvider.
        DetachWindowServices();

        // Persist while the host and its services are still alive. Application.Exit raises
        // Closed only after ShutdownAsync has disposed the provider; SaveWindowPlacementAsync is
        // idempotent, so that later Closed callback becomes a harmless no-op.
        await SaveWindowPlacementAsync();

        // Real exit (as opposed to minimize-to-tray): cancel in-flight turns, let them
        // persist recoverable state, and release the sockets — all bounded so exit never
        // hangs. ShutdownAsync self-limits; the extra cap is a hard backstop.
        await ((App)Application.Current).ShutdownAsync();

        Application.Current.Exit();
    }

    private void DisposeTrayBehavior()
    {
        if (_isTrayDisposed) return;

        _isTrayDisposed = true;
        TrayIcon.Dispose();

        if (_subclassProc is not null)
        {
            RemoveWindowSubclass(_hwnd, _subclassProc, UIntPtr.Zero);
            _subclassProc = null;
        }
    }
}
