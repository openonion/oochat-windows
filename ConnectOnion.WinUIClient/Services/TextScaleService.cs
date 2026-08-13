using System;
using ConnectOnion.WinUIClient.Models;
using Microsoft.UI.Dispatching;
using Windows.UI.ViewManagement;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Surfaces the Windows "Make text bigger" setting (Settings → Accessibility → Text size) so the
/// shell can honour it.
///
/// <para><b>Why this exists at all.</b> In WPF and UWP, <c>IsTextScaleFactorEnabled</c> defaults
/// to true and the framework scales text for you. WinUI 3 does not implement it — the property
/// is present and inert — so an app built on WinUI 3 ignores the setting entirely no matter how
/// its font sizes are declared. The shared product type ramp solves the separate maintainability
/// problem, but it does <i>not</i> fix OS text scaling: there is nothing in the framework to
/// re-resolve those resources against. Reading the factor and applying it ourselves is the only
/// route.</para>
///
/// <para>The value is a multiplier in the range 1.0–2.25 (100%–225% in the Settings slider). It
/// is read once at construction and then kept current: the OS raises
/// <see cref="UISettings.TextScaleFactorChanged"/> on a background thread, so the handler hops to
/// the UI thread before telling anyone, and subscribers can touch the visual tree directly.</para>
/// </summary>
public sealed class TextScaleService : IDisposable
{
    /// <summary>Matches the ceiling of the Windows text-size slider.</summary>
    public const double MaximumScale = 2.25;

    private readonly UISettings? _settings;
    private readonly DispatcherQueue? _dispatcher;
    private bool _disposed;

    public TextScaleService()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        try
        {
            _settings = new UISettings();
            Current = Normalize(_settings.TextScaleFactor);
            _settings.TextScaleFactorChanged += OnTextScaleFactorChanged;
        }
        catch (Exception ex)
        {
            // UISettings is unavailable in some hosts (and in a headless test process). Text
            // scaling is an enhancement — failing to read it must not stop the window opening.
            Serilog.Log.Warning(ex, "OS text scale could not be read; using 100%");
            Current = 1.0;
        }
    }

    /// <summary>The current OS text scale as a multiplier; 1.0 when the setting is at 100% or
    /// could not be read.</summary>
    public double Current { get; private set; } = 1.0;

    /// <summary>The user's app-specific interface text-size multiplier.</summary>
    public double InterfaceScale { get; private set; } = 1.0;

    /// <summary>The OS accessibility scale combined with the app-specific text-size preset.</summary>
    public double Effective => Current * InterfaceScale;

    /// <summary>Raised on the UI thread after the OS or app-specific text scale changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Applies an interface text-size preset and notifies the shell immediately.</summary>
    public void ApplyInterfaceTextSize(InterfaceTextSize size)
    {
        var updated = size switch
        {
            InterfaceTextSize.Small => 0.9,
            InterfaceTextSize.Large => 1.15,
            _ => 1.0,
        };
        if (_disposed || Math.Abs(updated - InterfaceScale) < 0.001) return;
        InterfaceScale = updated;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnTextScaleFactorChanged(UISettings sender, object args)
    {
        // Raised on a background thread; every subscriber re-lays out the window.
        var updated = Normalize(sender.TextScaleFactor);
        if (_dispatcher is null)
        {
            Apply(updated);
            return;
        }

        _dispatcher.TryEnqueue(() => Apply(updated));
    }

    private void Apply(double updated)
    {
        if (_disposed || Math.Abs(updated - Current) < 0.001) return;
        Current = updated;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static double Normalize(double factor)
        => double.IsFinite(factor) && factor > 0
            ? Math.Clamp(factor, 1.0, MaximumScale)
            : 1.0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_settings is not null)
            _settings.TextScaleFactorChanged -= OnTextScaleFactorChanged;
    }
}
