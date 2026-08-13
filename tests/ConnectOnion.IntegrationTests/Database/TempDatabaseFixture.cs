using ConnectOnion.WinUIClient.Data;

namespace ConnectOnion.IntegrationTests.Database;

public sealed class TempDatabaseFixture : IAsyncLifetime
{
    public string RootDirectory { get; } = Path.Combine(
        Path.GetTempPath(), "ConnectOnion.Tests", Guid.NewGuid().ToString("N"));

    public string DatabasePath => Path.Combine(RootDirectory, "connectonion.db");

    public ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable(AppStorage.DataRootEnvironmentVariable, RootDirectory);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(AppStorage.DataRootEnvironmentVariable, null);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(RootDirectory))
            Directory.Delete(RootDirectory, recursive: true);
        return ValueTask.CompletedTask;
    }

    public async Task CreateSessionAsync(string sessionId = "conversation-1")
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO agents (id, name, address) VALUES ('agent-1', 'Agent', '0x1');
            INSERT OR IGNORE INTO sessions (id, agent_id, title, created_at, updated_at)
            VALUES ($id, 'agent-1', 'Test conversation', '2026-01-01', '2026-01-01');
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync();
    }
}
