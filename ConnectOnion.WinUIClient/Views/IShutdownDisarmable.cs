namespace ConnectOnion.WinUIClient.Views;

/// <summary>
/// A page holding timers, animations, or other work that must be stopped <b>synchronously</b>
/// when the app exits.
///
/// <para>This exists because <c>Unloaded</c> is not guaranteed to fire on window close, and it is
/// where pages normally disarm their timers. The dispatcher keeps pumping for a moment after
/// <c>Window.Closed</c> — and on the real exit path it pumps for the whole of
/// <c>App.ShutdownAsync</c>, measured at ~150–220 ms — so a <c>DispatcherTimer</c> that nobody
/// stopped can still tick against a visual tree the framework is tearing down. That surfaces as
/// an access violation (<c>0xC0000005</c>) inside native <c>Microsoft.UI.Xaml.dll</c>: no managed
/// <c>catch</c> sees it, and the log shows a clean shutdown milliseconds earlier.</para>
///
/// <para><c>MainWindow.DetachWindowServices</c> calls this on whatever page is currently in the
/// content frame, right before the host is stopped. Implementations must be synchronous and
/// idempotent, and must do <i>only</i> disarming — no persistence, no service calls. The page is
/// about to die; work queued here is work that can outlive the tree it touches.</para>
/// </summary>
public interface IShutdownDisarmable
{
    /// <summary>Stops everything this page (and its controls) could fire after the window closes.</summary>
    void DisarmForShutdown();
}
