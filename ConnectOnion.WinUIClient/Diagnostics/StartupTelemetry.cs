using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Serilog;

namespace ConnectOnion.WinUIClient.Diagnostics;

/// <summary>
/// Measures how long this process took to put a drawn window on screen, and reports it once.
///
/// The origin is the OS process-start time, not a stopwatch started in managed code: on a cold
/// start most of the wall clock is spent in CLR + Windows App SDK bootstrap *before* the
/// <c>App</c> constructor runs, and a stopwatch started there would report a flattering number
/// that no user experiences. <see cref="StartupProfiler"/> holds the (platform-free) collection
/// logic; this type owns the two pieces that need the framework — the process clock and the
/// compositor's first frame.
///
/// "First frame" comes from <see cref="CompositionTarget.Rendering"/>, which fires on the first
/// composition pass after activation. The handler unsubscribes itself on that first tick: an
/// always-armed per-frame callback is exactly the kind of thing the dispatcher keeps calling
/// after <c>Window.Closed</c> (see CLAUDE.md's shutdown-race note), so this must not outlive
/// the measurement it exists for.
///
/// Cost when not benchmarking is one file-less log line — the JSON report is written only when
/// <see cref="StartupProfiler.OutputPathEnvironmentVariable"/> points somewhere.
/// </summary>
internal static class StartupTelemetry
{
    private static readonly object ReportGate = new();
    private static EventWaitHandle? _exitEvent;
    private static RegisteredWaitHandle? _exitRegistration;
    private static StartupMemory? _firstFrameMemory;
    private static readonly DateTime ProcessStartUtc = ResolveProcessStartUtc();

    private static readonly StartupProfiler Profiler =
        new(() => (DateTime.UtcNow - ProcessStartUtc).TotalMilliseconds);

    /// <summary>Records a launch phase. Safe to call from any thread; later duplicates of an
    /// already-recorded phase are ignored.</summary>
    internal static void Mark(string phase)
    {
        // Only a *new* mark may trigger a report. Several readiness phases are raised from
        // events that keep firing for the life of the process — `firstInteractive` comes from
        // GotFocus, which bubbles from every control the user ever clicks — so reporting on
        // every call would sample memory and write a log line on each focus change, forever.
        if (!Profiler.Mark(phase)) return;

        // A readiness phase can arrive either side of first frame — sessionListLoaded and
        // shellInitialized routinely land before it — but only a report written *after* first
        // frame is worth anything, so that is the gate. Rewriting as each one arrives is what
        // lets the harness observe the complete timeline. The summary is logged once, at first
        // frame: the later rewrites exist for the harness, and a user's log should read the
        // same as it always has.
        if (Profiler.HasMark(StartupPhases.FirstFrame))
            Report(logSummary: phase == StartupPhases.FirstFrame);
    }

    /// <summary>Connects the benchmark's named event to the same graceful exit action used by
    /// File &gt; Exit. Unset in normal runs, so this adds no waiter or handle for users.</summary>
    internal static void ArmPerformanceExit(DispatcherQueue dispatcher, Action requestExit)
    {
        var eventName = Environment.GetEnvironmentVariable(
            StartupProfiler.ExitEventEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(eventName)) return;

        try
        {
            _exitEvent = EventWaitHandle.OpenExisting(eventName);
            _exitRegistration = ThreadPool.RegisterWaitForSingleObject(
                _exitEvent,
                (_, timedOut) =>
                {
                    if (!timedOut) _ = dispatcher.TryEnqueue(() => requestExit());
                },
                null,
                Timeout.Infinite,
                executeOnlyOnce: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Performance exit event {EventName} could not be armed", eventName);
        }
    }

    /// <summary>Releases the benchmark waiter. Called from <c>MainWindow.DetachWindowServices</c>
    /// for the same reason every other window-owned active is disarmed there: a registered wait
    /// that outlives the window can re-enter a torn-down tree (see CLAUDE.md's shutdown-race
    /// note). A no-op outside benchmark runs.</summary>
    internal static void DisarmPerformanceExit()
    {
        _exitRegistration?.Unregister(null);
        _exitRegistration = null;
        _exitEvent?.Dispose();
        _exitEvent = null;
    }

    /// <summary>
    /// Arms the first-frame measurement. Call immediately after <c>Window.Activate()</c> —
    /// subscribing earlier would catch a composition pass from before the window existed.
    /// </summary>
    internal static void TrackFirstFrame()
    {
        if (Profiler.HasMark(StartupPhases.FirstFrame)) return;
        CompositionTarget.Rendering += OnRendering;
    }

    private static void OnRendering(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnRendering;
        Mark(StartupPhases.FirstFrame);
    }

    private static void Report(bool logSummary)
    {
        lock (ReportGate)
        {
            try
            {
                // Memory is captured once, at first frame, and reused by every later rewrite.
                // The budgets and the recorded baseline in docs/PERFORMANCE.md are written
                // against the first-frame figure, so re-sampling it as readiness marks arrive
                // would silently redefine the metric while keeping its name.
                _firstFrameMemory ??= CaptureMemory();

                var metrics = Profiler.Snapshot(_firstFrameMemory, Configuration, IsPackaged());

                if (logSummary) Log.Information("{StartupSummary}", metrics.ToLogLine());
                WriteReportIfRequested(metrics);
            }
            catch (Exception ex)
            {
                // Telemetry must never be able to break a launch it is only observing.
                Log.Warning(ex, "Startup telemetry could not be captured");
            }
        }
    }

    private static StartupMemory CaptureMemory()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new StartupMemory(
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.GetTotalMemory(forceFullCollection: false));
    }

    private static void WriteReportIfRequested(StartupMetrics metrics)
    {
        var path = Environment.GetEnvironmentVariable(StartupProfiler.OutputPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path)) return;

        // The benchmark polls this file while later readiness marks rewrite it. AtomicTextFile
        // keeps every observed document complete and retries the short Windows sharing race when
        // the poller has the previous destination open without FileShare.Delete.
        AtomicTextFile.WriteAllText(path, metrics.ToJson());
    }

    private static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private static bool IsPackaged()
    {
        // Package.Current throws when running unpackaged (dotnet run -p:RunUnpackaged=true);
        // that throw is the only reliable way to tell the two apart. Same trick as AppVersionService.
        try
        {
            _ = Windows.ApplicationModel.Package.Current.Id.Version;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Process.StartTime can throw on a locked-down process; falling back to "now" makes that run
    // report an implausibly small number rather than losing the whole measurement, and the
    // harness's outlier reporting is what catches it.
    private static DateTime ResolveProcessStartUtc()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }
}
