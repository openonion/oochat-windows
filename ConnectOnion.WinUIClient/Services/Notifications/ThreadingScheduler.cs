using System;
using System.Threading;

namespace ConnectOnion.WinUIClient.Services.Notifications;

/// <summary>
/// Real <see cref="INotificationScheduler"/> backed by a one-shot timer. The interface exists so
/// the coordinator's grace-period logic can be unit-tested against a fake clock instead of
/// waiting out real seconds.
/// </summary>
public sealed class ThreadingScheduler : INotificationScheduler
{
    /// <summary>Runs <paramref name="callback"/> once after <paramref name="delay"/>. The
    /// returned handle is the timer itself, so the caller's <c>Dispose</c> cancels a pending
    /// callback — which is exactly how a reconnect inside the grace period stops the
    /// "connection lost" notification from ever firing.</summary>
    public IDisposable Schedule(TimeSpan delay, Action callback)
        // InfiniteTimeSpan as the period is what makes this one-shot rather than repeating.
        => new Timer(_ =>
        {
            // The callback runs on a thread-pool thread with nothing above it to catch: an
            // escaping exception would take the process down, so a failed notification is
            // logged and swallowed instead.
            try { callback(); }
            catch (Exception ex) { NotificationLog.Warn("scheduled callback failed", ex); }
        }, null, delay, Timeout.InfiniteTimeSpan);
}
