using System.Text.Json.Serialization;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Data;

/// <summary>
/// The conversation-blob shape shipped before row-level <c>messages</c> storage. These DTOs exist
/// only so an old database can be upgraded; new writes continue through
/// <see cref="ConversationRepository"/>.
/// </summary>
public sealed class LegacyConversationEnvelope
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("state")]
    public LegacyConversationState State { get; set; } = new();
}

public sealed class LegacyConversationState
{
    [JsonPropertyName("messages")]
    public List<LegacyConversationMessage> Messages { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public long UpdatedAt { get; set; }
}

public sealed class LegacyConversationMessage
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("role")]
    public ChatRole Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("agentName")]
    public string? AgentName { get; set; }

    [JsonPropertyName("eventKind")]
    public string? EventKind { get; set; }

    [JsonPropertyName("eventEyebrow")]
    public string? EventEyebrow { get; set; }

    [JsonPropertyName("eventTitle")]
    public string? EventTitle { get; set; }

    [JsonPropertyName("eventDetail")]
    public string? EventDetail { get; set; }

    [JsonPropertyName("eventMeta")]
    public string? EventMeta { get; set; }

    [JsonPropertyName("eventArgs")]
    public string? EventArgs { get; set; }

    [JsonPropertyName("eventResult")]
    public string? EventResult { get; set; }

    [JsonPropertyName("eventStatus")]
    public EventStatus Status { get; set; }

    [JsonPropertyName("isOnboarding")]
    public bool IsOnboarding { get; set; }
}
