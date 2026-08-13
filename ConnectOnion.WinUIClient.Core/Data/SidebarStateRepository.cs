using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ConnectOnion.WinUIClient.Data;

/// <summary>Persisted disclosure state for the shell's agent tree and pinned-chat section.</summary>
public sealed record SidebarState(
    IReadOnlySet<string> ExpandedAgentIds,
    bool IsPinnedExpanded,
    bool HasAgentExpansionState);

/// <summary>
/// Stores small shell UI state in <c>app_meta</c>, alongside the active and pinned session ids.
/// An absent agent key means "first run" while a stored empty JSON list means the user explicitly
/// collapsed every agent, so those states must remain distinguishable.
/// </summary>
public sealed class SidebarStateRepository : IDisposable
{
    private const string ExpandedAgentIdsKey = "sidebar_expanded_agent_ids";
    private const string PinnedExpandedKey = "sidebar_pinned_expanded";
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private int _saveVersion;

    public async Task<SidebarState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        var expandedJson = await AppDatabase.GetMetaAsync(connection, ExpandedAgentIdsKey).ConfigureAwait(false);
        var pinnedValue = await AppDatabase.GetMetaAsync(connection, PinnedExpandedKey).ConfigureAwait(false);

        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var hasAgentState = expandedJson is not null;
        if (!string.IsNullOrWhiteSpace(expandedJson))
        {
            try
            {
                var ids = JsonSerializer.Deserialize(expandedJson, AppJsonContext.Default.ListString) ?? new();
                expanded.UnionWith(ids.Where(id => !string.IsNullOrWhiteSpace(id)));
            }
            catch
            {
                // Corrupt cosmetic state falls back to the normal first-run expansion.
                hasAgentState = false;
            }
        }

        var pinnedExpanded = pinnedValue is null
            || !string.Equals(pinnedValue, "0", StringComparison.Ordinal);
        return new SidebarState(expanded, pinnedExpanded, hasAgentState);
    }

    public async Task SaveAsync(
        IEnumerable<string> expandedAgentIds,
        bool isPinnedExpanded,
        CancellationToken cancellationToken = default)
    {
        var ids = expandedAgentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var version = Interlocked.Increment(ref _saveVersion);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A newer click arrived while this write waited for SQLite. Skip the obsolete
            // snapshot so the final row always describes the latest visible disclosure state.
            if (version != Volatile.Read(ref _saveVersion)) return;

            await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await AppDatabase.SetMetaAsync(
                    connection,
                    transaction,
                    ExpandedAgentIdsKey,
                    JsonSerializer.Serialize(ids, AppJsonContext.Default.ListString))
                .ConfigureAwait(false);
            await AppDatabase.SetMetaAsync(
                    connection,
                    transaction,
                    PinnedExpandedKey,
                    isPinnedExpanded ? "1" : "0")
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void Dispose() => _saveGate.Dispose();
}
