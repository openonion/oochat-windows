using System.Net;
using System.Text;
using System.Text.Json;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class OpenOnionAccountServiceTests
{
    [Fact]
    public async Task GetApiKeyAsync_AuthenticatesWithoutLoadingTheProfile_AndCachesTheToken()
    {
        var identity = AgentIdentity.FromSeed(new byte[32]);
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            requests.Add(await CloneAsync(request));
            return Json(HttpStatusCode.OK, """{"token":"voice-token"}""");
        }));
        using var service = new OpenOnionAccountService(
            http, () => identity, () => DateTimeOffset.UnixEpoch);

        var first = await service.GetApiKeyAsync();
        var cached = await service.GetApiKeyAsync();

        Assert.Equal("voice-token", first);
        Assert.Equal(first, cached);
        Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, requests[0].Method);
        Assert.Equal("https://oo.openonion.ai/api/v1/auth", requests[0].RequestUri?.AbsoluteUri);
        Assert.Null(service.Profile);
    }

    [Fact]
    public async Task GetApiKeyAsync_ForceAuthentication_ReplacesTheCachedToken()
    {
        var identity = AgentIdentity.FromSeed(new byte[32]);
        var postCount = 0;
        using var http = new HttpClient(new StubHandler(_ => Task.FromResult(
            Json(HttpStatusCode.OK, $$"""{"token":"token-{{++postCount}}"}"""))));
        using var service = new OpenOnionAccountService(
            http, () => identity, () => DateTimeOffset.UnixEpoch);

        Assert.Equal("token-1", await service.GetApiKeyAsync());
        Assert.Equal("token-2", await service.GetApiKeyAsync(forceAuthentication: true));
        Assert.Equal("token-2", service.ApiKey);
    }

    [Fact]
    public async Task RefreshAsync_SignsIdentity_AuthenticatesAndLoadsProfile()
    {
        var identity = AgentIdentity.FromSeed(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            requests.Add(await CloneAsync(request));
            return request.Method == HttpMethod.Post
                ? Json(HttpStatusCode.OK, """{"token":"test-token"}""")
                : Json(HttpStatusCode.OK,
                    $$"""{"public_key":"{{identity.Address}}","credits_usd":20.5,"total_cost_usd":4.25,"balance_usd":16.25}""");
        }));
        using var service = new OpenOnionAccountService(
            http, () => identity, () => DateTimeOffset.FromUnixTimeSeconds(123456));

        var profile = await service.RefreshAsync();

        Assert.Equal(16.25m, profile.BalanceUsd);
        Assert.Equal("test-token", service.ApiKey);
        Assert.Equal(2, requests.Count);
        using var body = JsonDocument.Parse(await requests[0].Content!.ReadAsStringAsync());
        var message = body.RootElement.GetProperty("message").GetString()!;
        Assert.Equal($"ConnectOnion-Auth-{identity.Address}-123456", message);
        Assert.Equal(identity.Address, body.RootElement.GetProperty("public_key").GetString());
        Assert.True(AgentIdentity.Verify(
            identity.Address, message, body.RootElement.GetProperty("signature").GetString()!));
        Assert.Equal("Bearer", requests[1].Headers.Authorization?.Scheme);
        Assert.Equal("test-token", requests[1].Headers.Authorization?.Parameter);
        Assert.Equal("https://oo.openonion.ai/api/v1/auth", requests[0].RequestUri?.AbsoluteUri);
        Assert.Equal("https://oo.openonion.ai/api/v1/auth/me", requests[1].RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task RefreshAsync_ReauthenticatesOnceAfterUnauthorizedProfile()
    {
        var identity = AgentIdentity.FromSeed(new byte[32]);
        var postCount = 0;
        var getCount = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(Json(HttpStatusCode.OK, $$"""{"token":"token-{{++postCount}}"}"""));
            getCount++;
            return Task.FromResult(getCount == 1
                ? Json(HttpStatusCode.Unauthorized, """{"detail":"expired"}""")
                : Json(HttpStatusCode.OK,
                    $$"""{"public_key":"{{identity.Address}}","credits_usd":1,"total_cost_usd":0,"balance_usd":1}"""));
        }));
        using var service = new OpenOnionAccountService(http, () => identity, () => DateTimeOffset.UnixEpoch);

        await service.RefreshAsync();

        Assert.Equal(2, postCount);
        Assert.Equal(2, getCount);
        Assert.Equal("token-2", service.ApiKey);
    }

    [Fact]
    public async Task RefreshAsync_RejectsAProfileForAnotherIdentity()
    {
        var identity = AgentIdentity.FromSeed(new byte[32]);
        using var http = new HttpClient(new StubHandler(request => Task.FromResult(
            request.Method == HttpMethod.Post
                ? Json(HttpStatusCode.OK, """{"token":"test-token"}""")
                : Json(HttpStatusCode.OK,
                    """{"public_key":"0xwrong","credits_usd":1,"total_cost_usd":0,"balance_usd":1}"""))));
        using var service = new OpenOnionAccountService(
            http, () => identity, () => DateTimeOffset.UnixEpoch);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshAsync());

        Assert.Contains("different ConnectOnion identity", error.Message);
        Assert.Null(service.Profile);
    }

    [Fact]
    public async Task RefreshAsync_IdentityChangesInFlight_DiscardsTheOldAccountResponse()
    {
        var oldIdentity = AgentIdentity.FromSeed(new byte[32]);
        var newIdentity = AgentIdentity.FromSeed(
            Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var currentIdentity = oldIdentity;
        var firstAuthenticationStarted =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstAuthentication =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postCount = 0;

        using var http = new HttpClient(new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                postCount++;
                if (postCount == 1)
                {
                    firstAuthenticationStarted.SetResult(true);
                    await releaseFirstAuthentication.Task;
                    return Json(HttpStatusCode.OK, """{"token":"old-token"}""");
                }

                return Json(HttpStatusCode.OK, """{"token":"new-token"}""");
            }

            return Json(HttpStatusCode.OK,
                $$"""{"public_key":"{{currentIdentity.Address}}","credits_usd":2,"total_cost_usd":0,"balance_usd":2}""");
        }));
        using var service = new OpenOnionAccountService(
            http, () => currentIdentity, () => DateTimeOffset.UnixEpoch);

        var refresh = service.RefreshAsync();
        await firstAuthenticationStarted.Task;
        currentIdentity = newIdentity;
        service.Clear();
        releaseFirstAuthentication.SetResult(true);

        var profile = await refresh;

        Assert.Equal(newIdentity.Address, profile.PublicKey);
        Assert.Equal("new-token", service.ApiKey);
        Assert.Equal(2, postCount);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content is not null)
            clone.Content = new StringContent(await request.Content.ReadAsStringAsync(), Encoding.UTF8, "application/json");
        clone.Headers.Authorization = request.Headers.Authorization;
        return clone;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
