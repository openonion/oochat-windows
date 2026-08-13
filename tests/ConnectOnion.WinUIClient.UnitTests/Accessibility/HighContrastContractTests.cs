using System.Xml.Linq;

namespace ConnectOnion.WinUIClient.UnitTests.Accessibility;

public sealed class HighContrastContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] CriticalBrushes =
    [
        "AppBackgroundBrush",
        "TextPrimaryBrush",
        "TextSecondaryBrush",
        "FocusBorderBrush",
        "SearchMatchBackgroundBrush",
        "SearchMatchTextBrush",
        "ChatBackgroundBrush",
        "UserMessageBackgroundBrush",
        "AgentMessageBackgroundBrush",
        "SidebarBackgroundBrush",
        "SettingsContentBrush",
        "StatusSuccessTextBrush",
        "StatusErrorTextBrush",
        "ComposerBackgroundBrush",
        "ComposerTextBrush",
        "ComposerPrimaryButtonBackgroundBrush",
        "ComposerPrimaryButtonForegroundBrush",
        "ComposerStopButtonBackgroundBrush",
        "ComposerStopButtonForegroundBrush",
    ];

    [Fact]
    public void CriticalSurfaces_UseWindowsSystemColorsInHighContrast()
    {
        var brushes = XDocument.Load(PathFor("ConnectOnion.WinUIClient", "Styles", "Brushes.xaml"));
        var highContrast = brushes.Descendants()
            .Single(element =>
                element.Name.LocalName == "ResourceDictionary"
                && element.Attribute(Xaml + "Key")?.Value == "HighContrast");
        var resources = highContrast.Elements()
            .Where(element => element.Attribute(Xaml + "Key") is not null)
            .ToDictionary(
                element => element.Attribute(Xaml + "Key")!.Value,
                element => element,
                StringComparer.Ordinal);

        foreach (var key in CriticalBrushes)
        {
            Assert.True(resources.TryGetValue(key, out var brush), $"HighContrast is missing {key}.");
            var color = brush!.Attribute("Color")?.Value;
            Assert.Contains("SystemColor", color, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DynamicChatStatusAndErrors_RemainLiveRegions()
    {
        var chat = File.ReadAllText(PathFor("ConnectOnion.WinUIClient", "Views", "ChatPage.xaml"));
        var composer = File.ReadAllText(PathFor(
            "ConnectOnion.WinUIClient", "Controls", "Chat", "ChatComposer.xaml"));

        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", chat, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Assertive\"", chat, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Assertive\"", composer, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ConnectionStatus\"", composer, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalDecisions_UseVisibleTextInAdditionToColorAndIcons()
    {
        var approval = File.ReadAllText(PathFor(
            "ConnectOnion.WinUIClient", "Controls", "Chat", "InteractiveCards", "ApprovalCard.xaml"));

        Assert.Contains("Text=\"Allow once\"", approval, StringComparison.Ordinal);
        Assert.Contains("Text=\"Allow for session\"", approval, StringComparison.Ordinal);
        Assert.Contains("Content=\"Decline\"", approval, StringComparison.Ordinal);
    }

    private static string PathFor(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
