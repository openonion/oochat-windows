using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectOnion.Protocol.Runtime;

/// <summary>
/// Everything the registry needs to start a turn. Stable ids are generated once, up
/// front, so the optimistic UI bubble, the background run, and the persisted rows all
/// agree — this is what makes completion idempotent and de-duplication possible.
/// </summary>
public sealed record TurnRequest(
    string RunId,
    string ConversationId,
    string AgentId,
    string UserMessageId,
    string AssistantMessageId,
    string Prompt,
    IReadOnlyList<string>? Images = null,
    IReadOnlyList<OutgoingFileAttachment>? Files = null,
    string? SessionId = null,
    /// <summary>The approval mode this turn should run under (see <see cref="AgentModes"/>).
    /// Re-sent with every turn because the host's session merge discards a mode we only put in
    /// CONNECT, and asserting an unchanged mode is a no-op host-side.</summary>
    string? Mode = null,
    IReadOnlyList<TurnAttachmentSource>? AttachmentSources = null);

/// <summary>
/// Retry-safe reference to a local attachment. The runtime keeps this descriptor instead of
/// pinning its Base64 wire representation; the app executor encodes it immediately before send.
/// </summary>
public sealed record TurnAttachmentSource(
    string Name,
    string LocalPath,
    string MimeType,
    bool IsImage);

/// <summary>
/// The channel an <see cref="ITurnExecutor"/> uses to report progress back into the
/// run. Every call is thread-safe and triggers a snapshot publish. Callers accumulate
/// their own partial text and pass the whole thing to <see cref="UpdatePartial"/> so
/// the run never double-appends a fragment (see the streaming-dedup requirement).
/// </summary>
public interface IRunSink
{
    /// <summary>The run's cooperative cancellation token (user stop / app shutdown).</summary>
    CancellationToken CancellationToken { get; }

    /// <summary>Transport is opening/authenticating.</summary>
    void SetConnecting();

    /// <summary>The agent started producing the reply.</summary>
    void SetRunning();

    /// <summary>Buffer a raw stream event and publish. Ignored after the run is terminal.</summary>
    void Publish(AgentStreamEvent streamEvent);

    /// <summary>Replace the accumulated partial reply text and publish.</summary>
    void UpdatePartial(string partialContent);
}

/// <summary>
/// Drives one turn against the transport. Production wraps the WebSocket connection;
/// tests supply a fake that scripts events onto the sink. Returns the final reply text.
/// Must observe <see cref="IRunSink.CancellationToken"/> and surface failures by throwing.
/// </summary>
public interface ITurnExecutor
{
    Task<string> ExecuteAsync(TurnRequest request, IRunSink sink);
}

/// <summary>
/// Durable persistence for a finished turn. <see cref="PersistCompletedAsync"/> is
/// awaited to success <b>before</b> the run is marked Completed, so a subscriber that
/// reacts to completion by reloading the database always finds the reply. Implementations
/// must be idempotent (keyed on <see cref="ConversationRunSnapshot.AssistantMessageId"/>)
/// because a completion callback can legitimately fire more than once.
/// </summary>
public interface IRunPersistence
{
    Task PersistCompletedAsync(ConversationRunSnapshot snapshot, string finalReply);

    Task PersistFailedAsync(ConversationRunSnapshot snapshot);
}

/// <summary>Structured, side-effect-free logging of run state transitions.</summary>
public interface IRunLogger
{
    void Log(ConversationRunSnapshot snapshot, string phase, TimeSpan elapsed);
}

/// <summary>A logger that drops everything — the default when the host wires none.</summary>
public sealed class NullRunLogger : IRunLogger
{
    public static readonly NullRunLogger Instance = new();
    public void Log(ConversationRunSnapshot snapshot, string phase, TimeSpan elapsed) { }
}
