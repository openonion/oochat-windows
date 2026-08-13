using System.Text.Json;
using ConnectOnion.Protocol.Runtime;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>
/// Extracts the token-usage ledger rows from a finished run's raw event stream.
///
/// Kept separate from <see cref="ChatTurnProjection"/> on purpose. That class's contract is
/// "events → chat bubbles", it is driven identically by the live and the headless path, and its
/// live/headless parity is a load-bearing invariant — bolting accounting onto it would put two
/// unrelated jobs behind one fragile guarantee. This is a pure function over the snapshot instead:
/// no UI, no database, no state, so it is trivially unit-testable.
///
/// Note that <see cref="ChatTurnProjection"/> also reads token counts, but only to render the
/// per-turn summary bubble, and it collapses the whole turn onto a single model. This is the
/// authoritative, per-call extraction.
/// </summary>
public static class UsageProjector
{
    /// <summary>
    /// One row per <c>llm_result</c> that actually reported a model. Called for completed
    /// <b>and</b> failed/cancelled runs: the tokens were spent either way, and a ledger that only
    /// records successes does not add up.
    /// </summary>
    /// <param name="agentName">Snapshotted into each row so the ledger stays readable after the
    /// agent is deleted.</param>
    public static IReadOnlyList<UsageRecord> Extract(ConversationRunSnapshot snapshot, string? agentName)
    {
        var rows = new List<UsageRecord>();

        foreach (var e in snapshot.Events)
        {
            if (!string.Equals(e.Type, "llm_result", StringComparison.Ordinal)) continue;

            JsonElement root;
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(e.RawJson);
                root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var model = Str(root, "model");

                // No model means we cannot attribute the spend to anything, and a usage row whose
                // model is "unknown" is worse than no row — it pollutes the only axis that matters.
                if (string.IsNullOrWhiteSpace(model)) continue;

                var (input, output, cached, cacheWrite) = ParseUsage(root);

                // A call that reported no tokens at all carries no information; skip it rather than
                // inflating the call count with empty rows.
                if (input == 0 && output == 0 && cached == 0 && cacheWrite == 0) continue;

                rows.Add(new UsageRecord(
                    // The server's event id makes the insert idempotent, so re-persisting a run
                    // (a retry, a replay) can never double-count. Fall back to a fresh id only when
                    // the server did not supply one.
                    Id: e.EventId ?? Guid.NewGuid().ToString(),
                    ConversationId: snapshot.ConversationId,
                    AgentId: snapshot.AgentId,
                    AgentName: agentName,
                    Model: model!,
                    InputTokens: input,
                    OutputTokens: output,
                    CachedTokens: cached,
                    CacheWriteTokens: cacheWrite,
                    DurationMs: Num(root, "duration_ms"),
                    CreatedAt: TimestampMs(root)));
            }
            catch (JsonException)
            {
                // A malformed frame must not cost us the rest of the turn's accounting.
                continue;
            }
            finally
            {
                doc?.Dispose();
            }
        }

        return rows;
    }

    private static (long Input, long Output, long Cached, long CacheWrite) ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
            return (0, 0, 0, 0);

        return (
            Long(u, "input_tokens"),
            Long(u, "output_tokens"),
            Long(u, "cached_tokens"),
            Long(u, "cache_write_tokens"));
    }

    private static long Long(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var l) ? l : 0;

    private static double? Num(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var d) ? d : null;

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>The event's own <c>ts</c> when it has one (seconds since the epoch, as the wire
    /// sends it), otherwise the local clock. Using the event's timestamp keeps a turn that was
    /// persisted long after it ran — a background turn, a delayed retry — in the right day bucket.</summary>
    private static long TimestampMs(JsonElement root)
    {
        if (Num(root, "ts") is { } ts && ts > 0)
        {
            // Tolerate either seconds (what the protocol documents) or milliseconds, since one
            // misconfigured agent sending ms would otherwise land every row in the year 56000.
            return ts > 100_000_000_000 ? (long)ts : (long)(ts * 1000);
        }

        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
