using System.Text.RegularExpressions;

namespace ConnectOnion.WinUIClient.UnitTests.Accessibility;

public sealed partial class TypographyContractTests
{
    private static readonly string[] RequiredTypeRampResources =
    {
        "ProductMicroFontSize",
        "ProductCaptionFontSize",
        "ProductCodeFontSize",
        "ProductBodyFontSize",
        "ProductSubtitleFontSize",
        "ProductSectionTitleFontSize",
        "ProductTitleFontSize",
        "ProductHeroFontSize",
    };

    [Fact]
    public void Xaml_FontSizesUseTheSharedTypeRamp()
    {
        var root = FindRepositoryRoot();
        var appRoot = Path.Combine(root, "ConnectOnion.WinUIClient");
        var typographyPath = Path.Combine(appRoot, "Styles", "Typography.xaml");
        var typography = File.ReadAllText(typographyPath);

        foreach (var resource in RequiredTypeRampResources)
            Assert.Contains($"x:Key=\"{resource}\"", typography, StringComparison.Ordinal);

        var violations = Directory.GetFiles(appRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Equals(typographyPath, StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => LiteralFontSizeRegex().Matches(File.ReadAllText(path))
                .Select(match => $"{Path.GetRelativePath(root, path)}: {match.Value}"))
            .ToList();

        Assert.True(violations.Count == 0,
            "Page and control XAML must reference Styles/Typography.xaml instead of embedding numeric FontSize values:\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void TypeRamp_KeepsEssentialTextAtAccessibleCompactDefaults()
    {
        var root = FindRepositoryRoot();
        var typography = File.ReadAllText(Path.Combine(
            root, "ConnectOnion.WinUIClient", "Styles", "Typography.xaml"));

        Assert.Contains("<x:Double x:Key=\"ProductMicroFontSize\">11</x:Double>", typography, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"ProductCaptionFontSize\">12</x:Double>", typography, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"ProductCodeFontSize\">13</x:Double>", typography, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"ProductBodyFontSize\">14</x:Double>", typography, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"ProductSubtitleFontSize\">16</x:Double>", typography, StringComparison.Ordinal);
    }

    [Fact]
    public void App_DefinesSharedLayoutMetricsForPrimarySurfaces()
    {
        var root = FindRepositoryRoot();
        var appXaml = File.ReadAllText(Path.Combine(root, "ConnectOnion.WinUIClient", "App.xaml"));

        Assert.Contains("x:Key=\"AgentLibraryWidth\">1120", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"PageMarginNarrow\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"PageMarginDefault\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"PageMarginWide\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SidebarPrimaryRowHeight\">40", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SidebarSecondaryRowHeight\">36", appXaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "ConnectOnion.WinUIClient")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ConnectOnion repository root.");
    }

    [GeneratedRegex("FontSize=\"\\d+(?:\\.\\d+)?\"")]
    private static partial Regex LiteralFontSizeRegex();
}
