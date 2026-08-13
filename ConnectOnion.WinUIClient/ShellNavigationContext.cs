namespace ConnectOnion.WinUIClient;

/// <summary>
/// The storage-backed identity of a Frame destination, plus a one-shot payload used while first
/// opening it. Frame otherwise remembers only the page type, which cannot distinguish one chat or
/// agent from another when Back/Forward navigation restores a page.
/// </summary>
internal sealed record ShellNavigationContext(
    string? AgentId,
    string? ConversationId,
    object? Payload);
