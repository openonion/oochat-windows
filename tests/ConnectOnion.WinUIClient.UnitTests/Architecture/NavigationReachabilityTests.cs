using System.Text.RegularExpressions;

namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

/// <summary>
/// Every <c>Page</c> under <c>Views</c> has to be reachable.
///
/// <para><c>SessionsPage</c> was not, and nothing noticed: it compiled, it kept a
/// <c>SessionsViewModel</c> and five resource keys alive, the sidebar still compared against its
/// type — and its Delete button removed a conversation with no confirmation, unlike the sidebar
/// path that actually shipped. An unreachable page is not merely dead weight; it is code that
/// never gets the fixes the live paths get, waiting for someone to wire it up.</para>
/// </summary>
public sealed class NavigationReachabilityTests
{
    [Fact]
    public void EveryPage_IsNavigatedToFromSomewhere()
    {
        var appDirectory = FindRepositoryDirectory("ConnectOnion.WinUIClient");
        var viewsDirectory = Path.Combine(appDirectory, "Views");
        var sources = Directory
            .EnumerateFiles(appDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar)
                .Any(segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(path => path, File.ReadAllText, StringComparer.Ordinal);

        var pageDeclaration = new Regex(
            @"class\s+(\w+)\s*:\s*Page\b", RegexOptions.CultureInvariant);
        var unreachable = new List<string>();

        foreach (var path in Directory.EnumerateFiles(viewsDirectory, "*.xaml.cs", SearchOption.AllDirectories))
        {
            var match = pageDeclaration.Match(sources[path]);
            if (!match.Success) continue;

            var pageName = match.Groups[1].Value;

            // Reachable one of two ways. Either some *other* file names the type in a typeof(),
            // which is the only form Frame.Navigate accepts...
            var navigated = sources
                .Where(entry => !string.Equals(entry.Key, path, StringComparison.Ordinal))
                .Any(entry => entry.Value.Contains($"typeof({pageName})", StringComparison.Ordinal));

            // ...or a XAML file instantiates it directly as a child element. SettingsPage is
            // hosted that way — it lives inside SettingsOverlay rather than in the content frame,
            // so it is genuinely reachable while never being a navigation target.
            var hosted = !navigated && XamlFiles(appDirectory)
                .Any(xaml => xaml.Contains($":{pageName} ", StringComparison.Ordinal)
                    || xaml.Contains($":{pageName}>", StringComparison.Ordinal));

            if (!navigated && !hosted) unreachable.Add(pageName);
        }

        Assert.True(
            unreachable.Count == 0,
            "These pages are never navigated to — delete them or wire them up: "
            + string.Join(", ", unreachable));
    }

    private static IEnumerable<string> XamlFiles(string appDirectory)
        => Directory
            .EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar)
                .Any(segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)))
            .Select(File.ReadAllText);

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
