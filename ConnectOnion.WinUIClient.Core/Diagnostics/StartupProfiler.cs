using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectOnion.WinUIClient.Diagnostics;

/// <summary>
/// The launch phases the app marks on its way to a visible window, in the order
/// they occur. Names are part of the benchmark report's JSON contract
/// (<c>scripts/Measure-Performance.ps1</c> reads them), so renaming one is a
/// breaking change to the harness, not a cosmetic edit.
/// </summary>
public static class StartupPhases
{
    /// <summary>First line of authored code — the <c>App</c> constructor. Everything before
    /// it is CLR + Windows App SDK bootstrap, which is exactly the part a cold start pays for
    /// and a warm start does not.</summary>
    public const string ManagedEntry = "managedEntry";

    /// <summary>Generic Host started: DI graph built, hosted services running.</summary>
    public const string HostStarted = "hostStarted";

    /// <summary>The single <c>MainWindow</c> instance exists (XAML parsed, tree built).</summary>
    public const string WindowCreated = "windowCreated";

    /// <summary><c>Window.Activate()</c> returned. The window is *shown*, but not yet drawn.</summary>
    public const string WindowActivated = "windowActivated";

    /// <summary>The compositor rendered its first frame. This is the number that matches what a
    /// user calls "the app started", and the one budgets are written against.</summary>
    public const string FirstFrame = "firstFrame";

    /// <summary>The shell has accepted focus, proving that it is interactive rather than merely
    /// visible.</summary>
    public const string FirstInteractive = "firstInteractive";

    /// <summary>The initial persisted session list has been loaded and bound to the shell.</summary>
    public const string SessionListLoaded = "sessionListLoaded";

    /// <summary>The first opened conversation has finished restoring and rendering its visible
    /// message containers. Absent when the startup fixture opens no conversation, when the
    /// conversation it opens is empty, and when the restore failed — in all three cases nothing
    /// was rendered, and a mark would let the harness time work that never happened.</summary>
    public const string FirstConversationRendered = "firstConversationRendered";

    /// <summary>The shell's own startup chain — session list, tray recents, first navigation,
    /// initial focus — has run to completion.
    ///
    /// <para>Named for what it measures rather than when it lands: it is routinely reached
    /// *before* <see cref="FirstFrame"/>, because that chain runs on the UI thread from
    /// <c>RootGrid.Loaded</c> and finishes before the compositor's first pass. An earlier name
    /// promised "deferred startup work", which invited reading a pre-first-frame number as
    /// post-launch work.</para></summary>
    public const string ShellInitialized = "shellInitialized";

    /// <summary>
    /// Nominal launch order, used only as a tie-breaker when two marks share an elapsed time.
    /// It is *not* the order a report renders in — <c>hostStarted</c> and <c>windowCreated</c>
    /// overlap by design (see <c>App.OnLaunched</c>), so reports sort chronologically.
    /// </summary>
    public static readonly IReadOnlyList<string> Ordered =
        [
            ManagedEntry,
            HostStarted,
            WindowCreated,
            WindowActivated,
            FirstFrame,
            FirstInteractive,
            SessionListLoaded,
            FirstConversationRendered,
            ShellInitialized,
        ];
}

/// <summary>One phase and how long after process start it was reached.</summary>
/// <param name="Phase">A <see cref="StartupPhases"/> name.</param>
/// <param name="ElapsedMs">Milliseconds since the OS created the process — not since managed
/// code began, which would hide the bootstrap cost entirely.</param>
public sealed record StartupMark(string Phase, double ElapsedMs);

/// <summary>Process memory at the moment the snapshot was taken.</summary>
/// <param name="WorkingSetBytes">Physical memory in use — what Task Manager shows.</param>
/// <param name="PrivateBytes">Committed memory unique to this process.</param>
/// <param name="ManagedHeapBytes">GC heap size; only observable in-process, which is why the
/// app reports it rather than the external harness.</param>
public sealed record StartupMemory(long WorkingSetBytes, long PrivateBytes, long ManagedHeapBytes);

/// <summary>A completed launch measurement, ready to serialize.</summary>
public sealed record StartupMetrics
{
    public required DateTimeOffset CapturedUtc { get; init; }

    /// <summary>"Debug" or "Release" — a Debug timing is not a result, and the report says so.</summary>
    public required string Configuration { get; init; }

    /// <summary>True when the process was launched packaged (MSIX), which has its own
    /// activation cost and is not comparable with an unpackaged run.</summary>
    public required bool Packaged { get; init; }

    public required IReadOnlyList<StartupMark> Marks { get; init; }

    public required StartupMemory Memory { get; init; }

    /// <summary>Process start → first rendered frame, or null if the run never got there.</summary>
    [JsonIgnore]
    public double? TimeToFirstFrameMs =>
        Marks.FirstOrDefault(m => m.Phase == StartupPhases.FirstFrame)?.ElapsedMs;

    /// <summary>
    /// Writes the report the benchmark harness reads. Hand-written with <see cref="Utf8JsonWriter"/>
    /// rather than <c>JsonSerializer.Serialize</c> on purpose: the app publishes trimmed, so
    /// <c>JsonSerializerIsReflectionEnabledByDefault</c> is false and a reflection-based
    /// serialize throws at runtime in exactly the configuration this measures. Emitting the
    /// document directly keeps the type out of any source-gen context and trim-safe by
    /// construction.
    /// </summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("capturedUtc", CapturedUtc);
            writer.WriteString("configuration", Configuration);
            writer.WriteBoolean("packaged", Packaged);
            if (TimeToFirstFrameMs is { } firstFrame)
            {
                writer.WriteNumber("timeToFirstFrameMs", Math.Round(firstFrame, 1));
            }

            writer.WriteStartArray("marks");
            foreach (var mark in Marks)
            {
                writer.WriteStartObject();
                writer.WriteString("phase", mark.Phase);
                writer.WriteNumber("elapsedMs", Math.Round(mark.ElapsedMs, 1));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartObject("memory");
            writer.WriteNumber("workingSetBytes", Memory.WorkingSetBytes);
            writer.WriteNumber("privateBytes", Memory.PrivateBytes);
            writer.WriteNumber("managedHeapBytes", Memory.ManagedHeapBytes);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>One-line log form: total, then each phase's own share of it.</summary>
    public string ToLogLine()
    {
        var deltas = new List<string>();
        var previous = 0.0;
        foreach (var mark in Marks)
        {
            deltas.Add(string.Create(
                CultureInfo.InvariantCulture, $"{mark.Phase} +{mark.ElapsedMs - previous:F0}"));
            previous = mark.ElapsedMs;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Startup {TimeToFirstFrameMs ?? previous:F0} ms [{string.Join(", ", deltas)}], " +
            $"working set {Memory.WorkingSetBytes / 1024 / 1024} MB, " +
            $"managed heap {Memory.ManagedHeapBytes / 1024 / 1024} MB");
    }
}

/// <summary>
/// Collects launch phase timings against a caller-supplied clock.
///
/// The clock is injected rather than read from <c>Process.GetCurrentProcess()</c> here so this
/// type stays platform-free and headlessly testable; the app passes a function measuring from
/// the real OS process-start time (see <c>StartupTelemetry</c>). Every mark is relative to that
/// single origin, so a report never needs to add phases up and can't drift by doing so.
///
/// Marking is idempotent per phase — first write wins — because <c>OnLaunched</c> can be
/// re-entered on activation paths and a second, later mark would silently inflate the result.
/// </summary>
public sealed class StartupProfiler
{
    /// <summary>Set this to a file path to have the app write its <see cref="StartupMetrics"/>
    /// JSON there once the first frame renders. Unset in normal use: the benchmark harness is
    /// the only thing that sets it, so a user's run does nothing but log a line.</summary>
    public const string OutputPathEnvironmentVariable = "CONNECTONION_PERF_OUT";

    /// <summary>Name of a pre-created Windows event that the benchmark signals to request the
    /// application's real, graceful exit path.</summary>
    public const string ExitEventEnvironmentVariable = "CONNECTONION_PERF_EXIT_EVENT";

    /// <summary>When set to 1, the benchmark fixture opens its active conversation at startup so
    /// first-conversation rendering can be measured independently of the empty Home route.</summary>
    public const string OpenConversationEnvironmentVariable =
        "CONNECTONION_PERF_OPEN_CONVERSATION";

    private readonly Func<double> _elapsedMs;
    private readonly List<StartupMark> _marks = [];
    private readonly Lock _gate = new();

    /// <param name="elapsedMillisecondsSinceProcessStart">Returns milliseconds since the OS
    /// created this process.</param>
    public StartupProfiler(Func<double> elapsedMillisecondsSinceProcessStart)
        => _elapsedMs = elapsedMillisecondsSinceProcessStart;

    /// <summary>Records <paramref name="phase"/> at the current elapsed time. A phase already
    /// marked is ignored.</summary>
    /// <returns>True when this call recorded the phase; false when it was already marked.
    /// Callers use this to do per-phase work exactly once — several marks are raised from
    /// events that keep firing for the life of the process (focus, in particular), so
    /// "did anything change?" has to be answerable without re-scanning the marks.</returns>
    public bool Mark(string phase)
    {
        lock (_gate)
        {
            if (_marks.Any(m => m.Phase == phase)) return false;
            _marks.Add(new StartupMark(phase, _elapsedMs()));
            return true;
        }
    }

    /// <summary>True once <paramref name="phase"/> has been marked.</summary>
    public bool HasMark(string phase)
    {
        lock (_gate) return _marks.Any(m => m.Phase == phase);
    }

    /// <summary>Elapsed time at <paramref name="phase"/>, or null if it was never reached.</summary>
    public double? ElapsedFor(string phase)
    {
        lock (_gate) return _marks.FirstOrDefault(m => m.Phase == phase)?.ElapsedMs;
    }

    /// <summary>
    /// Freezes the marks collected so far into a report, ordered by when they actually happened.
    ///
    /// <para>Chronological rather than by <see cref="StartupPhases.Ordered"/> because the launch
    /// is no longer a straight line: the host starts concurrently with window construction, so
    /// which of <c>hostStarted</c> and <c>windowCreated</c> lands first depends on the run.
    /// Sorting by elapsed time keeps every phase delta in the log non-negative and lets the
    /// report show the overlap instead of hiding it behind a fixed sequence.</para>
    /// </summary>
    public StartupMetrics Snapshot(StartupMemory memory, string configuration, bool packaged)
    {
        List<StartupMark> ordered;
        lock (_gate)
        {
            ordered = [.. _marks.OrderBy(m => m.ElapsedMs).ThenBy(m => IndexOf(m.Phase))];
        }

        return new StartupMetrics
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            Configuration = configuration,
            Packaged = packaged,
            Marks = ordered,
            Memory = memory,
        };
    }

    // Unknown phases sort last rather than throwing — a report is diagnostics, and losing a
    // future mark's position is cheaper than losing the whole measurement.
    private static int IndexOf(string phase)
    {
        var index = StartupPhases.Ordered.ToList().IndexOf(phase);
        return index < 0 ? int.MaxValue : index;
    }
}
