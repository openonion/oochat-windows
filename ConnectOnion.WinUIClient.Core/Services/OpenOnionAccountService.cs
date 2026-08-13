using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Data;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Authenticates this installation's existing Ed25519 identity with OpenOnion and reads its
/// account balance. The bearer token is process-memory only: it is a credential, not a preference,
/// and can be recreated from the DPAPI-protected identity whenever the app starts again.
/// </summary>
public sealed class OpenOnionAccountService : IDisposable
{
    private static readonly Uri AuthUri = new("https://oo.openonion.ai/api/v1/auth");
    private static readonly Uri ProfileUri = new("https://oo.openonion.ai/api/v1/auth/me");
    private readonly HttpClient _http;
    private readonly Func<AgentIdentity> _identityProvider;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private string? _authenticatedAddress;
    private int _stateVersion;
    private bool _disposed;

    public OpenOnionAccountService(HttpClient http)
        : this(http, IdentityStore.EnsureIdentity, () => DateTimeOffset.UtcNow)
    {
    }

    internal OpenOnionAccountService(
        HttpClient http,
        Func<AgentIdentity> identityProvider,
        Func<DateTimeOffset> clock)
    {
        _http = http;
        _identityProvider = identityProvider;
        _clock = clock;
        IdentityStore.IdentityReplaced += OnIdentityChanged;
        IdentityStore.IdentityReset += OnIdentityChanged;
    }

    public string? ApiKey { get; private set; }
    public OpenOnionProfile? Profile { get; private set; }

    /// <summary>
    /// Gets the bearer token derived from the current identity without loading the account
    /// profile. Callers can force a new token after an authenticated API returns 401.
    /// </summary>
    public async Task<string> GetApiKeyAsync(
        bool forceAuthentication = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var version = GetStateVersion();
                try
                {
                    var identity = _identityProvider();
                    var token = GetTokenFor(identity.Address);
                    if (forceAuthentication || attempt > 0 || string.IsNullOrWhiteSpace(token))
                    {
                        token = await AuthenticateCoreAsync(identity, version, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return token;
                }
                catch (AccountStateChangedException) when (attempt < 2)
                {
                    // Retry against an identity restored while authentication was in flight.
                }
            }

            throw new InvalidOperationException(
                "The ConnectOnion identity changed repeatedly while OpenOnion was authenticating. Try again.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gets a fresh profile, authenticating first when this process has no token.</summary>
    public async Task<OpenOnionProfile> RefreshAsync(
        bool forceAuthentication = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // An identity can be restored while a request is in flight. Clear() increments the
            // version, which makes the old request discard its result instead of putting the old
            // account's token/profile back after the identity has changed.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var version = GetStateVersion();
                try
                {
                    return await RefreshCoreAsync(
                        version, forceAuthentication || attempt > 0, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (AccountStateChangedException) when (attempt < 2)
                {
                    // Retry against the replacement identity. This is bounded so a caller that
                    // continuously swaps identities cannot hold the account gate forever.
                }
            }

            throw new InvalidOperationException(
                "The ConnectOnion identity changed repeatedly while the account was loading. Try again.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Clear()
    {
        lock (_stateGate)
        {
            _stateVersion++;
            _authenticatedAddress = null;
            ApiKey = null;
            Profile = null;
        }
    }

    private async Task<OpenOnionProfile> RefreshCoreAsync(
        int version,
        bool forceAuthentication,
        CancellationToken cancellationToken)
    {
        var identity = _identityProvider();
        var token = GetTokenFor(identity.Address);
        if (forceAuthentication || string.IsNullOrWhiteSpace(token))
            token = await AuthenticateCoreAsync(identity, version, cancellationToken).ConfigureAwait(false);

        var response = await GetProfileResponseAsync(token, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            token = await AuthenticateCoreAsync(identity, version, cancellationToken).ConfigureAwait(false);
            response = await GetProfileResponseAsync(token, cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            await EnsureSuccessAsync(response, "OpenOnion profile request failed", cancellationToken)
                .ConfigureAwait(false);
            var profile = await response.Content.ReadFromJsonAsync(
                OpenOnionJsonContext.Default.OpenOnionProfile, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("OpenOnion returned an empty profile.");

            // The JWT is supposed to be bound to the signing public key. Refuse to display or
            // cache another account if an upstream/proxy regression ever violates that invariant.
            if (!string.Equals(profile.PublicKey, identity.Address, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "OpenOnion returned an account for a different ConnectOnion identity.");
            }

            CommitProfile(version, profile);
            return profile;
        }
    }

    private async Task<string> AuthenticateCoreAsync(
        AgentIdentity identity,
        int version,
        CancellationToken cancellationToken)
    {
        var timestamp = _clock().ToUnixTimeSeconds();
        var message = $"ConnectOnion-Auth-{identity.Address}-{timestamp}";
        var request = new OpenOnionAuthRequest(identity.Address, identity.Sign(message), message);

        using var response = await _http.PostAsJsonAsync(
            AuthUri, request, OpenOnionJsonContext.Default.OpenOnionAuthRequest, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, "OpenOnion authentication failed", cancellationToken)
            .ConfigureAwait(false);
        var auth = await response.Content.ReadFromJsonAsync(
            OpenOnionJsonContext.Default.OpenOnionAuthResponse, cancellationToken).ConfigureAwait(false);
        var token = !string.IsNullOrWhiteSpace(auth?.Token)
            ? auth.Token
            : throw new InvalidOperationException("OpenOnion authentication returned no API key.");
        CommitAuthentication(version, identity.Address, token);
        return token;
    }

    private Task<HttpResponseMessage> GetProfileResponseAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ProfileUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private int GetStateVersion()
    {
        lock (_stateGate) return _stateVersion;
    }

    private string? GetTokenFor(string address)
    {
        lock (_stateGate)
        {
            return string.Equals(_authenticatedAddress, address, StringComparison.OrdinalIgnoreCase)
                ? ApiKey
                : null;
        }
    }

    private void CommitAuthentication(int version, string address, string token)
    {
        lock (_stateGate)
        {
            ThrowIfStateChanged(version);
            _authenticatedAddress = address;
            ApiKey = token;
            Profile = null;
        }
    }

    private void CommitProfile(int version, OpenOnionProfile profile)
    {
        lock (_stateGate)
        {
            ThrowIfStateChanged(version);
            Profile = profile;
        }
    }

    private void ThrowIfStateChanged(int expectedVersion)
    {
        if (_stateVersion != expectedVersion) throw new AccountStateChangedException();
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string fallback,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        string? detail = null;
        try
        {
            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            foreach (var propertyName in new[] { "detail", "error", "message" })
            {
                if (json.RootElement.TryGetProperty(propertyName, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    detail = value.GetString();
                    if (!string.IsNullOrWhiteSpace(detail)) break;
                }
            }
        }
        catch
        {
            // A proxy may return HTML or an empty body; the status code still gives a useful error.
        }

        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(detail)
                ? $"{fallback} ({(int)response.StatusCode})."
                : detail,
            null,
            response.StatusCode);
    }

    private void OnIdentityChanged(string _) => Clear();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IdentityStore.IdentityReplaced -= OnIdentityChanged;
        IdentityStore.IdentityReset -= OnIdentityChanged;
        _gate.Dispose();
    }

    private sealed class AccountStateChangedException : Exception;
}

public sealed record OpenOnionProfile(
    [property: JsonPropertyName("public_key")] string PublicKey,
    [property: JsonPropertyName("credits_usd")] decimal CreditsUsd,
    [property: JsonPropertyName("total_cost_usd")] decimal TotalCostUsd,
    [property: JsonPropertyName("balance_usd")] decimal BalanceUsd);

internal sealed record OpenOnionAuthRequest(
    [property: JsonPropertyName("public_key")] string PublicKey,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("message")] string Message);

internal sealed record OpenOnionAuthResponse(
    [property: JsonPropertyName("token")] string Token);

[JsonSerializable(typeof(OpenOnionAuthRequest))]
[JsonSerializable(typeof(OpenOnionAuthResponse))]
[JsonSerializable(typeof(OpenOnionProfile))]
internal sealed partial class OpenOnionJsonContext : JsonSerializerContext;
