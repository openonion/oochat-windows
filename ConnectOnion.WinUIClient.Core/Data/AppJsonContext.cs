using System.Text.Json.Serialization;
using ConnectOnion.WinUIClient.Models.Notifications;

namespace ConnectOnion.WinUIClient.Data;

/// <summary>
/// Source-generated JSON contracts for the blobs this app stores in SQLite (notification
/// settings, the shortcut-override map, string lists in <c>preferences</c>).
///
/// Deliberately scoped to *local persistence*. Outgoing protocol frames go the other way —
/// plain <c>Dictionary&lt;string, object?&gt;</c> through <c>WireJson</c>, no
/// source-gen context and no DTO types (see <c>InputMessageBuilder</c>) — because the wire
/// shape is defined by the host, not by our type system. Adding a wire message here would
/// split that convention across two mechanisms; add the <c>[JsonSerializable]</c> entry only
/// for something we read back out of our own database.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(NotificationSettings))]
[JsonSerializable(typeof(LegacyConversationEnvelope))]
public sealed partial class AppJsonContext : JsonSerializerContext;
