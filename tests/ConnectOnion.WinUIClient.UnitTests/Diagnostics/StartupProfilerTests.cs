using System.Text.Json;
using ConnectOnion.WinUIClient.Diagnostics;

namespace ConnectOnion.WinUIClient.UnitTests.Diagnostics;

public sealed class StartupProfilerTests
{
    private static StartupProfiler ProfilerOver(params double[] clockReadings)
    {
        var index = 0;
        return new StartupProfiler(() => clockReadings[index++]);
    }

    private static StartupMetrics SnapshotOf(StartupProfiler profiler) =>
        profiler.Snapshot(new StartupMemory(200, 100, 50), "Release", packaged: false);

    [Fact]
    public void Mark_RecordsElapsedTimeFromTheClock()
    {
        var profiler = ProfilerOver(120, 480);
        profiler.Mark(StartupPhases.ManagedEntry);
        profiler.Mark(StartupPhases.FirstFrame);

        Assert.Equal(120, profiler.ElapsedFor(StartupPhases.ManagedEntry));
        Assert.Equal(480, profiler.ElapsedFor(StartupPhases.FirstFrame));
    }

    [Fact]
    public void Mark_SamePhaseTwice_KeepsTheFirstReading()
    {
        // OnLaunched can be re-entered on activation paths; a later second mark would silently
        // inflate the reported startup time.
        var profiler = ProfilerOver(100, 900);
        profiler.Mark(StartupPhases.WindowActivated);
        profiler.Mark(StartupPhases.WindowActivated);

        Assert.Equal(100, profiler.ElapsedFor(StartupPhases.WindowActivated));
        Assert.Single(SnapshotOf(profiler).Marks);
    }

    [Fact]
    public void Mark_ReturnsTrueOnlyForTheCallThatRecordedThePhase()
    {
        // StartupTelemetry gates its report on this return value. Several readiness phases are
        // raised from events that keep firing for the life of the process — firstInteractive
        // comes from GotFocus, which bubbles from every control the user ever clicks — so a
        // caller that cannot tell a repeat from a new mark re-samples memory and writes a log
        // line on every focus change.
        var profiler = ProfilerOver(100, 900);

        Assert.True(profiler.Mark(StartupPhases.FirstInteractive));
        Assert.False(profiler.Mark(StartupPhases.FirstInteractive));
        Assert.False(profiler.Mark(StartupPhases.FirstInteractive));
        Assert.Single(SnapshotOf(profiler).Marks);
    }

    [Fact]
    public void ElapsedFor_PhaseNeverMarked_ReturnsNull()
    {
        var profiler = ProfilerOver(10);
        profiler.Mark(StartupPhases.ManagedEntry);

        Assert.Null(profiler.ElapsedFor(StartupPhases.FirstFrame));
        Assert.False(profiler.HasMark(StartupPhases.FirstFrame));
    }

    [Fact]
    public void Snapshot_OrdersMarksChronologically_NotByArrivalOrder()
    {
        var profiler = ProfilerOver(500, 100);
        profiler.Mark(StartupPhases.WindowCreated);
        profiler.Mark(StartupPhases.ManagedEntry);

        var phases = SnapshotOf(profiler).Marks.Select(m => m.Phase).ToArray();

        Assert.Equal([StartupPhases.ManagedEntry, StartupPhases.WindowCreated], phases);
    }

    [Fact]
    public void TimeToFirstFrame_IsNullUntilTheFirstFrameIsMarked()
    {
        var profiler = ProfilerOver(50, 900);
        profiler.Mark(StartupPhases.WindowActivated);
        Assert.Null(SnapshotOf(profiler).TimeToFirstFrameMs);

        profiler.Mark(StartupPhases.FirstFrame);
        Assert.Equal(900, SnapshotOf(profiler).TimeToFirstFrameMs);
    }

    [Fact]
    public void ToJson_EmitsTheShapeTheBenchmarkHarnessReads()
    {
        // The harness (scripts/Measure-Performance.ps1) indexes these exact property and phase
        // names, so this test is the contract between the two.
        var profiler = ProfilerOver(110, 450, 700, 715, 830, 850, 880, 920, 950);
        foreach (var phase in StartupPhases.Ordered) profiler.Mark(phase);

        var metrics = profiler.Snapshot(
            new StartupMemory(198_000_000, 125_000_000, 3_200_000), "Release", packaged: false);

        using var document = JsonDocument.Parse(metrics.ToJson());
        var root = document.RootElement;

        Assert.Equal("Release", root.GetProperty("configuration").GetString());
        Assert.False(root.GetProperty("packaged").GetBoolean());
        Assert.Equal(830, root.GetProperty("timeToFirstFrameMs").GetDouble());
        Assert.Equal(198_000_000, root.GetProperty("memory").GetProperty("workingSetBytes").GetInt64());
        Assert.Equal(125_000_000, root.GetProperty("memory").GetProperty("privateBytes").GetInt64());
        Assert.Equal(3_200_000, root.GetProperty("memory").GetProperty("managedHeapBytes").GetInt64());

        var marks = root.GetProperty("marks").EnumerateArray()
            .Select(m => m.GetProperty("phase").GetString()!)
            .ToArray();
        Assert.Equal(StartupPhases.Ordered, marks);
    }

    [Fact]
    public void ToJson_IsTrimSafe_AndDoesNotDependOnReflectionSerialization()
    {
        // The app publishes trimmed with JsonSerializerIsReflectionEnabledByDefault=false, so a
        // reflection-based serialize throws at runtime in exactly the build this measures. Any
        // regression to JsonSerializer.Serialize would surface here as a thrown exception.
        var profiler = ProfilerOver(10);
        profiler.Mark(StartupPhases.ManagedEntry);

        var json = SnapshotOf(profiler).ToJson();

        Assert.Contains("\"managedEntry\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ToLogLine_ReportsPerPhaseDeltasNotCumulativeTimes()
    {
        var profiler = ProfilerOver(100, 400, 700, 720, 800, 820, 850, 900, 930);
        foreach (var phase in StartupPhases.Ordered) profiler.Mark(phase);

        var line = profiler
            .Snapshot(new StartupMemory(190 * 1024 * 1024, 0, 3 * 1024 * 1024), "Release", false)
            .ToLogLine();

        Assert.Contains("Startup 800 ms", line, StringComparison.Ordinal);
        Assert.Contains("managedEntry +100", line, StringComparison.Ordinal);
        Assert.Contains("hostStarted +300", line, StringComparison.Ordinal);
        Assert.Contains("firstFrame +80", line, StringComparison.Ordinal);
        Assert.Contains("working set 190 MB", line, StringComparison.Ordinal);
    }
}
