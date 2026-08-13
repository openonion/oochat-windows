using System.Text.Json;
using ConnectOnion.WinUIClient.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ConnectOnion.WinUIClient.Data;

/// <summary>
/// <see cref="ConversationRepository"/>: the row&lt;-&gt;object plumbing — the reusable SQL command
/// shapes, the parameter binders, and the reader mapping.
///
/// Separated from the repository's public API because it answers a different question: the main
/// file says <i>what</i> a conversation read or write does and in what order, this one says
/// <i>how</i> a <see cref="ChatMessage"/> becomes rows. The commands are built once per batch and
/// re-bound per row (see <see cref="ConversationRepository.UpsertMessagesAsync"/>), which is why
/// they are factories rather than inline SQL.
/// </summary>
public sealed partial class ConversationRepository
{
    private static readonly Action<ILogger, long, Exception?> LogToolActivityPayloadUnreadable =
        LoggerMessage.Define<long>(LogLevel.Warning, new EventId(2, "ToolActivityPayloadUnreadable"),
            "Tool activity payload for message {MessageId} could not be restored; showing a plain activity card");

    private static readonly Action<ILogger, Exception?> LogThoughtStepsPayloadUnreadable =
        LoggerMessage.Define(LogLevel.Warning, new EventId(3, "ThoughtStepsPayloadUnreadable"),
            "Grouped thought steps payload could not be restored; falling back to the single stored step");

    // Instance rather than static so these can reach _logger. A payload that will not deserialize
    // is degraded rather than rethrown, and a degradation nobody can see afterwards is the kind of
    // thing that gets reported as "the app lost my history" with nothing to go on.
    private ChatMessage RowToMessage(SqliteDataReader reader)
    {
        var m = new ChatMessage
        {
            Id = reader.GetInt64(0),
            Role = ParseRole(reader.GetString(1)),
            Content = ReadNullableString(reader, 2) ?? "",
            AgentName = ReadNullableString(reader, 3),
            EventKind = ReadNullableString(reader, 4) ?? "",
            EventKey = ReadNullableString(reader, 5),
            EventEyebrow = ReadNullableString(reader, 6) ?? "",
            EventTitle = ReadNullableString(reader, 7) ?? "",
            EventDetail = ReadNullableString(reader, 8),
            EventMeta = ReadNullableString(reader, 9),
            EventArgs = ReadNullableString(reader, 10),
            EventResult = ReadNullableString(reader, 11),
            Status = ParseEventStatus(ReadNullableString(reader, 12)),
            IsOnboarding = reader.GetInt32(13) != 0,
            CreatedAtUnixMs = reader.GetInt64(14),
        };
        // Diff disclosure is presentation-only and is deliberately not persisted. Rows loaded
        // from SQLite belong to a completed/historical transcript, so keep them compact even
        // when their durable outcome is Failed, Pending, or another state that expands while a
        // live runtime is still handling it.
        if (m.IsDiffPreviewEvent) m.IsDiffExpanded = false;
        // Interactive disclosure is also presentation-only. A freshly received card remains
        // expanded so the user can act on it immediately, while a row restored into history
        // starts compact and can still be reopened from its header. Applies to every interactive
        // kind, not only plan_review — an answered ask_user restored from SQLite is history in
        // exactly the same sense.
        if (m.IsInteractiveEvent) m.IsInteractiveCardExpanded = false;

        // Tool activity is stored in the existing event_args payload column. This keeps the
        // schema backward compatible while making the aggregate durable across page teardown
        // and application restart. Legacy per-tool rows still load normally and are upgraded
        // by the chat projection when they are next replayed/saved.
        if (m.EventKind == "tool_activity" && !string.IsNullOrWhiteSpace(m.EventArgs))
        {
            try
            {
                m.ToolActivity = JsonSerializer.Deserialize(
                    m.EventArgs,
                    ConversationJsonContext.Default.ToolActivityViewModel);
            }
            catch (Exception ex)
            {
                // Degrade to a plain activity card rather than dropping the bubble or
                // rethrowing: the turn genuinely happened, and one unreadable payload must not
                // cost the user the surrounding conversation. The visible title says so
                // instead of silently rendering an empty card.
                LogToolActivityPayloadUnreadable(_logger, m.Id, ex);
                m.EventKind = "activity";
                m.EventTitle = "Tool execution history could not be restored";
            }
        }
        // A grouped thought card stores its steps in the same payload column, for the same reason.
        // Rows written before grouping existed (and single-step cards) carry no payload, so the
        // one thought in event_detail is the whole card — seed from that instead.
        if (m.IsThinkingEvent)
        {
            var steps = TryReadThoughtSteps(m.EventArgs);
            if (steps is not null)
            {
                foreach (var step in steps) m.ThoughtSteps.Add(step);
            }
            else if (!string.IsNullOrEmpty(m.EventDetail))
            {
                m.ThoughtSteps.Add(m.EventDetail);
            }
        }
        return m;
    }

    private IReadOnlyList<string>? TryReadThoughtSteps(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            return JsonSerializer.Deserialize(
                payload,
                ConversationJsonContext.Default.ListString);
        }
        catch (Exception ex)
        {
            // Same bargain as the tool-activity payload above: one unreadable blob must not cost
            // the user the conversation. The caller falls back to the event_detail single step.
            LogThoughtStepsPayloadUnreadable(_logger, ex);
            return null;
        }
    }

    // The write commands below are built once per transaction and rebound per row. Microsoft.Data
    // .Sqlite caches the compiled statement on the command object, so reusing one command means the
    // statement is parsed once rather than once per bubble — which is what made saving a long
    // conversation scale badly, far more than the row writes themselves.

    private static SqliteCommand CreateMessageUpsert(SqliteConnection connection, SqliteTransaction transaction)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO messages
              (id, conversation_id, role, content, agent_name,
               event_kind, event_key, event_eyebrow, event_title,
               event_detail, event_meta, event_args, event_result,
               event_status, is_onboarding, created_at)
            VALUES
              ($id, $conversation_id, $role, $content, $agent_name,
               $event_kind, $event_key, $event_eyebrow, $event_title,
               $event_detail, $event_meta, $event_args, $event_result,
               $event_status, $is_onboarding, $created_at)
            ON CONFLICT(conversation_id, id) DO UPDATE SET
               role = excluded.role,
               content = excluded.content,
               agent_name = excluded.agent_name,
               event_kind = excluded.event_kind,
               event_key = excluded.event_key,
               event_eyebrow = excluded.event_eyebrow,
               event_title = excluded.event_title,
               event_detail = excluded.event_detail,
               event_meta = excluded.event_meta,
               event_args = excluded.event_args,
               event_result = excluded.event_result,
               event_status = excluded.event_status,
               is_onboarding = excluded.is_onboarding;
            """;
        // Note what the UPDATE arm deliberately omits: created_at. A row keeps the timestamp
        // it was first written with, so re-persisting a bubble mid-turn (an agent card that
        // gains an image, a status that flips to done) does not move it in time.
        cmd.Parameters.Add("$id", SqliteType.Integer);
        cmd.Parameters.Add("$conversation_id", SqliteType.Text);
        cmd.Parameters.Add("$role", SqliteType.Text);
        cmd.Parameters.Add("$content", SqliteType.Text);
        cmd.Parameters.Add("$agent_name", SqliteType.Text);
        cmd.Parameters.Add("$event_kind", SqliteType.Text);
        cmd.Parameters.Add("$event_key", SqliteType.Text);
        cmd.Parameters.Add("$event_eyebrow", SqliteType.Text);
        cmd.Parameters.Add("$event_title", SqliteType.Text);
        cmd.Parameters.Add("$event_detail", SqliteType.Text);
        cmd.Parameters.Add("$event_meta", SqliteType.Text);
        cmd.Parameters.Add("$event_args", SqliteType.Text);
        cmd.Parameters.Add("$event_result", SqliteType.Text);
        cmd.Parameters.Add("$event_status", SqliteType.Text);
        cmd.Parameters.Add("$is_onboarding", SqliteType.Integer);
        cmd.Parameters.Add("$created_at", SqliteType.Integer);
        return cmd;
    }

    private static SqliteCommand CreateAttachmentDelete(SqliteConnection connection, SqliteTransaction transaction)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        // (conversation_id, message_id) is a prefix of the table's primary key, so this is a seek.
        cmd.CommandText = """
            DELETE FROM message_attachments
            WHERE conversation_id = $conversation_id AND message_id = $message_id;
            """;
        cmd.Parameters.Add("$conversation_id", SqliteType.Text);
        cmd.Parameters.Add("$message_id", SqliteType.Integer);
        return cmd;
    }

    private static SqliteCommand CreateAttachmentInsert(SqliteConnection connection, SqliteTransaction transaction)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO message_attachments
              (id, conversation_id, message_id, kind, file_name, mime_type,
               size_bytes, local_cache_path, remote_uri, status, created_at)
            VALUES
              ($id, $conversation_id, $message_id, $kind, $file_name, $mime_type,
               $size_bytes, $local_cache_path, $remote_uri, $status, $created_at);
            """;
        cmd.Parameters.Add("$id", SqliteType.Text);
        cmd.Parameters.Add("$conversation_id", SqliteType.Text);
        cmd.Parameters.Add("$message_id", SqliteType.Integer);
        cmd.Parameters.Add("$kind", SqliteType.Text);
        cmd.Parameters.Add("$file_name", SqliteType.Text);
        cmd.Parameters.Add("$mime_type", SqliteType.Text);
        cmd.Parameters.Add("$size_bytes", SqliteType.Integer);
        cmd.Parameters.Add("$local_cache_path", SqliteType.Text);
        cmd.Parameters.Add("$remote_uri", SqliteType.Text);
        cmd.Parameters.Add("$status", SqliteType.Text);
        cmd.Parameters.Add("$created_at", SqliteType.Integer);
        return cmd;
    }

    private static void BindMessage(SqliteCommand cmd, ChatMessage m, string conversationId, long now)
    {
        // A tool-activity bubble's aggregate is re-serialized on every write, so event_args
        // reflects the live ToolActivity object rather than whatever string the row was loaded
        // with. This is the counterpart to the deserialize in RowToMessage — the column does
        // double duty as a generic payload and as this specific aggregate's storage.
        var eventArgs = m.IsToolActivityEvent
            ? JsonSerializer.Serialize(
                m.ToolActivity,
                ConversationJsonContext.Default.ToolActivityViewModel)
            : m.IsThinkingEvent && m.ThoughtSteps.Count > 0
                ? JsonSerializer.Serialize(
                    m.ThoughtSteps.ToList(),
                    ConversationJsonContext.Default.ListString)
                : m.EventArgs;

        // Empty strings are stored as NULL throughout. ChatMessage seeds its string properties
        // to "" (a partial property can't carry an initializer — see the MVVM notes in
        // CLAUDE.md), so without this normalization every unused event column would hold ""
        // instead of NULL, and RowToMessage's `?? ""` reads would be the only thing hiding it.
        cmd.Parameters["$id"].Value = m.Id;
        cmd.Parameters["$conversation_id"].Value = conversationId;
        // Role is persisted lowercase; ParseRole is the matching reader. Storing the enum's
        // name rather than its numeric value keeps the table readable in a SQLite browser and
        // survives reordering the enum.
        cmd.Parameters["$role"].Value = m.Role.ToString().ToLowerInvariant();
        cmd.Parameters["$content"].Value = string.IsNullOrEmpty(m.Content) ? DBNull.Value : m.Content;
        cmd.Parameters["$agent_name"].Value = m.AgentName ?? (object)DBNull.Value;
        cmd.Parameters["$event_kind"].Value = string.IsNullOrEmpty(m.EventKind) ? DBNull.Value : m.EventKind;
        cmd.Parameters["$event_key"].Value = m.EventKey ?? (object)DBNull.Value;
        cmd.Parameters["$event_eyebrow"].Value = string.IsNullOrEmpty(m.EventEyebrow) ? DBNull.Value : m.EventEyebrow;
        cmd.Parameters["$event_title"].Value = string.IsNullOrEmpty(m.EventTitle) ? DBNull.Value : m.EventTitle;
        cmd.Parameters["$event_detail"].Value = m.EventDetail ?? (object)DBNull.Value;
        cmd.Parameters["$event_meta"].Value = m.EventMeta ?? (object)DBNull.Value;
        cmd.Parameters["$event_args"].Value = eventArgs ?? (object)DBNull.Value;
        cmd.Parameters["$event_result"].Value = m.EventResult ?? (object)DBNull.Value;
        cmd.Parameters["$event_status"].Value = m.Status switch
        {
            EventStatus.Running => "running",
            EventStatus.Error => "error",
            _ => "done",
        };
        cmd.Parameters["$is_onboarding"].Value = m.IsOnboarding ? 1 : 0;
        // Preserve an existing timestamp; only stamp `now` on a bubble that has never had one.
        // Combined with the UPDATE arm not touching created_at, a message's time is fixed at
        // first write from either direction.
        cmd.Parameters["$created_at"].Value = m.CreatedAtUnixMs > 0 ? m.CreatedAtUnixMs : now;
    }

    private static void BindAttachment(SqliteCommand cmd, ChatAttachment a, string conversationId, long messageId, long now)
    {
        cmd.Parameters["$id"].Value = a.Id;
        cmd.Parameters["$conversation_id"].Value = conversationId;
        cmd.Parameters["$message_id"].Value = messageId;
        cmd.Parameters["$kind"].Value = a.Kind == AttachmentKind.Image ? "image" : "file";
        cmd.Parameters["$file_name"].Value = a.FileName;
        cmd.Parameters["$mime_type"].Value = a.MimeType ?? (object)DBNull.Value;
        cmd.Parameters["$size_bytes"].Value = a.SizeBytes;
        cmd.Parameters["$local_cache_path"].Value = a.LocalCachePath ?? (object)DBNull.Value;
        cmd.Parameters["$remote_uri"].Value = a.RemoteUri ?? (object)DBNull.Value;
        cmd.Parameters["$status"].Value = a.Status == AttachmentStatus.Failed ? "failed" : "sent";
        cmd.Parameters["$created_at"].Value = now;
    }

    private static ChatRole ParseRole(string value)
        => value switch
        {
            "agent" => ChatRole.Agent,
            "event" => ChatRole.Event,
            _ => ChatRole.User,
        };

    private static EventStatus ParseEventStatus(string? value)
        => value switch
        {
            "running" => EventStatus.Running,
            "error" => EventStatus.Error,
            _ => EventStatus.Done,
        };

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
