using System.Text.RegularExpressions;
using ConnectOnion.Protocol;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// The two things a client does with an agent's declared skills: complete them as slash commands,
/// and turn the best of them into an opening suggestion.
///
/// <para>Both mirror the reference web client (<c>chat-input.tsx</c>'s palette and
/// <c>[address]/page.tsx</c>'s <c>bestOffers</c>) so the same agent reads the same way in both
/// clients. Kept WinUI-free and here rather than in a control's code-behind because the ranking
/// rules are the part worth testing — a matcher that puts the wrong skill first is invisible in a
/// screenshot and obvious in a table.</para>
/// </summary>
public static partial class AgentSkills
{
    /// <summary>How many chips a landing page offers. Three is what fits one row without wrapping,
    /// and past three the list stops reading as a handful of suggestions and starts reading as a
    /// menu the user is expected to study.</summary>
    public const int MaxOffers = 3;

    /// <summary>
    /// Skills that exist for other skills, not for people. They are perfectly real and stay
    /// available to the slash palette (a user who types their name means it); they are only kept
    /// off the chip row, which is a first-impression surface where "capture debug state" is noise.
    /// </summary>
    private static readonly Regex InternalSkill = InternalSkillRegex();

    [GeneratedRegex(
        @"debug|capture|not for direct|called by other skills|internal",
        RegexOptions.IgnoreCase)]
    private static partial Regex InternalSkillRegex();

    /// <summary>
    /// Openers that name an outcome rather than a mechanism. A chip leading with one of these
    /// ("Publish a post…") sorts above one leading with a means ("Use the API to…") — the row has
    /// three slots and they should go to offers a user can say yes to.
    /// </summary>
    private static readonly Regex GoalVerb = GoalVerbRegex();

    [GeneratedRegex(
        @"^(publish|post|submit|send|create|write|draft|schedule|generate|search|find|reply|engage|react|comment|log|translate|summarize|analyze|review|build|make|plan|book)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex GoalVerbRegex();

    /// <summary>Clause boundaries a description is cut at, so a chip is an offer rather than a
    /// paragraph. Ordered longest-first only for readability; the cut takes whichever appears
    /// earliest and still leaves four words behind.</summary>
    private static readonly string[] ClauseBreaks =
    [
        ", ", "; ", " — ", " - ", " in the ", " through ", " via ", " using ", " by ", " from ",
        " so ", " and then ",
    ];

    /// <summary>Words that cannot end a chip: cutting at a clause boundary can strand a
    /// preposition, and "Publish a post to" reads as a truncation bug rather than an offer.</summary>
    private static readonly HashSet<string> DanglingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "to", "of", "in", "into", "on", "or", "and", "for", "with", "by", "from",
    };

    /// <summary>Brand names the agents' own descriptions routinely lowercase. Cosmetic, but a chip
    /// is the most-read text on the page.</summary>
    private static readonly (string Pattern, string Replacement)[] BrandCasing =
    [
        ("linkedin", "LinkedIn"), ("github", "GitHub"), ("youtube", "YouTube"),
    ];

    // ---- Slash palette ------------------------------------------------------

    /// <summary>
    /// The skills matching what the user has typed after <c>/</c>, best first.
    ///
    /// <para>Forgiving on purpose (see <see cref="MatchRank"/>): people remember roughly what a
    /// skill is called, and a palette that only accepts prefixes makes them type the identifier
    /// exactly right — which is the thing the palette exists to spare them.</para>
    /// </summary>
    /// <param name="query">Text after the slash, already lowercased or not; casing is ignored.
    /// Empty lists every skill, which is what an bare <c>/</c> should show.</param>
    public static IReadOnlyList<SkillInfo> Match(IReadOnlyList<SkillInfo>? skills, string? query)
    {
        if (skills is null || skills.Count == 0) return [];

        var normalized = (query ?? "").Trim().ToLowerInvariant();
        if (normalized.Length == 0) return skills;

        return skills
            .Select(skill => (Skill: skill, Rank: MatchRank(skill.Name, normalized)))
            .Where(candidate => candidate.Rank >= 0)
            // OrderBy is stable in LINQ, so equal-rank skills keep the agent's own declared order
            // rather than being reshuffled between keystrokes.
            .OrderBy(candidate => candidate.Rank)
            .Select(candidate => candidate.Skill)
            .ToList();
    }

    /// <summary>
    /// How well a skill name matches a query: <b>0</b> prefix, <b>1</b> substring, <b>2</b> letters
    /// in order, <b>-1</b> no match.
    ///
    /// <para>The in-order tier is what makes "/linkedeng" find <c>linkedin-engagement</c>. It is
    /// last because it is the loosest — a short query matches a lot of things that way — and a
    /// prefix is almost always what someone typing a name means.</para>
    /// </summary>
    public static int MatchRank(string? name, string? query)
    {
        var haystack = (name ?? "").ToLowerInvariant();
        var needle = (query ?? "").ToLowerInvariant();
        if (needle.Length == 0) return 0;
        if (haystack.Length == 0) return -1;

        if (haystack.StartsWith(needle, StringComparison.Ordinal)) return 0;
        if (haystack.Contains(needle, StringComparison.Ordinal)) return 1;

        var matched = 0;
        foreach (var ch in haystack)
        {
            if (matched < needle.Length && ch == needle[matched]) matched++;
        }
        return matched == needle.Length ? 2 : -1;
    }

    /// <summary>
    /// Whether <paramref name="text"/> is a slash command still being typed, and therefore whether
    /// the palette should be open.
    ///
    /// <para>The space is the commit: everything after it is arguments, so the palette closes and
    /// Enter goes back to sending the message. Without that rule a user who completed a command
    /// and started typing its argument would still have Enter stolen by the palette.</para>
    /// </summary>
    public static bool TryGetSlashQuery(string? text, out string query)
    {
        query = "";
        if (text is null || text.Length == 0 || text[0] != '/') return false;
        if (text.Contains(' ') || text.Contains('\n') || text.Contains('\r')) return false;
        query = text[1..];
        return true;
    }

    // ---- Opening suggestions ------------------------------------------------

    /// <summary>
    /// The agent's best few skills phrased as offers — the chips a landing page puts before its
    /// generic "What can you do?" prompts.
    ///
    /// <para>Best, not merely parseable: internal utilities are dropped, descriptions that will not
    /// cut into a clean short phrase are dropped rather than truncated, and what survives is
    /// ordered by whether it leads with an outcome. An agent whose descriptions are all unusable
    /// yields nothing, and the caller fills the row from its static prompts. A bad chip is worse
    /// than no chip, because it is the first thing the user reads about the agent.</para>
    /// </summary>
    public static IReadOnlyList<string> BestOffers(IReadOnlyList<SkillInfo>? skills, int max = MaxOffers)
    {
        if (skills is null || skills.Count == 0 || max <= 0) return [];

        return skills
            .Where(skill => !InternalSkill.IsMatch(skill.Name) && !InternalSkill.IsMatch(skill.Description ?? ""))
            .Select(skill => ChipOffer(skill.Description))
            .Where(offer => offer is not null)
            .Select(offer => offer!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(offer => GoalVerb.IsMatch(offer) ? 0 : 1)
            .ThenBy(offer => offer.Length)
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// Keeps an agent's own opening offers first, then fills any empty slots with generic
    /// starters. Duplicate or blank text never consumes a slot.
    ///
    /// <para>The landing row is designed as a set of three choices. Treating one usable skill as
    /// a replacement for the whole generic set left the row looking accidentally incomplete;
    /// this makes replacement happen per slot instead.</para>
    /// </summary>
    public static IReadOnlyList<string> CompleteOffers(
        IReadOnlyList<string>? offers,
        IReadOnlyList<string>? fallbacks,
        int max = MaxOffers)
    {
        if (max <= 0) return [];

        var completed = new List<string>(max);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Append(offers);
        Append(fallbacks);
        return completed;

        void Append(IReadOnlyList<string>? candidates)
        {
            if (candidates is null) return;

            foreach (var candidate in candidates)
            {
                if (completed.Count == max) return;
                if (string.IsNullOrWhiteSpace(candidate)) continue;

                var normalized = candidate.Trim();
                if (seen.Add(normalized)) completed.Add(normalized);
            }
        }
    }

    /// <summary>
    /// Extracts one short imperative from a skill description, or null when it cannot produce a
    /// clean one. Null is a real answer, not a failure: a command-named or badly-described skill
    /// should stay off the chip row rather than leak an identifier or a half-sentence into it.
    /// </summary>
    public static string? ChipOffer(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;

        // First sentence only. A description's later sentences are caveats and mechanics.
        var cut = FirstSentence(description.Trim());

        foreach (var boundary in ClauseBreaks)
        {
            var index = cut.IndexOf(boundary, StringComparison.OrdinalIgnoreCase);
            // The four-word floor stops an early comma from cutting the phrase down to "Publish a".
            if (index > 0 && WordCount(cut[..index]) >= 4) cut = cut[..index];
        }

        cut = cut.TrimEnd('.', '!', '?', ',', ';', ':').Trim();
        var words = cut.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Too long to read at a glance, or too short to be an offer.
        if (cut.Length > 48 || words.Length < 2) return null;
        // A cut that ends on a preposition reads as a truncation bug.
        if (DanglingWords.Contains(words[^1])) return null;

        return FixBrandCase(cut);
    }

    private static string FirstSentence(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] is '.' or '!' or '?' && char.IsWhiteSpace(text[i + 1]))
                return text[..(i + 1)];
        }
        return text;
    }

    private static int WordCount(string text)
        => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Normalizes the casing of a few brand names inside a generated offer.
    /// <para>Ordinary case-insensitive string replacement, not <c>Regex.Replace</c>. Every
    /// pattern in <see cref="BrandCasing"/> is a plain literal with no regex metacharacter in it,
    /// so the regex engine was parsing three patterns per call to do what <c>string.Replace</c>
    /// does directly — and these were the only runtime-constructed regexes left in the codebase
    /// (everything else is <c>[GeneratedRegex]</c>).</para></summary>
    private static string FixBrandCase(string text)
    {
        foreach (var (pattern, replacement) in BrandCasing)
        {
            text = text.Replace(pattern, replacement, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }
}
