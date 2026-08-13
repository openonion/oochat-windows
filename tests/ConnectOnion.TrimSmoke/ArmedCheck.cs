using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace ConnectOnion.TrimSmoke;

/// <summary>
/// Proves the harness is armed before it reports anything else.
///
/// <para>Every other check in this project passes trivially if the reflection fallback is on —
/// which is the default everywhere except a trimmed publish, and is a single stray MSBuild
/// property away from being on here too. A green run would then mean nothing at all. So the
/// first thing the harness does is call the reflection-based serializer and require it to
/// <i>fail</i>. If it succeeds, the run is not measuring what it claims to and says so.</para>
/// </summary>
internal static class ArmedCheck
{
    // The one place in the repo where a reflection-based JsonSerializer call is intended. It is
    // never on a code path that has to work — the call exists precisely so its failure can be
    // asserted — so nothing is lost when the linker trims what it would have needed.
    [UnconditionalSuppressMessage(
        "Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Deliberate negative control: this call must throw under trimming, and " +
                        "the check fails if it does not.")]
    public static void Run(Harness h)
    {
        h.Section("Harness self-test");

        h.Check("the reflection-based serializer is disabled", () =>
        {
            var probe = new Dictionary<string, object?> { ["type"] = "probe" };

            try
            {
                var json = JsonSerializer.Serialize(probe);
                throw new InvalidOperationException(
                    "JsonSerializer.Serialize succeeded, so the reflection fallback is enabled and "
                    + "every other check in this run is meaningless. Confirm "
                    + "JsonSerializerIsReflectionEnabledByDefault=false reached the runtimeconfig. "
                    + $"(It returned: {json})");
            }
            // "Reflection-based serialization has been disabled for this application." The exact
            // type is InvalidOperationException, not the NotSupportedException the IL2026 warning
            // text leads you to expect — worth knowing when reading a crash report from a trimmed
            // build, where this is what a regressed serialization path actually throws.
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("Reflection-based serialization", StringComparison.Ordinal))
            {
            }
        });
    }
}
