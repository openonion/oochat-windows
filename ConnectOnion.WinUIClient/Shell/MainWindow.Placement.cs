using System;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace ConnectOnion.WinUIClient;

/// <summary><see cref="MainWindow"/> position restore and normal-state tracking.</summary>
public sealed partial class MainWindow
{
    private WindowPosition? _lastNormalPosition;
    private PixelSize? _lastNormalSize;
    private bool _lastNonMinimizedWasMaximized;
    private double? _cachedDisplayScale;
    private XamlRoot? _trackedXamlRoot;

    private void InitializeWindowPlacement()
    {
        ApplyFlowDirection();
        ApplyMinimumWindowSize();

        if (_windowPlacementStore.Current is { } saved)
        {
            // Resize before Move: ClampToVisibleWorkArea measures against the window's size, so
            // clamping the old size and then growing the window can push it back off-screen.
            if (saved.Size is { } size)
                _appWindow.Resize(new SizeInt32(size.Width, size.Height));

            var restored = ClampToVisibleWorkArea(saved.Position);
            _appWindow.Move(new PointInt32(restored.X, restored.Y));
            _lastNormalPosition = restored;
            _lastNonMinimizedWasMaximized = saved.IsMaximized;
            if (saved.Size is { } restoredSize) _lastNormalSize = restoredSize;
            if (saved.IsMaximized && _appWindow.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();
        }
        else
        {
            _lastNormalPosition = new WindowPosition(_appWindow.Position.X, _appWindow.Position.Y);
        }

        _appWindow.Changed += OnAppWindowPlacementChanged;
    }

    /// <summary>Mirrors the shell for a right-to-left UI language.
    ///
    /// <para>Setting <see cref="FrameworkElement.FlowDirection"/> on the content root is most of
    /// the work — WinUI mirrors layout, <c>Margin</c>, <c>Padding</c>, <c>HorizontalAlignment</c>
    /// and the column order of every <c>Grid</c> beneath it, which is what moves the sidebar and
    /// the caption-button column to the correct sides. It does <b>not</b> mirror
    /// <c>CornerRadius</c> or <c>BorderThickness</c>, so the content panel's one rounded corner
    /// is swapped by hand.</para>
    ///
    /// <para>Both shipped languages are left-to-right, so this is inert today. It exists so the
    /// direction is read from the locale rather than assumed, and so the geometry that would
    /// have to change is marked and handled in one place instead of being found later one
    /// hardcoded corner at a time.</para></summary>
    private void ApplyFlowDirection()
    {
        try
        {
            if (!LayoutDirection.IsRightToLeft(AppServices.Language.Current)) return;

            RootGrid.FlowDirection = FlowDirection.RightToLeft;
            ContentPanelBorder.CornerRadius = new CornerRadius(0, 12, 0, 0);
            ContentPanelBorder.BorderThickness = new Thickness(0, 1, 1, 0);
        }
        catch (Exception ex)
        {
            // A left-to-right shell is a far better failure than no window.
            Serilog.Log.Warning(ex, "UI flow direction could not be applied");
        }
    }

    /// <summary>Stops the window being dragged below the size the shell can lay out.
    /// <para><see cref="OverlappedPresenter"/> expresses this in <b>device</b> pixels while the
    /// rest of the layout works in epx, so the floor is scaled by the window's current DPI —
    /// otherwise a 640 epx minimum is 640 physical pixels, which is only 400 epx at 160% and
    /// lets the user right back past it.</para></summary>
    private void ApplyMinimumWindowSize()
    {
        if (_appWindow.Presenter is not OverlappedPresenter presenter) return;

        var scale = GetDisplayScale();
        presenter.PreferredMinimumWidth = (int)Math.Ceiling(WindowPlacementPolicy.MinimumWidth * scale);
        presenter.PreferredMinimumHeight = (int)Math.Ceiling(WindowPlacementPolicy.MinimumHeight * scale);
    }

    /// <summary>Device pixels per epx for this window, cached.
    ///
    /// <para>Cached because the callers sit on layout paths that run per frame during a window
    /// drag, and a P/Invoke per frame to read a value that only changes when the window moves
    /// between monitors is pure waste. <see cref="InvalidateDisplayScale"/> drops it; the
    /// XamlRoot's <c>Changed</c> event is what raises that, since <c>RasterizationScale</c> is
    /// exactly the thing being cached.</para>
    ///
    /// <para>The read itself is a P/Invoke rather than <c>XamlRoot.RasterizationScale</c> because
    /// the first caller is <see cref="InitializeWindowPlacement"/>, which runs in the window's
    /// constructor — before there is a XamlRoot to ask.</para></summary>
    private double GetDisplayScale()
    {
        if (_cachedDisplayScale is { } cached) return cached;

        try
        {
            var dpi = NativeMethods.GetDpiForWindow(_hwnd);
            var scale = dpi > 0 ? dpi / 96.0 : 1.0;
            _cachedDisplayScale = scale;
            return scale;
        }
        catch (Exception ex)
        {
            // A missing DPI is not worth failing startup over; the unscaled floor still helps.
            // Not cached — a transient failure should not pin the window to 100% for its life.
            Serilog.Log.Warning(ex, "Window DPI could not be read; using an unscaled minimum size");
            return 1.0;
        }
    }

    /// <summary>Drops the cached DPI and the title bar's derived metrics after a display change.
    /// Both the minimum window size and the caption-inset column are computed from it.</summary>
    private void InvalidateDisplayScale()
    {
        _cachedDisplayScale = null;
        _titleBarMetricsValid = false;
        ApplyMinimumWindowSize();
    }

    /// <summary>Watches for the window moving to a monitor with a different scale. There is no
    /// DPI-changed event on <c>AppWindow</c>; <c>XamlRoot.Changed</c> is what reports a
    /// <c>RasterizationScale</c> change.</summary>
    private void TrackDisplayScaleChanges()
    {
        if (RootGrid.XamlRoot is not { } xamlRoot) return;
        _trackedXamlRoot = xamlRoot;
        xamlRoot.Changed += XamlRoot_Changed;
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        var previous = _cachedDisplayScale;
        _cachedDisplayScale = null;
        if (previous is { } old && Math.Abs(GetDisplayScale() - old) < 0.001) return;

        InvalidateDisplayScale();
        UpdateZoomLayout();
    }

    private void DetachDisplayScaleTracking()
    {
        if (_trackedXamlRoot is not { } xamlRoot) return;
        _trackedXamlRoot = null;
        xamlRoot.Changed -= XamlRoot_Changed;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hwnd);
    }

    private void OnAppWindowPlacementChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (sender.Presenter is OverlappedPresenter statePresenter)
        {
            if (statePresenter.State == OverlappedPresenterState.Maximized)
                _lastNonMinimizedWasMaximized = true;
            else if (statePresenter.State == OverlappedPresenterState.Restored)
                _lastNonMinimizedWasMaximized = false;
        }

        // A maximized/minimized AppWindow reports the presenter's temporary coordinates and
        // size. Retain the last restored ones instead, so exiting while maximized still reopens
        // on the monitor, location and size where the normal window actually lived.
        if (sender.Presenter is not OverlappedPresenter presenter
            || presenter.State != OverlappedPresenterState.Restored)
        {
            return;
        }

        if (args.DidPositionChange)
            _lastNormalPosition = new WindowPosition(sender.Position.X, sender.Position.Y);

        if (args.DidSizeChange)
            _lastNormalSize = new PixelSize(sender.Size.Width, sender.Size.Height);
    }

    private WindowPosition ClampToVisibleWorkArea(WindowPosition saved)
    {
        try
        {
            var point = new PointInt32(saved.X, saved.Y);
            var display = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Nearest);
            var work = display.WorkArea;
            var size = _appWindow.Size;

            // Keep the whole window inside the work area whenever it fits. If it is larger than
            // the current display (for example after a resolution/DPI change), anchor it at the
            // work area's top-left so its caption and resize affordances remain reachable.
            return WindowPlacementPolicy.ClampToWorkArea(
                saved,
                new PixelSize(size.Width, size.Height),
                new PixelRect(work.X, work.Y, work.Width, work.Height));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Saved window position could not be validated");
            return saved;
        }
    }

    internal Task SaveWindowPlacementAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _windowPlacementSaved, 1) != 0)
            return Task.CompletedTask;

        _appWindow.Changed -= OnAppWindowPlacementChanged;
        var position = _lastNormalPosition
            ?? new WindowPosition(_appWindow.Position.X, _appWindow.Position.Y);
        var isMaximized = _appWindow.Presenter is OverlappedPresenter presenter
            ? presenter.State == OverlappedPresenterState.Maximized
                || presenter.State == OverlappedPresenterState.Minimized && _lastNonMinimizedWasMaximized
            : _lastNonMinimizedWasMaximized;
        // Same rule as the position: the size that gets saved is the *restored* one, so exiting
        // while maximized reopens maximized but un-maximizes to the size the user last chose.
        var size = _lastNormalSize
            ?? new PixelSize(_appWindow.Size.Width, _appWindow.Size.Height);
        return _windowPlacementStore.SaveAsync(
            new WindowPlacement(position, isMaximized, WindowPlacementPolicy.ClampToMinimum(size)));
    }
}
