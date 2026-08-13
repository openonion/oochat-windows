using System;
using System.Threading;
using Microsoft.Windows.AppNotifications;

namespace ConnectOnion.WinUIClient.Services.Notifications;

/// <summary>
/// Process-wide capability probe for Windows App SDK app notifications.
///
/// Self-contained deployment carries the framework DLLs but not the Windows App Runtime
/// Singleton package that <see cref="AppNotificationManager"/> depends on. Microsoft explicitly
/// requires self-contained apps to probe <see cref="AppNotificationManager.IsSupported"/> before
/// using that surface. Some machines still pass that API probe but fail the first registration
/// because the optional Singleton notification service is incomplete, so an actual registration
/// failure is latched as unavailable for the rest of the process. Keeping the answer here gives
/// registration, delivery and Settings one consistent view instead of letting each call fail
/// independently.
/// </summary>
internal static class AppNotificationCapability
{
    private static readonly Lazy<bool> ApiSupported = new(DetectSupport);
    private static int _runtimeUnavailable;

    public static bool IsAvailable =>
        Volatile.Read(ref _runtimeUnavailable) == 0 && ApiSupported.Value;

    public static void MarkRuntimeUnavailable(string operation, Exception exception)
    {
        if (Interlocked.Exchange(ref _runtimeUnavailable, 1) == 0)
        {
            NotificationLog.Warn(
                $"Windows app notifications became unavailable during {operation}; " +
                "in-app notifications remain enabled",
                exception);
        }
    }

    private static bool DetectSupport()
    {
        try
        {
            var supported = AppNotificationManager.IsSupported();
            NotificationLog.Info(supported
                ? "Windows app notifications are supported"
                : "Windows app notifications are unavailable; in-app notifications remain enabled");
            return supported;
        }
        catch (Exception ex)
        {
            // A capability probe is allowed to fail on older or policy-restricted systems. Treat
            // that as unsupported; notification availability must never become a startup failure.
            NotificationLog.Warn("notification capability check failed", ex);
            return false;
        }
    }
}
