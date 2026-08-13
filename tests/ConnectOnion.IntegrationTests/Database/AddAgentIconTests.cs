using System.Net;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.ViewModels;

namespace ConnectOnion.IntegrationTests.Database;

/// <summary>
/// Covers the icon half of adding an agent. The interesting property is that the chosen icon and
/// the agent it belongs to reach SQLite in the <i>same</i> save: the icon's filename is derived
/// from an id that does not exist until the agent is built, and a follow-up write would race any
/// other agent-list save (the repository reconciles the whole list, so a concurrent one would drop
/// the icon silently).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AddAgentIconTests
{
    [Fact]
    public async Task AddAsync_WithCommitHook_StoresTheIconAlongsideTheNewAgent()
    {
        var viewModel = await CreateConnectedViewModelAsync("icon-commit");
        string? observedAgentId = null;

        var agent = await viewModel.AddAsync(
            CancellationToken.None,
            (agentId, _) =>
            {
                observedAgentId = agentId;
                return Task.FromResult<string?>("avatars/committed.png");
            });

        Assert.NotNull(agent);
        // The hook is handed the real id, which is what makes the committed filename traceable
        // back to its agent.
        Assert.Equal(agent!.Id, observedAgentId);
        Assert.Equal("avatars/committed.png", agent.IconPath);

        var stored = Assert.Single((await new AgentRepository().LoadAsync()).Agents);
        Assert.Equal("avatars/committed.png", stored.IconPath);
    }

    [Fact]
    public async Task AddAsync_WhenTheIconCannotBeCommitted_StillCreatesTheAgent()
    {
        var viewModel = await CreateConnectedViewModelAsync("icon-failed");

        // A null return is how the hook reports "the image failed". An icon is decoration and must
        // never cost the user the agent they were adding.
        var agent = await viewModel.AddAsync(CancellationToken.None, (_, _) => Task.FromResult<string?>(null));

        Assert.NotNull(agent);
        Assert.Null(agent!.IconPath);
        Assert.Null(Assert.Single((await new AgentRepository().LoadAsync()).Agents).IconPath);
    }

    [Fact]
    public async Task AddAsync_WithoutACommitHook_LeavesTheAgentWithNoIcon()
    {
        var viewModel = await CreateConnectedViewModelAsync("icon-none");

        var agent = await viewModel.AddAsync(CancellationToken.None);

        Assert.NotNull(agent);
        Assert.Null(agent!.IconPath);
    }

    /// <summary>
    /// Drives the view model to the one state in which <c>CanAdd</c> is true.
    ///
    /// The tests in this collection share one database, and <c>TestConnectionAsync</c> refuses an
    /// endpoint an existing agent already uses — so the list is cleared first and each caller
    /// brings its own host, rather than the second test to run silently never reaching Connected.
    /// </summary>
    private static async Task<AddAgentViewModel> CreateConnectedViewModelAsync(string host)
    {
        var repository = new AgentRepository();
        await repository.SaveAsync(new AgentsState());

        var viewModel = new AddAgentViewModel(
            repository,
            new ConnectionTester(new HttpClient(new HealthyAgentHandler())))
        {
            Input = $"https://{host}.example.test/agent",
        };

        await viewModel.TestConnectionAsync(CancellationToken.None);
        Assert.True(viewModel.CanAdd);
        return viewModel;
    }

    private sealed class HealthyAgentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"healthy":true,"name":"icon-agent"}"""),
            });
    }
}
