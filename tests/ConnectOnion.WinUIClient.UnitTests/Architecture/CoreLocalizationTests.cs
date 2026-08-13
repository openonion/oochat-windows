using System.Text.RegularExpressions;

namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

/// <summary>
/// The localization gate for <b>Core</b>.
///
/// <para><c>LocalizationResourceTests</c> checks XAML uids, resw parity, and code-behind under
/// the app's <c>Views</c>/<c>Controls</c>/<c>Shell</c> folders. None of that reaches Core — and
/// Core is where the approval card, the activity header, the interactive phase labels and the
/// tool-status text are actually computed, as XAML-bound properties with no uid to check and no
/// assignment statement to pattern-match. That is how roughly seventy user-visible English
/// strings sat outside every gate this repo has.</para>
///
/// <para>Core cannot call <c>LocalizedStrings</c> (a <c>ResourceManager</c> is Windows App SDK,
/// which the ArchUnit layer gate keeps out), so the contract is: display text goes through
/// <see cref="ConnectOnion.WinUIClient.Common.CoreStrings"/>, whose English fallback is the
/// literal that stays in the source.</para>
/// </summary>
public sealed class CoreLocalizationTests
{
    /// <summary>Files whose literals are display text. Deliberately a list rather than "all of
    /// Core": most of Core is storage and protocol, where an English literal is a column name or
    /// a wire value and localizing it would be the bug.</summary>
    private static readonly string[] DisplayModelFiles =
    [
        "ChatMessage.cs",
        "ChatMessage.Approval.cs",
        "ChatMessage.InteractiveCards.cs",
        "AskUserEntries.cs",
        "ToolActivity.cs",
        "UsageModels.cs",
    ];

    /// <summary>Literals that are <b>not</b> display text and must stay English. Each is either a
    /// value that crosses the wire to the agent, or a marker this client persists and later
    /// matches on — localizing either breaks behaviour rather than translating it.</summary>
    private static readonly string[] AllowedEnglishLiterals =
    [
        // Wire values the host sends, matched on the left-hand side of a switch.
        "Yes, apply this change",
        "Yes to all (auto-approve)",
        "No, reject and give feedback",
        "No, reject",
        "Apply changes to ",
        "Apply this change to ",
        // Stored markers written to and read back from event_meta / event_title.
        "Waiting for you",
        "Skipped",
        "Rejected",
        "Answered:",
        "Approved once",
        "Changes requested",
        "Thinking",
        "approved",
        // Sent to the agent's model, not shown to the user.
        "Explain why this operation is required, what it will do, and what risks it has.",
    ];

    [Fact]
    public void CoreDisplayModels_RouteUserVisibleTextThroughCoreStrings()
    {
        // A literal in a returned position: after =>, ?, or : in an expression-bodied member or
        // a switch arm. Requires a capital letter and a space, so single-word identifiers,
        // format specifiers and glyph codepoints do not trip it.
        var returnedLiteral = new Regex(
            @"(?:=>|\?|:)\s*""([A-Z][^""]*\s[^""]*)""", RegexOptions.CultureInvariant);
        var violations = new List<string>();

        foreach (var fileName in DisplayModelFiles)
        {
            var path = FindCoreFile(fileName);
            var lineNumber = 0;
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith('*'))
                    continue;

                // A CoreStrings call carries its English fallback as an argument by design.
                if (line.Contains("CoreStrings.", StringComparison.Ordinal)) continue;

                var match = returnedLiteral.Match(line);
                if (!match.Success) continue;

                var literal = match.Groups[1].Value;
                if (AllowedEnglishLiterals.Any(allowed =>
                        literal.StartsWith(allowed, StringComparison.Ordinal)))
                    continue;

                violations.Add($"{fileName}:{lineNumber}: \"{literal}\"");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Core display text must go through CoreStrings.Get/Format (add the literal to "
            + "AllowedEnglishLiterals only if it is a wire value or a persisted marker):"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>Every key Core asks for has to exist in both locales. Core's fallback means a
    /// missing key renders English instead of throwing, so nothing else would ever report it.</summary>
    [Fact]
    public void EveryCoreStringsKey_HasARowInBothLocales()
    {
        var call = new Regex(
            @"CoreStrings\.(?:Get|Format)\(\s*""([A-Za-z0-9_.]+)""",
            RegexOptions.CultureInvariant);
        var english = ReadResourceKeys("en-US");
        var chinese = ReadResourceKeys("zh-CN");
        var coreDirectory = FindRepositoryDirectory("ConnectOnion.WinUIClient.Core");
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(coreDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var segments = path.Split(Path.DirectorySeparatorChar);
            if (segments.Contains("obj", StringComparer.OrdinalIgnoreCase)) continue;
            if (segments.Contains("bin", StringComparer.OrdinalIgnoreCase)) continue;

            var lineNumber = 0;
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                foreach (Match match in call.Matches(line))
                {
                    var key = match.Groups[1].Value;
                    var missingIn = new List<string>();
                    if (!english.Contains(key)) missingIn.Add("en-US");
                    if (!chinese.Contains(key)) missingIn.Add("zh-CN");
                    if (missingIn.Count == 0) continue;

                    violations.Add(
                        $"{Path.GetFileName(path)}:{lineNumber} '{key}' missing in "
                        + string.Join(", ", missingIn));
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "CoreStrings keys need a .resw row in both locales:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>Core's wording facade is useless unless <c>App</c> actually points it at the
    /// resource map — and because it falls back to English rather than failing, an unwired build
    /// looks completely normal in English and is wrong in every other language.</summary>
    [Fact]
    public void App_WiresCoreStringsToTheResourceMap()
    {
        var source = File.ReadAllText(
            FindRepositoryFile("ConnectOnion.WinUIClient", "App.xaml.cs"));

        Assert.Contains("CoreStrings.Configure(LocalizedStrings.Get)", source, StringComparison.Ordinal);
    }

    private static HashSet<string> ReadResourceKeys(string locale)
    {
        var path = FindRepositoryFile(
            "ConnectOnion.WinUIClient", "Strings", locale, "Resources.resw");
        var document = System.Xml.Linq.XDocument.Load(path);
        return document.Root!
            .Elements("data")
            .Select(element => element.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string FindCoreFile(string fileName)
    {
        var directory = FindRepositoryDirectory("ConnectOnion.WinUIClient.Core");
        var match = Directory
            .EnumerateFiles(directory, fileName, SearchOption.AllDirectories)
            .FirstOrDefault(path => !path.Split(Path.DirectorySeparatorChar)
                .Contains("obj", StringComparer.OrdinalIgnoreCase));
        Assert.NotNull(match);
        return match!;
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null)
        {
            var candidate = Path.Combine([root.FullName, .. relativeParts]);
            if (File.Exists(candidate)) return candidate;
            root = root.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, relativeParts));
    }

    private static string FindRepositoryDirectory(params string[] relativeParts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null)
        {
            var candidate = Path.Combine([root.FullName, .. relativeParts]);
            if (Directory.Exists(candidate)) return candidate;
            root = root.Parent;
        }

        throw new DirectoryNotFoundException(string.Join(Path.DirectorySeparatorChar, relativeParts));
    }
}
