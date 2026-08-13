using System.Net;
using System.Text;
using System.Text.Json;
using ConnectOnion.WinUIClient.Services.Speech;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class VoiceTranscriptionServiceTests
{
    [Fact]
    public async Task TranscribeAsync_SendsTheWebSdkContractWithBearerAuthentication()
    {
        HttpRequestMessage? captured = null;
        using var http = new HttpClient(new StubHandler(async request =>
        {
            captured = await CloneAsync(request);
            return Json(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"  hello from audio  "}}]}""");
        }));
        var service = new VoiceTranscriptionService(
            http,
            (_, _) => Task.FromResult("voice-token"),
            new Uri("https://voice.test/v1/chat/completions"));
        byte[] wave = [0x52, 0x49, 0x46, 0x46];

        var transcript = await service.TranscribeAsync(wave);

        Assert.Equal("hello from audio", transcript);
        Assert.NotNull(captured);
        Assert.Equal("https://voice.test/v1/chat/completions", captured.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("voice-token", captured.Headers.Authorization?.Parameter);
        Assert.False(captured.Headers.Contains("X-Signature"));

        using var body = JsonDocument.Parse(await captured.Content!.ReadAsStringAsync());
        Assert.Equal("gemini-2.5-flash", body.RootElement.GetProperty("model").GetString());
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("Transcribe this audio accurately.", content[0].GetProperty("text").GetString());
        var audio = content[1].GetProperty("input_audio");
        Assert.Equal("wav", audio.GetProperty("format").GetString());
        Assert.Equal(Convert.ToBase64String(wave), audio.GetProperty("data").GetString());
    }

    [Fact]
    public async Task TranscribeAsync_UnauthorizedToken_ReauthenticatesAndRetriesOnce()
    {
        var requests = new List<HttpRequestMessage>();
        var forceValues = new List<bool>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            requests.Add(await CloneAsync(request));
            return requests.Count == 1
                ? Json(HttpStatusCode.Unauthorized, """{"detail":"expired"}""")
                : Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"retry worked"}}]}""");
        }));
        var service = new VoiceTranscriptionService(
            http,
            (force, _) =>
            {
                forceValues.Add(force);
                return Task.FromResult(force ? "fresh-token" : "stale-token");
            },
            new Uri("https://voice.test/v1/chat/completions"));

        var transcript = await service.TranscribeAsync(new byte[] { 1, 2 });

        Assert.Equal("retry worked", transcript);
        Assert.Equal([false, true], forceValues);
        Assert.Equal(2, requests.Count);
        Assert.Equal("stale-token", requests[0].Headers.Authorization?.Parameter);
        Assert.Equal("fresh-token", requests[1].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task TranscribeAsync_MapsAuthenticationFailureWithoutLeakingTheResponseShape()
    {
        using var http = new HttpClient(new StubHandler(_ => Task.FromResult(
            Json(HttpStatusCode.Unauthorized, """{"detail":"identity rejected"}"""))));
        var service = Create(http);

        var error = await Assert.ThrowsAsync<VoiceTranscriptionException>(
            () => service.TranscribeAsync(new byte[] { 1, 2 }));

        Assert.Equal(VoiceTranscriptionFailure.Authentication, error.Failure);
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Equal("identity rejected", error.Message);
    }

    [Fact]
    public async Task TranscribeAsync_ReportsAValidResponseWithNoTranscriptAsNoSpeech()
    {
        using var http = new HttpClient(new StubHandler(_ => Task.FromResult(
            Json(HttpStatusCode.OK, """{"choices":[]}"""))));
        var service = Create(http);

        var error = await Assert.ThrowsAsync<VoiceTranscriptionException>(
            () => service.TranscribeAsync(new byte[] { 1, 2 }));

        Assert.Equal(VoiceTranscriptionFailure.NoSpeech, error.Failure);
    }

    private static VoiceTranscriptionService Create(HttpClient http)
        => new(
            http,
            (_, _) => Task.FromResult("voice-token"),
            new Uri("https://voice.test/v1/chat/completions"));

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is not null)
        {
            clone.Content = new StringContent(
                await request.Content.ReadAsStringAsync(), Encoding.UTF8, "application/json");
        }
        return clone;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
