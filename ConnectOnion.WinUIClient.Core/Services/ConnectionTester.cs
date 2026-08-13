using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Common;

namespace ConnectOnion.WinUIClient.Services;

public readonly record struct ConnectionTestResult(
    bool Ok, string Detail, string? AgentName = null);

/// <summary>
/// Reachability check for an agent target. Port of <c>connectionTest.ts</c>.
///
/// Direct URL and relay-address paths use the lightweight <c>GET /health</c>
/// contract. Full metadata is fetched separately when the detail page opens.
/// </summary>
public sealed class ConnectionTester
{
    private const int DefaultTimeoutMs = 6000;

    // Relay-address probes make an extra hop through the relay server before the
    // agent is reached, so they need a longer budget than a direct /health hit.
    private const int RelayTimeoutMs = 12000;
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowDuplicateProperties = false,
    };
    private static readonly Regex AddressPattern = new("^[a-zA-Z0-9]+$", RegexOptions.Compiled);
    private readonly HttpClient _http;

    public ConnectionTester(HttpClient http) => _http = http;

    public async Task<ConnectionTestResult> TestAsync(
        string? address, string? directUrl, int timeoutMs = DefaultTimeoutMs,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        directUrl = directUrl?.Trim();

        if (!string.IsNullOrEmpty(directUrl))
        {
            // A Direct URL is the most specific target, so prefer it over a relay
            // address when both are present.
            if (!TryBuildHealthUrl(directUrl, out var healthUrl))
            {
                return new ConnectionTestResult(false, "Invalid Direct URL.");
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeoutMs);
                using var response = await _http
                    .GetAsync(healthUrl, cts.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return new ConnectionTestResult(false, $"Agent responded with HTTP {(int)response.StatusCode}.");
                }

                var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var (healthy, name) = ParseHealth(body);
                if (!healthy)
                {
                    return new ConnectionTestResult(false, "Agent health check reported an unhealthy state.");
                }
                var suffix = name is null ? "" : $" - {FriendlyAgentName.From(name)}";
                return new ConnectionTestResult(
                    true, $"Healthy via Direct URL{suffix}.", AgentName: name);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new ConnectionTestResult(false, "Connection timed out - the agent did not respond in time.");
            }
            catch
            {
                return new ConnectionTestResult(false, "Could not reach the agent (network error or invalid URL).");
            }
        }

        address = address?.Trim();
        if (!string.IsNullOrEmpty(address))
        {
            if (!AddressPattern.IsMatch(address))
            {
                return new ConnectionTestResult(false, "Agent address can only contain letters and numbers.");
            }

            try
            {
                var health = await ConnectOnion.Protocol.EndpointResolver
                    .FetchAgentHealthAsync(_http, address, ConnectOnion.Protocol.EndpointResolver.DefaultRelay, RelayTimeoutMs, forceRefresh)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (health.Online)
                {
                    var suffix = string.IsNullOrEmpty(health.Name)
                        ? ""
                        : $" - {FriendlyAgentName.From(health.Name)}";
                    return new ConnectionTestResult(true, $"Healthy via relay{suffix}.", AgentName: health.Name);
                }
                return new ConnectionTestResult(false, "Registered on the relay but currently offline.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new ConnectionTestResult(false, "Could not reach the relay.");
            }
        }

        return new ConnectionTestResult(false, "Enter a Direct URL or agent address first.");
    }

    // Preserve the configured origin/base path, append /health, drop any
    // user-entered query string or fragment.
    private static bool TryBuildHealthUrl(string directUrl, out Uri healthUrl)
    {
        healthUrl = null!;
        if (!Uri.TryCreate(directUrl, UriKind.Absolute, out var uri)) return false;

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
                ? Uri.UriSchemeHttps
                : uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
                    ? Uri.UriSchemeHttp
                    : uri.Scheme,
            Path = uri.AbsolutePath.TrimEnd('/') + "/health",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        if (uri.IsDefaultPort) builder.Port = -1;
        if (builder.Scheme is not ("http" or "https")) return false;
        healthUrl = builder.Uri;
        return true;
    }

    private static (bool Healthy, string? Name) ParseHealth(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body, JsonOptions);
            var root = doc.RootElement;
            if (root.TryGetProperty("healthy", out var healthy) &&
                healthy.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                healthy.ValueKind == JsonValueKind.False)
            {
                return (false, ExtractAgentName(root));
            }

            if (root.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String &&
                status.GetString()?.Trim().ToLowerInvariant() is
                    ("offline" or "unhealthy" or "error" or "failed" or "fail"))
            {
                return (false, ExtractAgentName(root));
            }

            return (true, ExtractAgentName(root));
        }
        catch
        {
            // A successful HTTP health response is sufficient for older agents that return
            // plain text or an empty body instead of JSON.
            return (true, null);
        }
    }

    // Different SDK/server versions exposed the display name under slightly
    // different keys and a nested `agent` object; read the known spellings.
    private static string? ExtractAgentName(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString()?.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        if (value.ValueKind != JsonValueKind.Object) return null;

        foreach (var key in new[] { "name", "agentName", "agent_name" })
        {
            if (value.TryGetProperty(key, out var field) &&
                field.ValueKind == JsonValueKind.String)
            {
                var s = field.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }

        return value.TryGetProperty("agent", out var nested) ? ExtractAgentName(nested) : null;
    }
}
