using System.Collections.Concurrent;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>
/// Credentials the user typed into an agent's <c>ask_user</c> form during this run of the app,
/// remembered only so they can be masked if they come back out somewhere else.
///
/// <para><b>Why this is needed even though the answer summary already masks them.</b> Handing an
/// agent a password is the beginning of its journey, not the end: the agent's next move is
/// typically to type it into a browser, and that tool call's arguments are projected into the
/// timeline and persisted in <c>event_args</c>. So the same secret reaches the same database
/// through a completely different door. Masking it at the form only closes the first one.</para>
///
/// <para><b>Exact matches only.</b> A value is masked when a tool argument <i>is</i> one of the
/// entered credentials, never when it merely contains one. That is deliberate and mirrors the
/// reference client: substring masking over agent-supplied prose turns a short password into a
/// censor that fires on ordinary words, and a timeline full of spurious bullets is both useless and
/// a hint that something was redacted there. The real case — <c>keyboard_type({"text": "hunter2"})</c>
/// — is an exact match, because the JSON walk in <c>ToolActivityProjector</c> applies this per
/// string value rather than to the whole blob.</para>
///
/// <para><b>Never persisted, never logged.</b> The set lives in memory for the process lifetime and
/// dies with it. It is deliberately not keyed by conversation: an agent can carry a credential from
/// the turn that asked for it into any later turn, and a per-conversation set would stop masking at
/// exactly the boundary where the risk continues.</para>
/// </summary>
public static class SessionSecrets
{
    /// <summary>What a remembered secret renders as. Matches the mask used by the answer summary
    /// so a reader sees one consistent "something was hidden here" mark.</summary>
    public const string Mask = "••••••";

    /// <summary>
    /// Values shorter than this are not remembered. A two-character secret would match far too much
    /// ordinary text, and masking a real value is worth less than the damage of masking the wrong
    /// ones — the same trade the reference client makes.
    /// </summary>
    private const int MinimumLength = 3;

    // A dictionary rather than a HashSet purely for the lock-free concurrent membership test:
    // Redact runs on the projection path for every tool argument, from more than one thread.
    private static readonly ConcurrentDictionary<string, byte> Secrets = new(StringComparer.Ordinal);

    /// <summary>Remembers a credential the user just submitted. Blank, whitespace-only and
    /// too-short values are ignored.</summary>
    public static void Remember(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var trimmed = value.Trim();
        if (trimmed.Length < MinimumLength) return;
        Secrets.TryAdd(trimmed, 0);
    }

    /// <summary>Replaces <paramref name="value"/> with <see cref="Mask"/> when it is exactly one of
    /// the remembered credentials; returns it unchanged otherwise.</summary>
    public static string Redact(string value)
        => value is not null && Secrets.ContainsKey(value) ? Mask : value!;

    /// <summary>Whether anything has been remembered. Lets the hot path skip the lookup entirely in
    /// the overwhelmingly common case where no credential was ever entered.</summary>
    public static bool IsEmpty => Secrets.IsEmpty;

    /// <summary>Forgets everything. For tests, and for any future "sign out" action.</summary>
    public static void Clear() => Secrets.Clear();
}
