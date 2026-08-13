using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class LocalizationResourceTests
{
    [Fact]
    public void ChineseLocale_HasSameKeysAsEnglishAndNonEmptyValues()
    {
        var english = ReadResources("en-US");
        var chinese = ReadResources("zh-CN");

        Assert.Equal(english.Keys.Order(), chinese.Keys.Order());
        foreach (var key in english.Keys)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(chinese[key]),
                $"Chinese resource '{key}' is empty.");
        }
    }

    [Fact]
    public void RuntimeLookup_HasEnglishFallbackAndCultureAwareFormatting()
    {
        var source = ReadAppSource("Common", "LocalizedStrings.cs");

        Assert.Contains("return fallback;", source, StringComparison.Ordinal);
        Assert.Contains("CultureInfo.CurrentCulture", source, StringComparison.Ordinal);
        Assert.Contains("ReportFallbackOnce", source, StringComparison.Ordinal);
        Assert.Contains("using English", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LiteralRuntimeFallbacks_MatchTheEnglishResourceValue()
    {
        var appDirectory = FindRepositoryDirectory("ConnectOnion.WinUIClient");
        var repository = Directory.GetParent(appDirectory)!.FullName;
        var english = ReadResources("en-US");
        var lookup = new Regex(
            @"(?:LocalizedStrings|CoreStrings)\.(?:Get|Format)\(\s*""(?<key>(?:\\.|[^""])*)""\s*,\s*" +
            @"(?<fallback>""(?:\\.|[^""])*""(?:\s*\+\s*""(?:\\.|[^""])*"")*)",
            RegexOptions.CultureInvariant);
        var literal = new Regex(@"""(?:\\.|[^""])*""", RegexOptions.CultureInvariant);
        var violations = new List<string>();

        foreach (var project in new[] { "ConnectOnion.WinUIClient", "ConnectOnion.WinUIClient.Core" })
        {
            var projectDirectory = Path.Combine(repository, project);
            foreach (var path in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
            {
                var segments = path.Split(Path.DirectorySeparatorChar);
                if (segments.Contains("obj", StringComparer.OrdinalIgnoreCase)
                    || segments.Contains("bin", StringComparer.OrdinalIgnoreCase))
                    continue;

                var source = File.ReadAllText(path);
                foreach (Match match in lookup.Matches(source))
                {
                    var key = JsonSerializer.Deserialize<string>($"\"{match.Groups["key"].Value}\"")!;
                    var fallback = string.Concat(literal.Matches(match.Groups["fallback"].Value)
                        .Select(part => JsonSerializer.Deserialize<string>(part.Value)));
                    var line = source[..match.Index].Count(character => character == '\n') + 1;
                    if (!english.TryGetValue(key, out var resource))
                    {
                        violations.Add($"{project}/{Path.GetRelativePath(projectDirectory, path)}:{line} " +
                            $"'{key}' has no en-US resource row");
                    }
                    else if (!string.Equals(resource, fallback, StringComparison.Ordinal))
                    {
                        violations.Add($"{project}/{Path.GetRelativePath(projectDirectory, path)}:{line} " +
                            $"'{key}' fallback differs from en-US: \"{fallback}\" != \"{resource}\"");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Literal runtime localization fallbacks must be the canonical English resource text:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void LanguagePicker_DefaultsToEnglishAndPersistsSimplifiedChinese()
    {
        var pageSource = ReadAppSource("Views", "SettingsPage.xaml.cs");
        var storeSource = ReadAppSource("Services", "LanguagePreferenceStore.cs");

        Assert.Contains("LanguagePreferenceStore.English", pageSource, StringComparison.Ordinal);
        Assert.Contains("LanguagePreferenceStore.SimplifiedChinese", pageSource, StringComparison.Ordinal);
        Assert.Contains("LanguageRestartInfo.IsOpen = true", pageSource, StringComparison.Ordinal);
        Assert.Contains("public string Current { get; private set; } = English", storeSource, StringComparison.Ordinal);
        Assert.Contains("application_language", storeSource, StringComparison.Ordinal);
        Assert.Contains("ApplicationLanguages.PrimaryLanguageOverride = Current", storeSource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ApplicationLanguages.PrimaryLanguageOverride = normalized",
            storeSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyDotNetCulture(normalized)", storeSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Xaml_UserVisibleEnglishTextRequiresAResourceUid()
    {
        var appDirectory = FindRepositoryDirectory("ConnectOnion.WinUIClient");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] visibleAttributes =
        [
            "Text", "Content", "Header", "Title", "Description",
            "PlaceholderText", "PrimaryButtonText", "SecondaryButtonText",
            "CloseButtonText", "OnContent", "OffContent",
            "ToolTipService.ToolTip"
        ];
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            if (path.Split(Path.DirectorySeparatorChar).Contains("obj", StringComparer.OrdinalIgnoreCase))
                continue;

            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants())
            {
                if (element.Attribute(xaml + "Uid") is not null)
                    continue;

                foreach (var attributeName in visibleAttributes)
                {
                    var attribute = element.Attribute(attributeName);
                    if (attribute is null || !ContainsEnglishWord(attribute.Value))
                        continue;
                    if (attribute.Value is "ConnectOnion" ||
                        attribute.Value.StartsWith("Ctrl+", StringComparison.Ordinal) ||
                        attribute.Value.Contains("://", StringComparison.Ordinal))
                        continue;

                    var line = ((IXmlLineInfo)element).HasLineInfo()
                        ? ((IXmlLineInfo)element).LineNumber
                        : 0;
                    violations.Add(
                        $"{Path.GetRelativePath(appDirectory, path)}:{line} " +
                        $"{element.Name.LocalName}.{attributeName}=\"{attribute.Value}\"");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "User-visible XAML text must use x:Uid and .resw resources:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// An <c>x:Uid</c> with no matching <c>&lt;Uid&gt;.&lt;Property&gt;</c> row is the silent half of
    /// this problem: the element looks localized, the sibling test above is satisfied, and at
    /// runtime WinUI simply leaves the English literal on screen in every language. Nothing caught
    /// it because it cannot be seen from either side alone — the XAML has the uid, the resw has
    /// plenty of keys, and only the pairing is missing.
    /// </summary>
    [Fact]
    public void Xaml_EveryUidBackedAttribute_HasAResourceRowInBothLocales()
    {
        var appDirectory = FindRepositoryDirectory("ConnectOnion.WinUIClient");
        var english = ReadResources("en-US");
        var chinese = ReadResources("zh-CN");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        // AutomationProperties.Name and .HelpText are included here and deliberately absent from
        // the x:Uid-presence test above: they are what a screen reader speaks, so they are
        // user-visible text, but requiring a uid for every one of them would also demand it for
        // the many that are bound to a view model rather than written as a literal.
        string[] localizedAttributes =
        [
            "Text", "Content", "Header", "Title", "Description", "Message",
            "PlaceholderText", "PrimaryButtonText", "SecondaryButtonText",
            "CloseButtonText", "OnContent", "OffContent",
            "ToolTipService.ToolTip",
            "AutomationProperties.Name", "AutomationProperties.HelpText",
        ];
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            if (path.Split(Path.DirectorySeparatorChar).Contains("obj", StringComparer.OrdinalIgnoreCase))
                continue;
            if (path.Split(Path.DirectorySeparatorChar).Contains("bin", StringComparer.OrdinalIgnoreCase))
                continue;

            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants())
            {
                var uid = element.Attribute(xaml + "Uid")?.Value;
                if (string.IsNullOrEmpty(uid)) continue;

                foreach (var attributeName in localizedAttributes)
                {
                    var attribute = element.Attribute(attributeName);
                    // A bound value is the view model's to localize, not the resource map's.
                    if (attribute is null || attribute.Value.StartsWith('{')) continue;
                    if (!ContainsEnglishWord(attribute.Value)) continue;

                    var key = $"{uid}.{attributeName}";
                    var missingIn = new List<string>();
                    if (!english.ContainsKey(key)) missingIn.Add("en-US");
                    if (!chinese.ContainsKey(key)) missingIn.Add("zh-CN");
                    if (missingIn.Count == 0) continue;

                    var line = ((IXmlLineInfo)element).HasLineInfo()
                        ? ((IXmlLineInfo)element).LineNumber
                        : 0;
                    violations.Add(
                        $"{Path.GetRelativePath(appDirectory, path)}:{line} " +
                        $"{key} missing in {string.Join(", ", missingIn)} " +
                        $"(fallback would render \"{attribute.Value}\")");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Every x:Uid-backed user-visible attribute needs its .resw row in both locales:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// An <c>x:Uid</c> is a key into one global resource map, so WinUI applies <b>every</b>
    /// <c>&lt;uid&gt;.&lt;Property&gt;</c> row to <b>every</b> element carrying that uid. Two
    /// elements of different types may therefore only share one if all of its rows are attached
    /// properties, which any <c>FrameworkElement</c> accepts.
    ///
    /// <para>Sharing a uid between, say, a <c>Button</c> and the <c>TextBlock</c> inside it means
    /// the <c>.Text</c> row written for the label is also pushed onto the button, and the page
    /// throws <c>XamlParseException</c> the moment it loads. That is what
    /// <c>IdentityShowPhrase</c> did. The XAML compiler does not check this and the build is
    /// clean, so without this test the only signal is the app crashing on the affected page —
    /// which for a settings sub-page nothing else exercises.</para>
    /// </summary>
    [Fact]
    public void Xaml_UidSharedAcrossElementTypes_OnlyCarriesAttachedProperties()
    {
        var appDirectory = FindRepositoryDirectory("ConnectOnion.WinUIClient");
        var english = ReadResources("en-US");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        // uid -> the distinct element type names using it, with where.
        var usage = new Dictionary<string, List<(string Tag, string Where)>>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            var segments = path.Split(Path.DirectorySeparatorChar);
            if (segments.Contains("obj", StringComparer.OrdinalIgnoreCase)) continue;
            if (segments.Contains("bin", StringComparer.OrdinalIgnoreCase)) continue;

            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants())
            {
                var uid = element.Attribute(xaml + "Uid")?.Value;
                if (string.IsNullOrEmpty(uid)) continue;

                var line = ((IXmlLineInfo)element).HasLineInfo() ? ((IXmlLineInfo)element).LineNumber : 0;
                if (!usage.TryGetValue(uid, out var sites))
                    usage[uid] = sites = [];
                sites.Add((
                    element.Name.LocalName,
                    $"{Path.GetRelativePath(appDirectory, path)}:{line}"));
            }
        }

        var violations = new List<string>();
        foreach (var (uid, sites) in usage)
        {
            var tags = sites.Select(site => site.Tag).Distinct(StringComparer.Ordinal).ToList();
            if (tags.Count <= 1) continue;   // same type twice is fine and sometimes deliberate

            // A dotted property name is an attached property (AutomationProperties.Name,
            // ToolTipService.ToolTip); anything else is declared by one specific control.
            var elementSpecific = english.Keys
                .Where(key => key.StartsWith(uid + ".", StringComparison.Ordinal))
                .Select(key => key[(uid.Length + 1)..])
                .Where(property => !property.Contains('.', StringComparison.Ordinal))
                .ToList();

            if (elementSpecific.Count == 0) continue;

            violations.Add(
                $"x:Uid '{uid}' is shared by {string.Join(", ", tags)} but declares " +
                $"element-specific row(s) {string.Join(", ", elementSpecific)} — " +
                $"used at {string.Join("; ", sites.Select(site => site.Where))}");
        }

        Assert.True(
            violations.Count == 0,
            "A shared x:Uid pushes every resource row onto every element using it:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Sidebar_SecondaryActionsUseContextFlyoutsWithoutMoreButtons()
    {
        var path = FindRepositoryFile(
            "ConnectOnion.WinUIClient", "Controls", "Shell", "ShellSidebar.xaml");
        var document = XDocument.Load(path);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var contextFlyouts = document.Descendants()
            .Where(element => element.Name.LocalName is "Grid.ContextFlyout" or "Border.ContextFlyout")
            .ToList();
        var moreActionButtons = document.Descendants()
            .Where(element => string.Equals(
                element.Attribute(xaml + "Uid")?.Value,
                "SidebarMoreActions",
                StringComparison.Ordinal))
            .ToList();
        var moreIcons = document.Descendants()
            .Where(element => string.Equals(
                element.Attribute("Icon")?.Value,
                "MoreHorizontal",
                StringComparison.Ordinal))
            .ToList();

        Assert.Equal(3, contextFlyouts.Count);
        Assert.Empty(moreActionButtons);
        Assert.Empty(moreIcons);
    }

    [Fact]
    public void PrimaryCodeBehind_DoesNotAssignHardCodedEnglishToUiProperties()
    {
        string[] primaryDirectories = ["Views", "Controls", "Shell"];
        var assignment = new Regex(
            @"(?:Text|Content|Title|Header|PlaceholderText|PrimaryButtonText|" +
            @"SecondaryButtonText|CloseButtonText)\s*=\s*""[A-Za-z]",
            RegexOptions.CultureInvariant);
        var violations = new List<string>();

        foreach (var directoryName in primaryDirectories)
        {
            var directory = FindRepositoryDirectory("ConnectOnion.WinUIClient", directoryName);
            foreach (var path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(
                    FindRepositoryDirectory("ConnectOnion.WinUIClient"),
                    path);
                var lineNumber = 0;
                foreach (var line in File.ReadLines(path))
                {
                    lineNumber++;
                    if (!assignment.IsMatch(line))
                        continue;

                    // Internal font measurement probe; it is never attached to the visual tree.
                    if (relativePath.EndsWith(
                            Path.Combine("Controls", "Primitives", "HighlightedTextBlock.cs"),
                            StringComparison.OrdinalIgnoreCase) &&
                        line.Contains("new TextBlock { Text = \"Hg\"", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    violations.Add($"{relativePath}:{lineNumber} {line.Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Code-behind must resolve user-visible text through LocalizedStrings:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static bool ContainsEnglishWord(string value)
    {
        if (value.StartsWith('{') || value.Length < 2)
            return false;

        var consecutiveLetters = 0;
        foreach (var character in value)
        {
            consecutiveLetters = char.IsAsciiLetter(character) ? consecutiveLetters + 1 : 0;
            if (consecutiveLetters >= 2)
                return true;
        }
        return false;
    }

    private static Dictionary<string, string> ReadResources(string locale)
    {
        var path = FindRepositoryFile(
            "ConnectOnion.WinUIClient", "Strings", locale, "Resources.resw");
        var document = XDocument.Load(path);
        return document.Root!
            .Elements("data")
            .ToDictionary(
                entry => entry.Attribute("name")!.Value,
                entry => entry.Element("value")?.Value ?? "",
                StringComparer.Ordinal);
    }

    private static string ReadAppSource(params string[] relativeParts)
    {
        string[] pathParts = ["ConnectOnion.WinUIClient", .. relativeParts];
        return File.ReadAllText(FindRepositoryFile(pathParts));
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativeParts]);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file {Path.Combine(relativeParts)}.");
    }

    private static string FindRepositoryDirectory(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativeParts]);
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repository directory {Path.Combine(relativeParts)}.");
    }
}
