using System.Net;
using System.Net.Http;
using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class ConnectionTesterTests
{
    [Theory]
    [InlineData("ws://agent.test/base", "http://agent.test/base/health")]
    [InlineData("wss://agent.test/base", "https://agent.test/base/health")]
    public async Task DirectUrl_UsesHealthEndpoint(string directUrl, string expectedUrl)
    {
        var handler = new StubHandler("""{"status":"ok","name":"test-agent"}""");
        var tester = new ConnectionTester(new HttpClient(handler));

        var result = await tester.TestAsync(null, directUrl);

        Assert.True(result.Ok);
        Assert.Equal("test-agent", result.AgentName);
        Assert.Contains("Test Agent", result.Detail);
        Assert.Equal(expectedUrl, handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task ExplicitUnhealthyResponse_FailsWithSpecificReason()
    {
        var tester = new ConnectionTester(new HttpClient(
            new StubHandler("""{"healthy":false}""")));

        var result = await tester.TestAsync(null, "wss://agent.test");

        Assert.False(result.Ok);
        Assert.Contains("unhealthy", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("ftp://agent.test")]
    [InlineData("file:///C:/agent")]
    public async Task InvalidDirectUrl_IsRejectedWithoutSendingARequest(string directUrl)
    {
        var handler = new StubHandler("unused");
        var tester = new ConnectionTester(new HttpClient(handler));

        var result = await tester.TestAsync("validAddress", directUrl);

        Assert.False(result.Ok);
        Assert.Equal("Invalid Direct URL.", result.Detail);
        Assert.Null(handler.LastRequestUri);
    }

    [Fact]
    public async Task DirectUrl_DropsQueryAndFragmentWhenBuildingHealthEndpoint()
    {
        var handler = new StubHandler("ok");
        var tester = new ConnectionTester(new HttpClient(handler));

        var result = await tester.TestAsync(null, "https://agent.test/base/?token=secret#section");

        Assert.True(result.Ok);
        Assert.Equal("https://agent.test/base/health", handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task DirectUrl_NonSuccessStatusIncludesTheStatusCode()
    {
        var tester = new ConnectionTester(new HttpClient(
            new StubHandler("down", HttpStatusCode.ServiceUnavailable)));

        var result = await tester.TestAsync(null, "https://agent.test");

        Assert.False(result.Ok);
        Assert.Contains("503", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("UNHEALTHY")]
    [InlineData(" error ")]
    [InlineData("failed")]
    [InlineData("fail")]
    public async Task DirectUrl_UnhealthyStatusSpellingsAreRejected(string status)
    {
        var tester = new ConnectionTester(new HttpClient(
            new StubHandler($"{{\"status\":\"{status}\"}}")));

        var result = await tester.TestAsync(null, "https://agent.test");

        Assert.False(result.Ok);
        Assert.Contains("unhealthy", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("")]
    [InlineData("{not-json")]
    public async Task DirectUrl_LegacyNonJsonHealthBodyIsAccepted(string body)
    {
        var tester = new ConnectionTester(new HttpClient(new StubHandler(body)));

        var result = await tester.TestAsync(null, "https://agent.test");

        Assert.True(result.Ok);
        Assert.Null(result.AgentName);
        Assert.Equal("Healthy via Direct URL.", result.Detail);
    }

    [Theory]
    [InlineData("{\"agentName\":\"camel-agent\"}", "camel-agent")]
    [InlineData("{\"agent_name\":\"snake-agent\"}", "snake-agent")]
    [InlineData("{\"agent\":{\"name\":\"nested-agent\"}}", "nested-agent")]
    [InlineData("{\"agent\":\" string-agent \"}", "string-agent")]
    public async Task DirectUrl_ReadsNamesUsedByDifferentAgentVersions(
        string body, string expectedName)
    {
        var tester = new ConnectionTester(new HttpClient(new StubHandler(body)));

        var result = await tester.TestAsync(null, "https://agent.test");

        Assert.True(result.Ok);
        Assert.Equal(expectedName, result.AgentName);
    }

    [Fact]
    public async Task DirectUrl_NetworkFailureReturnsStableDiagnostic()
    {
        var tester = new ConnectionTester(new HttpClient(new DelegateHandler(
            (_, _) => throw new HttpRequestException("socket failed"))));

        var result = await tester.TestAsync(null, "https://agent.test");

        Assert.False(result.Ok);
        Assert.Contains("Could not reach", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectUrl_InternalCancellationIsReportedAsTimeout()
    {
        var tester = new ConnectionTester(new HttpClient(new DelegateHandler(
            (_, _) => throw new OperationCanceledException())));

        var result = await tester.TestAsync(null, "https://agent.test");

        Assert.False(result.Ok);
        Assert.Contains("timed out", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirectUrl_CallerCancellationIsPropagated()
    {
        var tester = new ConnectionTester(new HttpClient(new DelegateHandler(
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            })));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tester.TestAsync(null, "https://agent.test", cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task EmptyTargetExplainsWhatTheUserMustEnter()
    {
        var tester = new ConnectionTester(new HttpClient(new StubHandler("unused")));

        var result = await tester.TestAsync("  ", "  ");

        Assert.False(result.Ok);
        Assert.Contains("Enter a Direct URL or agent address", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidRelayAddressIsRejectedBeforeNetworkAccess()
    {
        var handler = new StubHandler("unused");
        var tester = new ConnectionTester(new HttpClient(handler));

        var result = await tester.TestAsync("agent-with-dashes", null);

        Assert.False(result.Ok);
        Assert.Contains("letters and numbers", result.Detail, StringComparison.Ordinal);
        Assert.Null(handler.LastRequestUri);
    }

    private sealed class StubHandler(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body),
            });
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => sendAsync(request, cancellationToken);
    }
}
