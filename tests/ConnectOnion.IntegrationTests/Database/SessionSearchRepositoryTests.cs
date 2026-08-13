using ConnectOnion.WinUIClient.Data;

namespace ConnectOnion.IntegrationTests.Database;

[Collection(DatabaseCollection.Name)]
public sealed class SessionSearchRepositoryTests
{
    private readonly SessionRepository _sessions = new();

    [Fact]
    public async Task SearchByTitleOrAgentAsync_SearchesInSqlAndHonoursTheLimit()
    {
        await SeedAsync();

        var byAgent = await _sessions.SearchByTitleOrAgentAsync("Alpha Helper", 1);
        var byTitle = await _sessions.SearchByTitleOrAgentAsync("Alpha in the title", 10);

        Assert.Single(byAgent);
        Assert.Equal("one", byAgent[0].Id);
        Assert.Equal("three", Assert.Single(byTitle).Id);
    }

    [Fact]
    public async Task LoadSessionsByIdsAsync_LoadsTheSetInOneBoundedRead()
    {
        await SeedAsync();
        var rows = await _sessions.LoadSessionsByIdsAsync(["one", "three", "missing"]);

        Assert.Equal(new[] { "one", "three" }, rows.Select(row => row.Id));
    }

    private static async Task SeedAsync()
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM message_attachments;
            DELETE FROM messages;
            DELETE FROM trace_events;
            DELETE FROM executions;
            DELETE FROM sessions;
            DELETE FROM agents;
            INSERT INTO agents (id, name, address) VALUES
                ('agent-alpha', 'Alpha Helper', '0x1'),
                ('agent-beta', 'Beta Helper', '0x2');
            INSERT INTO sessions (id, agent_id, title, created_at, updated_at) VALUES
                ('one', 'agent-alpha', 'First project', '1', '3'),
                ('two', 'agent-alpha', 'Second project', '1', '2'),
                ('three', 'agent-beta', 'Alpha in the title', '1', '1');
            """;
        await command.ExecuteNonQueryAsync();
    }
}
