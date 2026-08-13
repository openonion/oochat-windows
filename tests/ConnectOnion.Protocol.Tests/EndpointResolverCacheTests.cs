using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectOnion.Protocol.Tests;

public sealed class EndpointResolverCacheTests
{
    [Fact]
    public async Task UniqueLookups_KeepEndpointCacheCapacityBounded()
    {
        EndpointResolver.ClearCachesForTests();
        try
        {
            using var http = new HttpClient(new EmptyRelayHandler());
            for (var i = 0; i < 140; i++)
            {
                await EndpointResolver.ResolveEndpointAsync(
                    http, $"agent-{i}", "https://relay.test", timeoutMs: 100);
            }

            Assert.InRange(EndpointResolver.CacheCountsForTests.Endpoints, 1, 128);
        }
        finally
        {
            EndpointResolver.ClearCachesForTests();
        }
    }

    [Fact]
    public async Task HealthLookup_UsesHealthEndpointAndCachesResult()
    {
        EndpointResolver.ClearCachesForTests();
        try
        {
            var handler = new HealthHandler();
            using var http = new HttpClient(handler);

            var first = await EndpointResolver.FetchAgentHealthAsync(
                http, "agent-1", "https://relay.test", timeoutMs: 100);
            var second = await EndpointResolver.FetchAgentHealthAsync(
                http, "agent-1", "https://relay.test", timeoutMs: 100);

            Assert.True(first.Online);
            Assert.Equal("browser-agent", first.Name);
            Assert.Equal(first, second);
            Assert.Equal(1, handler.HealthRequests);
            Assert.Equal(0, handler.InfoRequests);
            Assert.Equal(1, EndpointResolver.CacheCountsForTests.Healths);
        }
        finally
        {
            EndpointResolver.ClearCachesForTests();
        }
    }

    [Fact]
    public async Task ForcedHealthLookup_BypassesCachedHealth()
    {
        EndpointResolver.ClearCachesForTests();
        try
        {
            var handler = new HealthHandler();
            using var http = new HttpClient(handler);

            await EndpointResolver.FetchAgentHealthAsync(
                http, "agent-1", "https://relay.test", timeoutMs: 100);
            await EndpointResolver.FetchAgentHealthAsync(
                http, "agent-1", "https://relay.test", timeoutMs: 100, forceRefresh: true);

            Assert.Equal(2, handler.HealthRequests);
            Assert.Equal(0, handler.InfoRequests);
        }
        finally
        {
            EndpointResolver.ClearCachesForTests();
        }
    }

    [Fact]
    public async Task InfoLookup_UsesInfoEndpointAndCachesMetadataSeparately()
    {
        EndpointResolver.ClearCachesForTests();
        try
        {
            var handler = new HealthHandler();
            using var http = new HttpClient(handler);

            var first = await EndpointResolver.FetchAgentInfoAsync(
                http, "agent-1", "https://relay.test", timeoutMs: 100);
            var second = await EndpointResolver.FetchAgentInfoAsync(
                http, "agent-1", "https://relay.test", timeoutMs: 100);

            Assert.True(first.Online);
            Assert.Equal("browser-agent", first.Name);
            Assert.Equal(first, second);
            Assert.Equal(1, handler.InfoRequests);
            Assert.Equal(0, handler.HealthRequests);
            Assert.Equal(1, EndpointResolver.CacheCountsForTests.AgentInfos);
        }
        finally
        {
            EndpointResolver.ClearCachesForTests();
        }
    }

    private sealed class EmptyRelayHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"endpoints\":[]}"),
            });
    }

    private sealed class HealthHandler : HttpMessageHandler
    {
        public int HealthRequests { get; private set; }
        public int InfoRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string body;
            if (path.EndsWith("/health", StringComparison.Ordinal))
            {
                HealthRequests++;
                body = """{"status":"ok","address":"agent-1","name":"browser-agent"}""";
            }
            else if (path.EndsWith("/info", StringComparison.Ordinal))
            {
                InfoRequests++;
                body = """{"address":"agent-1","name":"browser-agent","tools":["Click"]}""";
            }
            else
            {
                body = """{"relay":null,"endpoints":["https://agent.test"]}""";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
