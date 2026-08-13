using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// The small animated starburst beside "Thinking…". Eight rounded rays breathe, drift and fade
/// out of phase with each other, so the mark reads as ongoing thought rather than as a progress
/// spinner — nothing about it implies a percentage or an ETA.
///
/// Deliberately not a <see cref="ProgressRing"/>: a determinate-looking spinner in a chat
/// transcript invites "how far along is it?", which is a question this state cannot answer.
///
/// <para><b>Why plain XAML shapes.</b> This was a Win2D <c>CanvasAnimatedControl</c>, and that is
/// the wrong tool for something realized inside a virtualized <see cref="ListView"/> item
/// template. Each canvas brings its own D3D device, swap chain and render thread, and Win2D
/// releases none of it unless <c>RemoveFromVisualTree()</c> is called — which a recycled row
/// cannot do, because Win2D does not support re-attaching one afterwards. So every realization
/// leaked: measured at roughly 55 threads and 23 MB <i>per chat turn</i>, taking a 40-turn
/// text-only conversation from 133 MB to 984 MB of private bytes. The composer's waveform avoids
/// Win2D for the same reason; keep this one on shapes too.</para>
///
/// <para><b>Lifetime.</b> The storyboard is started only while <see cref="IsActive"/> is true and
/// the control is loaded, and <c>Unloaded</c> stops it unconditionally, which covers both page
/// navigation and the virtualizing list recycling the container. Start/Stop are idempotent
/// because <c>Loaded</c>/<c>Unloaded</c> fire repeatedly on the same instance as the list
/// recycles. Everything animated is a <c>RenderTransform</c> or <c>Opacity</c>, so the loop runs
/// as an independent (composition-thread) animation and costs no layout pass.</para>
/// </summary>
public sealed partial class ThinkingIndicator : UserControl, IDisposable
{
    /// <summary>Read once: the system exposes this as a user setting that requires a restart of
    /// the app's animations to observe, and the app already treats it as a startup constant (see
    /// <c>DisclosureAnimation</c>).</summary>
    private static readonly bool AnimationsEnabled = new UISettings().AnimationsEnabled;

    private readonly Storyboard _spinner;
    private bool _isRunning;
    private bool _isUnloaded;
    private int _disposed;

    public ThinkingIndicator()
    {
        InitializeComponent();
        _spinner = (Storyboard)((FrameworkElement)Content).Resources["SpinnerStoryboard"];
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Whether the model is currently working. True shows the mark and runs the loop; false
    /// collapses the control and retires the loop. Bound straight to the message's existing
    /// running state — this control introduces no state of its own to keep in sync.
    /// </summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(ThinkingIndicator),
        new PropertyMetadata(false, OnIsActiveChanged));

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ThinkingIndicator)d).ApplyActiveState();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = false;
        ApplyActiveState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Unloaded is the one signal that reliably precedes a recycled list item being reused or
        // a page being navigated away from, so it stops unconditionally — regardless of IsActive,
        // which may still read true for a turn that is genuinely still running elsewhere.
        _isUnloaded = true;
        Stop();
    }

    private void ApplyActiveState()
    {
        if (IsActive && !_isUnloaded)
        {
            Visibility = Visibility.Visible;
            Start();
        }
        else
        {
            Stop();
            Visibility = Visibility.Collapsed;
        }
    }

    private void Start()
    {
        if (_isRunning) return;
        // Reduce Motion still gets the mark, just held still: the meaning is carried by the
        // "Thinking…" text beside it, so suppressing the motion costs nothing but the motion.
        // The rays keep their declared resting length and opacity, so this is a static starburst
        // rather than a gap.
        if (AnimationsEnabled) _spinner.Begin();
        _isRunning = true;
    }

    private void Stop()
    {
        if (!_isRunning) return;
        // Stop, not Pause: a stopped storyboard releases its animation clocks, and this control's
        // whole point is that a recycled row must leave nothing running behind it. The rays fall
        // back to their XAML values, which is the resting mark.
        _spinner.Stop();
        _isRunning = false;
    }

    /// <summary>Retires the loop when the containing chat page is discarded. Kept (and kept
    /// idempotent) because <c>ChatPage.DisposeThinkingIndicators</c> calls it on teardown; there
    /// are no native resources left to release since the Win2D canvas was removed.</summary>
    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Stop();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
    }
}
