using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectOnion.WinUIClient.Services.Speech;

/// <summary>
/// Sends a recorded WAV message to the same OpenOnion multimodal transcription endpoint used by
/// <c>@connectonion/react</c>. The installation identity is exchanged for the same in-memory
/// OpenOnion bearer token used by the web client, so voice input adds no persisted credential.
/// </summary>
public sealed class VoiceTranscriptionService
{
    internal const string DefaultModel = "gemini-2.5-flash";
    private static readonly Uri DefaultEndpoint =
        new("https://oo.openonion.ai/v1/chat/completions");

    private readonly HttpClient _http;
    private readonly Func<bool, CancellationToken, Task<string>> _tokenProvider;
    private readonly Uri _endpoint;

    public VoiceTranscriptionService(HttpClient http, OpenOnionAccountService account)
        : this(http, account.GetApiKeyAsync, DefaultEndpoint)
    {
    }

    internal VoiceTranscriptionService(
        HttpClient http,
        Func<bool, CancellationToken, Task<string>> tokenProvider,
        Uri endpoint)
    {
        _http = http;
        _tokenProvider = tokenProvider;
        _endpoint = endpoint;
    }

    public async Task<string> TranscribeAsync(
        ReadOnlyMemory<byte> waveAudio,
        CancellationToken cancellationToken = default)
    {
        if (waveAudio.IsEmpty)
            throw new ArgumentException("Recorded audio cannot be empty.", nameof(waveAudio));

        var body = new VoiceChatCompletionRequest(
            DefaultModel,
            [new VoiceMessage(
                "user",
                [
                    new VoiceContent("text", "Transcribe this audio accurately.", null),
                    new VoiceContent(
                        "input_audio",
                        null,
                        new VoiceAudio(Convert.ToBase64String(waveAudio.Span), "wav")),
                ])]);

        var token = await GetTokenAsync(forceAuthentication: false, cancellationToken)
            .ConfigureAwait(false);
        var response = await SendAsync(body, token, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            token = await GetTokenAsync(forceAuthentication: true, cancellationToken)
                .ConfigureAwait(false);
            response = await SendAsync(body, token, cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorDetailAsync(response, cancellationToken)
                    .ConfigureAwait(false);
                throw new VoiceTranscriptionException(
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? VoiceTranscriptionFailure.Authentication
                        : VoiceTranscriptionFailure.Service,
                    response.StatusCode,
                    detail);
            }

            var result = await response.Content.ReadFromJsonAsync(
                VoiceTranscriptionJsonContext.Default.VoiceChatCompletionResponse,
                cancellationToken).ConfigureAwait(false);
            var transcript = result?.Choices is { Count: > 0 } choices
                ? choices[0].Message?.Content?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                throw new VoiceTranscriptionException(
                    VoiceTranscriptionFailure.NoSpeech,
                    response.StatusCode,
                    "The transcription service returned no text.");
            }

            return transcript;
        }
    }

    private async Task<string> GetTokenAsync(
        bool forceAuthentication,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _tokenProvider(forceAuthentication, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
            when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new VoiceTranscriptionException(
                VoiceTranscriptionFailure.Authentication,
                exception.StatusCode,
                exception.Message);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        VoiceChatCompletionRequest body,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(
                body, VoiceTranscriptionJsonContext.Default.VoiceChatCompletionRequest),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string?> ReadErrorDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            foreach (var name in new[] { "detail", "error", "message" })
            {
                if (!json.RootElement.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind == JsonValueKind.String) return value.GetString();
                if (value.ValueKind == JsonValueKind.Object
                    && value.TryGetProperty("message", out var nested)
                    && nested.ValueKind == JsonValueKind.String)
                {
                    return nested.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Proxies sometimes return HTML. The status code remains the useful part.
        }

        return null;
    }
}

public enum VoiceTranscriptionFailure
{
    Authentication,
    Service,
    NoSpeech,
}

public sealed class VoiceTranscriptionException : Exception
{
    public VoiceTranscriptionException(
        VoiceTranscriptionFailure failure,
        HttpStatusCode? statusCode,
        string? detail)
        : base(string.IsNullOrWhiteSpace(detail)
            ? $"Voice transcription failed{(statusCode is null ? "." : $" ({(int)statusCode}).")}"
            : detail)
    {
        Failure = failure;
        StatusCode = statusCode;
    }

    public VoiceTranscriptionFailure Failure { get; }
    public HttpStatusCode? StatusCode { get; }
}

internal sealed record VoiceChatCompletionRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<VoiceMessage> Messages);

internal sealed record VoiceMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] IReadOnlyList<VoiceContent> Content);

internal sealed record VoiceContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text,
    [property: JsonPropertyName("input_audio"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] VoiceAudio? InputAudio);

internal sealed record VoiceAudio(
    [property: JsonPropertyName("data")] string Data,
    [property: JsonPropertyName("format")] string Format);

internal sealed record VoiceChatCompletionResponse(
    [property: JsonPropertyName("choices")] IReadOnlyList<VoiceChoice>? Choices);

internal sealed record VoiceChoice(
    [property: JsonPropertyName("message")] VoiceResponseMessage? Message);

internal sealed record VoiceResponseMessage(
    [property: JsonPropertyName("content")] string? Content);

[JsonSerializable(typeof(VoiceChatCompletionRequest))]
[JsonSerializable(typeof(VoiceChatCompletionResponse))]
internal sealed partial class VoiceTranscriptionJsonContext : JsonSerializerContext;
