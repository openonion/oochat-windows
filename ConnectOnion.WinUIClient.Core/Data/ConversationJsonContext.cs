using System.Text.Json.Serialization;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Data;

/// <summary>
/// Trim-safe metadata for the two structured payloads stored in <c>messages.event_args</c>.
/// Release publishing trims the desktop app, so the repository must not rely on reflection to
/// discover model members that the linker cannot see.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ToolActivityViewModel))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class ConversationJsonContext : JsonSerializerContext;
