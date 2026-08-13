using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>Which of the two endpoint fields collided with an existing agent. <c>[Flags]</c>
/// because both can collide at once, and the message shown to the user names exactly which
/// — "this address already exists" is actionable in a way that "duplicate agent" isn't.</summary>
[Flags]
public enum AgentEndpointDuplicate
{
    None = 0,
    Address = 1,
    DirectUrl = 2,
}

/// <summary>
/// Compares the connection targets entered by the user with saved agents.
/// Direct URLs are compared by the effective base endpoint used by the
/// connection test, which ignores query strings and fragments.
/// </summary>
public static class AgentEndpointDuplicateDetector
{
    public static AgentEndpointDuplicate Find(
        IEnumerable<AgentConfig> agents,
        string? address,
        string? directUrl)
    {
        ArgumentNullException.ThrowIfNull(agents);

        var candidateAddress = address?.Trim();
        var candidateUrl = NormalizeDirectUrl(directUrl);
        var duplicate = AgentEndpointDuplicate.None;

        // Flags accumulate across the whole list, not per agent: matching agent A's address
        // and agent B's URL still reports both. That is intended — the question being asked
        // is "is either endpoint already spoken for", not "is there one agent equal to this".
        foreach (var agent in agents)
        {
            if (!string.IsNullOrEmpty(candidateAddress) &&
                string.Equals(candidateAddress, agent.Address?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                duplicate |= AgentEndpointDuplicate.Address;
            }

            if (!string.IsNullOrEmpty(candidateUrl) &&
                string.Equals(candidateUrl, NormalizeDirectUrl(agent.DirectUrl), StringComparison.Ordinal))
            {
                duplicate |= AgentEndpointDuplicate.DirectUrl;
            }

            // Nothing more to learn once both flags are set — the remaining agents cannot
            // change the answer.
            if (duplicate == (AgentEndpointDuplicate.Address | AgentEndpointDuplicate.DirectUrl))
            {
                break;
            }
        }

        return duplicate;
    }

    /// <summary>
    /// Reduces a direct URL to the part that identifies the endpoint: scheme + host + port +
    /// path, with query and fragment dropped and any trailing slash removed. Two URLs that
    /// differ only in those respects reach the same agent, so they must compare equal here.
    /// </summary>
    /// <remarks>
    /// The two branches don't normalize case the same way: an unparseable string is
    /// upper-cased, while a parsed URI keeps the case of its path (only host and scheme are
    /// case-folded by <see cref="Uri"/> itself). Since the caller compares with
    /// <see cref="StringComparison.Ordinal"/>, that means <c>/WS</c> and <c>/ws</c> count as
    /// different endpoints for a valid URL but the same for an invalid one. Paths genuinely
    /// are case-sensitive on most hosts, so the parsed branch is the correct behavior — it is
    /// the fallback that is the odd one out.
    /// </remarks>
    private static string NormalizeDirectUrl(string? directUrl)
    {
        var value = directUrl?.Trim();
        if (string.IsNullOrEmpty(value)) return "";

        // Not a URL we can parse — fall back to comparing the raw text rather than treating
        // it as "no URL", so two identical bad entries are still caught as duplicates.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value.TrimEnd('/').ToUpperInvariant();
        }

        var origin = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        var path = uri.AbsolutePath.TrimEnd('/');
        return origin + path;
    }
}
