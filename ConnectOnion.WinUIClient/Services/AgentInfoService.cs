using System;
using System.Threading.Tasks;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Fetches agent /info (from Direct URL or relay) and persists it to the
/// agent record so the detail page can load cached data without a network call.
/// </summary>
public static class AgentInfoService
{
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);

    public static bool IsCacheFresh(AgentConfig agent)
    {
        if (string.IsNullOrWhiteSpace(agent.InfoJson) ||
            string.IsNullOrWhiteSpace(agent.InfoUpdatedAt) ||
            !DateTimeOffset.TryParse(agent.InfoUpdatedAt, out var updatedAt))
        {
            return false;
        }

        return DateTimeOffset.UtcNow - updatedAt.ToUniversalTime() < DefaultCacheTtl;
    }

    /// <summary>
    /// Fetches /info from the agent's Direct URL or relay address, then
    /// persists the result to the agent record. Returns the info JSON string
    /// (null if the fetch failed).
    /// </summary>
    public static async Task<string?> FetchAndPersistAsync(AgentConfig agent, bool forceRefresh = false)
    {
        if (!forceRefresh && IsCacheFresh(agent)) return agent.InfoJson;

        string? infoJson = null;

        if (!string.IsNullOrWhiteSpace(agent.DirectUrl))
        {
            infoJson = await FetchFromDirectUrlAsync(agent.DirectUrl);
        }
        else if (!string.IsNullOrWhiteSpace(agent.Address))
        {
            infoJson = await FetchFromRelayAsync(agent.Address, forceRefresh);
        }

        if (infoJson is null) return null;

        // Update the page's live model, then persist only this cache entry. A whole-state save
        // here can overwrite a sidebar selection made while the network request was in flight.
        var updatedAt = DateTime.UtcNow.ToString("o");
        agent.InfoJson = infoJson;
        agent.InfoUpdatedAt = updatedAt;
        await AppServices.Agents.UpdateInfoAsync(agent.Id, infoJson, updatedAt);

        return infoJson;
    }

    /// <summary>
    /// Checks if the agent's info has changed since last persisted. Fetches
    /// fresh info and compares with the cached value. Returns (changed, freshJson).
    /// If the agent can't be reached, returns (false, null).
    /// </summary>
    public static async Task<(bool Changed, string? FreshJson)> CheckForChangesAsync(AgentConfig agent)
    {
        string? freshJson = null;

        if (!string.IsNullOrWhiteSpace(agent.DirectUrl))
        {
            freshJson = await FetchFromDirectUrlAsync(agent.DirectUrl);
        }
        else if (!string.IsNullOrWhiteSpace(agent.Address))
        {
            freshJson = await FetchFromRelayAsync(agent.Address, forceRefresh: true);
        }

        if (freshJson is null) return (false, null);

        var changed = agent.InfoJson != freshJson;
        if (changed)
        {
            var updatedAt = DateTime.UtcNow.ToString("o");
            agent.InfoJson = freshJson;
            agent.InfoUpdatedAt = updatedAt;
            await AppServices.Agents.UpdateInfoAsync(agent.Id, freshJson, updatedAt);
        }

        return (changed, freshJson);
    }

    private static async Task<string?> FetchFromDirectUrlAsync(string directUrl)
    {
        try
        {
            if (!TryBuildInfoUrl(directUrl, out var infoUrl)) return null;
            using var cts = new System.Threading.CancellationTokenSource(8000);
            using var response = await AppServices.Http.GetAsync(infoUrl, cts.Token);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryBuildInfoUrl(string directUrl, out Uri infoUrl)
    {
        infoUrl = null!;
        if (!Uri.TryCreate(directUrl.Trim(), UriKind.Absolute, out var uri)) return false;

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
                ? Uri.UriSchemeHttps
                : uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
                    ? Uri.UriSchemeHttp
                    : uri.Scheme,
            Path = uri.AbsolutePath.TrimEnd('/') + "/info",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        if (uri.IsDefaultPort) builder.Port = -1;
        if (builder.Scheme is not ("http" or "https")) return false;
        infoUrl = builder.Uri;
        return true;
    }

    private static async Task<string?> FetchFromRelayAsync(string address, bool forceRefresh = false)
    {
        try
        {
            var info = await EndpointResolver.FetchAgentInfoAsync(
                AppServices.Http, address, EndpointResolver.DefaultRelay, forceRefresh: forceRefresh);

            if (!info.Online) return null;

            // Composed by the protocol library rather than here, so the writer sits next to the
            // parsers that read this blob back: an anonymous type serialized by reflection gave
            // the nested skills and capabilities their C# names, which those parsers never
            // matched. See EndpointResolver.SerializeAgentInfo.
            return EndpointResolver.SerializeAgentInfo(info);
        }
        catch
        {
            return null;
        }
    }
}
