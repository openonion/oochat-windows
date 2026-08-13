using System.Text.RegularExpressions;

namespace ConnectOnion.WinUIClient.UnitTests.Accessibility;

public sealed partial class AutomationContractTests
{
    private static readonly string[] RequiredIds =
    {
        "MainWindow",
        "AgentList",
        "AgentButton",
        "ChangeAgentIconMenuItem",
        "RemoveAgentIconMenuItem",
        "AgentCapabilitiesLoadingIndicator",
        "ToolActivityHeaderButton",
        "AddAgentButton",
        "AgentAddressInput",
        "SubmitAgentButton",
        "AgentAppearanceExpander",
        "ChooseIconButton",
        "MessageList",
        "MessageInput",
        "SendMessageButton",
        "SuggestionButton",
        "StopResponseButton",
        "RetryTurnButton",
        "ConnectionStatus",
        "ComposerSurface",
        "PendingAttachmentsList",
        "DropOverlayHint",
        "FindCounterText",
        "CloseFindButton",
        "SettingsButton",
        "SystemNotificationsUnavailableInfoBar",
        "HelpMenuButton",
        "FileMenuButton",
        "AboutMenuItem",
        "ConnectOnionDocsMenuItem",
        "KeyboardShortcutsMenuItem",
        "SidebarResizeHandle",
        "SettingsOverlay",
        "SettingsSearchBox",
        "SettingsCategoryPicker",
        "SettingsCloseButton",
        "AgentsNav",
        "SettingsAgentList",
        "SettingsAddAgentButton",
        "AboutOverlay",
        "KeyboardShortcutsOverlay",
        "KeyboardShortcutsCloseButton",
        "AboutOkButton",
        "AboutCloseButton",
        "ShortcutsSearchBox",
        "ApprovalModeButton",
        "SessionSearchButton",
        "SessionSearchOverlay",
        "SessionSearchBox",
        "SessionSearchResults",
        "EmptyStateAddAgentButton",
        "EmptyStateDocsLink",
        "RecoveryPhraseDialog",
    };

    [Fact]
    public void Xaml_E2eAutomationIds_ArePresentAndUnique()
    {
        var root = FindRepositoryRoot();
        var xaml = Directory.GetFiles(
                Path.Combine(root, "ConnectOnion.WinUIClient"), "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => AutomationIdRegex().Matches(File.ReadAllText(path)).Select(match => match.Groups[1].Value))
            .ToList();

        foreach (var required in RequiredIds)
            Assert.Contains(required, xaml);

        var duplicates = xaml.GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Xaml_NamedInteractiveControls_HaveAutomationIds()
    {
        var root = FindRepositoryRoot();
        var failures = new List<string>();

        foreach (var path in Directory.GetFiles(
                     Path.Combine(root, "ConnectOnion.WinUIClient"), "*.xaml", SearchOption.AllDirectories)
                 .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
        {
            var relativePath = Path.GetRelativePath(root, path);
            var text = File.ReadAllText(path);
            foreach (Match element in NamedInteractiveElementRegex().Matches(text))
            {
                var name = element.Groups[2].Value;
                if (!AutomationIdRegex().IsMatch(element.Value))
                    failures.Add($"{relativePath}: {element.Groups[1].Value} x:Name=\"{name}\"");
            }
        }

        Assert.True(failures.Count == 0,
            "Named interactive XAML elements must expose a stable AutomationId:\n" + string.Join("\n", failures));
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

    [GeneratedRegex("(?:AutomationProperties\\.AutomationId=\"|Property=\"AutomationProperties\\.AutomationId\" Value=\")([^\"]+)\"")]
    private static partial Regex AutomationIdRegex();

    [GeneratedRegex("<(Button|ToggleButton|CheckBox|RadioButton|TextBox|PasswordBox|ComboBox|Slider|ListView|GridView|NavigationViewItem|MenuFlyoutItem|HyperlinkButton|AutoSuggestBox|AppBarButton|ToggleSwitch)\\b(?!\\.)[^>]*?x:Name=\"([^\"]+)\"[^>]*?/?>", RegexOptions.Singleline)]
    private static partial Regex NamedInteractiveElementRegex();
}
