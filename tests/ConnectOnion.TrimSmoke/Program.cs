using ConnectOnion.TrimSmoke;
using ConnectOnion.WinUIClient.Data;

// Isolate the data root before anything reads AppStorage.RootDir, which caches it in a static.
// Without this the harness would write into the real %AppData%\ConnectOnion profile.
var dataRoot = Environment.GetEnvironmentVariable("CONNECTONION_TRIMSMOKE_ROOT");
if (string.IsNullOrWhiteSpace(dataRoot))
{
    dataRoot = Path.Combine(Path.GetTempPath(), "ConnectOnion.TrimSmoke", Guid.NewGuid().ToString("N"));
}
Environment.SetEnvironmentVariable(AppStorage.DataRootEnvironmentVariable, dataRoot);
AppStorage.EnsureDirectories();

// persist / verify are the two halves of a real restart: the runner invokes this executable twice
// against one CONNECTONION_TRIMSMOKE_ROOT. "all" does both in one process, which is the fast loop.
var phase = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
if (phase is not ("all" or "persist" or "verify"))
{
    Console.Error.WriteLine($"Unknown phase '{phase}'. Expected all, persist or verify.");
    return 2;
}

Console.WriteLine($"ConnectOnion trim smoke — phase '{phase}'");
Console.WriteLine($"Data root: {dataRoot}");
// Printed rather than asserted so a run that accidentally has reflection back on is obvious in
// the log: with it enabled these checks would pass even against a reflection-based serializer,
// which would make the whole harness meaningless.
var reflectionConfigured = AppContext.TryGetSwitch(
    "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", out var reflectionEnabled);
Console.WriteLine(reflectionConfigured
    ? $"JsonSerializer reflection fallback: {(reflectionEnabled ? "ENABLED (checks are not meaningful)" : "disabled")}"
    : "JsonSerializer reflection fallback: unset (defaults to enabled — checks are not meaningful)");

var harness = new Harness();
ArmedCheck.Run(harness);

if (phase is "all" or "persist")
{
    ProtocolChecks.Run(harness);
    IdentityChecks.Run(harness, freshDataRoot: true);
    await PersistenceChecks.WriteAsync(harness);
}

if (phase is "all" or "verify")
{
    if (phase == "verify")
    {
        // A verify-only process has none of the writer's in-memory state, which is the point —
        // but the pure paths are cheap and worth re-running where the linker output differs.
        ProtocolChecks.Run(harness);
        IdentityChecks.Run(harness, freshDataRoot: false);
    }
    await PersistenceChecks.VerifyAsync(harness);
}

return harness.Report();
