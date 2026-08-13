namespace ConnectOnion.WinUIClient.Data;

// ConversationCache retains this namespace import in production because it is part
// of the application's data-facing service layer. The headless test project only
// compiles the cache's model dependencies, so this marker keeps that import valid.
internal static class DataNamespaceMarker;
