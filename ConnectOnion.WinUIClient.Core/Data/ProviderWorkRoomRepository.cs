using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Models;
using Microsoft.Data.Sqlite;

namespace ConnectOnion.WinUIClient.Data;

// stores the provider workroom states. provider events are correlated to specific rooms
public sealed class ProviderWorkRoomRepository
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _roomGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string ConversationId, string Id), string> _roomByCorrelation = new();

    public event Action<ProviderWorkRoom>? WorkRoomUpdated;

    // loads a workroom's json snapshot and restores its transcript order then returns it
    public async Task<ProviderWorkRoom?> GetAsync(string workRoomId, CancellationToken ct = default)
    {
        await using var connection = await AppDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT state_json FROM provider_workrooms WHERE workroom_id = $id;";
        command.Parameters.AddWithValue("$id", workRoomId);

        var json = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (json is null) return null;
        try
        {
            var room = JsonSerializer.Deserialize(json, AppJsonContext.Default.ProviderWorkRoom);
            room?.NormalizeMessageOrder();
            return room;
        }
        catch (JsonException) { return null; }
    }

    // finds the matching workroom, applies the provider event, saves and notifies the page
    public async Task<bool> ApplyEventAsync(string parentConversationId, AgentStreamEvent streamEvent, CancellationToken ct = default)
    {
        if (!ProviderWorkRoom.IsWorkRoomEvent(streamEvent.Type)) return false;

        JsonDocument document;
        try { document = JsonDocument.Parse(streamEvent.RawJson); }
        catch (JsonException) { return false; }

        using (document)
        {
            var root = document.RootElement;
            var explicitId = ProviderWorkRoom.GetExplicitWorkRoomId(root);
            var workRoomId = explicitId;

            if (workRoomId is not null)
            {
                RememberCorrelation(parentConversationId, ReadString(root, "invocationId"), workRoomId);
                RememberCorrelation(parentConversationId, ReadString(root, "parentToolCallId"), workRoomId);
            }
            else if (streamEvent.Type == "provider_message")
                workRoomId = await FindWorkRoomIdAsync(parentConversationId, root, ct).ConfigureAwait(false);

            if (workRoomId is null) return false;

            var gate = _roomGates.GetOrAdd(workRoomId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var room = await GetAsync(workRoomId, ct).ConfigureAwait(false)
                    ?? new ProviderWorkRoom { WorkRoomId = workRoomId, ParentConversationId = parentConversationId };

                if (!string.Equals(room.ParentConversationId, parentConversationId, StringComparison.Ordinal))
                    return false;

                if (!room.Apply(streamEvent.Type, root, workRoomId)) return false;

                room.ParentConversationId = parentConversationId;
                await SaveAsync(room, ct).ConfigureAwait(false);
                Notify(room);
                return true;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    // remembers provider invocation and tool call ids
    private void RememberCorrelation(string conversationId, string? id, string workRoomId)
    {
        if (!string.IsNullOrWhiteSpace(id)) _roomByCorrelation[(conversationId, id)] = workRoomId;
    }

    // finds a messages provider invocation or tool call id in memory, then checks the saved workroom snapshots
    private async Task<string?> FindWorkRoomIdAsync(string conversationId, JsonElement message, CancellationToken ct)
    {
        var invocationId = ReadString(message, "invocationId");
        var parentToolCallId = ReadString(message, "parentToolCallId");
        foreach (var id in new[] { invocationId, parentToolCallId })
            if (id is not null && _roomByCorrelation.TryGetValue((conversationId, id), out var workRoomId))
                return workRoomId;

        if (invocationId is null && parentToolCallId is null) return null;

        await using var connection = await AppDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT workroom_id, state_json FROM provider_workrooms WHERE conversation_id = $conversation;";
        command.Parameters.AddWithValue("$conversation", conversationId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        string? parentMatch = null;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            ProviderWorkRoom? room;

            try { room = JsonSerializer.Deserialize(reader.GetString(1), AppJsonContext.Default.ProviderWorkRoom); }
            catch (JsonException) { continue; }

            if (room is null) continue;
            if (room.CurrentInvocationId == invocationId) return id;
            if (parentToolCallId is not null && room.ParentToolCallId == parentToolCallId) parentMatch = id;
        }
        return parentMatch;
    }

    // reads a payload iff its a JSON string
    private static string? ReadString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;

        return value.GetString();
    }

    // allows persistence of previous permission level if the permission change fails
    public async Task SetPermissionChangeUnavailableAsync(
        string workRoomId, bool unavailable, CancellationToken ct = default)
    {
        var gate = _roomGates.GetOrAdd(workRoomId, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var room = await GetAsync(workRoomId, ct).ConfigureAwait(false);
            if (room is null || room.PermissionChangeUnavailable == unavailable) return;

            room.PermissionChangeUnavailable = unavailable;

            await SaveAsync(room, ct).ConfigureAwait(false);
            Notify(room);
        }
        finally
        {
            gate.Release();
        }
    }

    // serialises the current workroom state and adds it with its conversation and update timestamp
    private async Task SaveAsync(ProviderWorkRoom room, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(room, AppJsonContext.Default.ProviderWorkRoom);

        await using var connection = await AppDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO provider_workrooms(workroom_id, conversation_id, updated_at, state_json)
            VALUES ($id, $conversation, $updated, $json)
            ON CONFLICT(workroom_id) DO UPDATE SET
                conversation_id = excluded.conversation_id,
                updated_at = excluded.updated_at,
                state_json = excluded.state_json;
            """;
        command.Parameters.AddWithValue("$id", room.WorkRoomId);
        command.Parameters.AddWithValue("$conversation", room.ParentConversationId);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$json", json);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private void Notify(ProviderWorkRoom room)
    {
        WorkRoomUpdated?.Invoke(room.Clone());
    }
}
