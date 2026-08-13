using System;
using System.Globalization;

namespace ConnectOnion.WinUIClient.Common;

/// <summary>
/// Resolves user-visible wording that Core computes for itself.
///
/// <para>Core holds a large number of XAML-bound computed string properties — the approval
/// card's title, subtitle and risk lines, the activity card's header, the ask_user field
/// labels — and none of them can call <c>LocalizedStrings</c>, because Core has no resource
/// map and cannot take one: a <c>ResourceManager</c> is Windows App SDK, which is exactly what
/// the Core seam exists to keep out (see the ArchUnit layer gate).</para>
///
/// <para>So this is the same shape as <c>NotificationLog</c> and <c>IdentityStore</c>'s logger:
/// a static facade that resolves to its English fallback until <c>App</c> hands it a resolver.
/// That default is not a degraded mode — it is what every headless test and every pre-startup
/// call legitimately gets, which is why the fallback is a required parameter rather than a
/// lookup that can fail.</para>
///
/// <para>Distinct from <c>Common.SessionTitles</c>, which passes wording *in at the call site*.
/// That works for <c>SessionSummary.NewConversation</c> because there is a call site to pass
/// it at; a property XAML binds to has none, so the wording has to be reachable from inside.
/// </para>
/// </summary>
public static class CoreStrings
{
    private static Func<string, string, string> _resolver = static (_, fallback) => fallback;

    /// <summary>Points Core's wording at the app's PRI resources. Called once from <c>App</c>'s
    /// constructor, next to the other facade wiring, and before any window exists.</summary>
    public static void Configure(Func<string, string, string> resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    /// <summary>Returns the localized value for <paramref name="key"/>, or
    /// <paramref name="fallback"/> — the diagnosable English text — if nothing is wired up or
    /// the key is missing.</summary>
    public static string Get(string key, string fallback)
    {
        try
        {
            var candidate = _resolver(key, fallback);
            return string.IsNullOrEmpty(candidate) ? fallback : candidate;
        }
        catch (Exception)
        {
            // A resource lookup must never be able to break a chat bubble's rendering.
            return fallback;
        }
    }

    /// <summary>Composite-formats a localized resource using the active UI culture.
    /// <para>Falls back to formatting <paramref name="fallbackFormat"/> if the translation's
    /// placeholders do not match what the caller passed — a bad translation should cost the
    /// user their language on one string, not the whole card.</para></summary>
    public static string Format(string key, string fallbackFormat, params object?[] arguments)
    {
        var format = Get(key, fallbackFormat);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, arguments);
        }
        catch (FormatException)
        {
            return string.Format(CultureInfo.InvariantCulture, fallbackFormat, arguments);
        }
    }
}
