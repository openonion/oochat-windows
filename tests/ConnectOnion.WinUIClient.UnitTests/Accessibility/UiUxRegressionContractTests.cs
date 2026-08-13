using System.Xml.Linq;

namespace ConnectOnion.WinUIClient.UnitTests.Accessibility;

public sealed class UiUxRegressionContractTests
{
    [Fact]
    public void ConnectionFailures_UseDistinctThemeAwareStatusSurfaces()
    {
        var chat = Read("ConnectOnion.WinUIClient", "Views", "ChatPage.xaml");
        var offline = Read(
            "ConnectOnion.WinUIClient", "Controls", "Chat", "OfflineNoticeBar.xaml");

        Assert.Contains(
            "Visibility=\"{x:Bind IsConnectionError, Mode=OneWay, Converter={StaticResource BoolToVis}}\"",
            chat,
            StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource DangerSubtleBrush}\"", chat, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{ThemeResource DangerBrush}\"", chat, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource AttentionSubtleBrush}\"", offline, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{ThemeResource AttentionBrush}\"", offline, StringComparison.Ordinal);
        Assert.Contains("BorderThickness=\"3,1,1,1\"", offline, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource AccentButtonStyle}\"", offline, StringComparison.Ordinal);
    }

    [Fact]
    public void Composer_OnboardingAndFooter_AdaptInsteadOfDisappearingOrOverflowing()
    {
        var xaml = Read("ConnectOnion.WinUIClient", "Controls", "Chat", "ChatComposer.xaml");
        var code = Read("ConnectOnion.WinUIClient", "Controls", "Chat", "ChatComposer.xaml.cs");

        Assert.Contains("x:Name=\"SuggestionScroller\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollMode=\"Enabled\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ComposerFooter\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{x:Bind Label, Mode=OneTime}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"{x:Bind Prompt, Mode=OneTime}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AgentSkills.CompleteOffers", code, StringComparison.Ordinal);
        Assert.Contains("Summarize your capabilities", code, StringComparison.Ordinal);
        Assert.Contains("Help me get started", code, StringComparison.Ordinal);
        Assert.Contains("Suggest three useful tasks", code, StringComparison.Ordinal);
        Assert.DoesNotContain("What can you do?", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("width >= 620", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Max(280", code, StringComparison.Ordinal);
        Assert.Contains("ModeLabel.Visibility = isNarrow", code, StringComparison.Ordinal);
        Assert.Contains("ComposerFooter.ColumnSpacing = isNarrow", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Sidebar_DisclosuresNameTheirTargetAndExposeUsablePointerTargets()
    {
        var styles = XDocument.Load(PathFor(
            "ConnectOnion.WinUIClient", "Styles", "ControlStyles.xaml"));
        var sidebar = Read("ConnectOnion.WinUIClient", "Controls", "Shell", "ShellSidebar.xaml");
        var item = Read("ConnectOnion.WinUIClient", "Models", "ShellAgentItem.cs");
        var rows = Read("ConnectOnion.WinUIClient", "Models", "ShellSidebarRows.cs");
        var chat = Read("ConnectOnion.WinUIClient", "Views", "ChatPage.xaml");

        var smallStyle = styles.Descendants()
            .Single(element => element.Name.LocalName == "Style"
                && element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value
                    == "SmallIconButtonStyle");
        Assert.Contains(smallStyle.Elements(), element =>
            element.Attribute("Property")?.Value == "Width"
            && element.Attribute("Value")?.Value == "36");
        Assert.Contains(smallStyle.Elements(), element =>
            element.Attribute("Property")?.Value == "Height"
            && element.Attribute("Value")?.Value == "36");

        // Both disclosures name their target. Compiled bindings (the sidebar is x:Bind throughout,
        // for the per-row cost), so the mode is part of the markup being asserted: these names
        // change with expansion state and would go stale under x:Bind's OneTime default.
        Assert.Contains("AutomationProperties.Name=\"{x:Bind ToggleAccessibilityName, Mode=OneWay}\"", sidebar, StringComparison.Ordinal);
        // The pinned disclosure's name now comes from ShellPinnedHeaderRow rather than from the
        // sidebar itself: the row templates moved into one flat ItemsRepeater so the tree could
        // virtualize, and a DataTemplate resolves x:Bind against its item, not the enclosing
        // control. Same requirement as above — the name flips with expansion, so it must be OneWay.
        Assert.Contains("AutomationProperties.Name=\"{x:Bind AccessibilityName, Mode=OneWay}\"", sidebar, StringComparison.Ordinal);
        Assert.Contains("Collapse pinned shortcuts", rows, StringComparison.Ordinal);
        Assert.Contains("Expand pinned shortcuts", rows, StringComparison.Ordinal);
        Assert.Contains("Text=\"Agent library\"", sidebar, StringComparison.Ordinal);
        Assert.Contains("Text=\"Pinned shortcuts\"", sidebar, StringComparison.Ordinal);
        // No "By agent" heading is asserted, and its absence is the point: every group in the tree
        // is headed by an agent's own avatar and name, so the label restated the structure it sat
        // on and spent a row of the sidebar doing it.
        Assert.DoesNotContain("Text=\"By agent\"", sidebar, StringComparison.Ordinal);
        Assert.Contains("Collapse chats for {0}", item, StringComparison.Ordinal);
        Assert.Contains("Expand chats for {0}", item, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Save a copy\"", chat, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_SearchScopeAndFailures_AreVisibleToTheUser()
    {
        var overlayXaml = Read("ConnectOnion.WinUIClient", "Controls", "Settings", "SettingsOverlay.xaml");
        var overlay = Read("ConnectOnion.WinUIClient", "Controls", "Settings", "SettingsOverlay.xaml.cs");
        var settingsXaml = Read("ConnectOnion.WinUIClient", "Views", "SettingsPage.xaml");
        var settingsCode = Read("ConnectOnion.WinUIClient", "Views", "SettingsPage.xaml.cs");
        var usageXaml = Read("ConnectOnion.WinUIClient", "Controls", "Settings", "UsageSettingsContent.xaml");
        var usageCode = Read("ConnectOnion.WinUIClient", "Controls", "Settings", "UsageSettingsContent.xaml.cs");

        Assert.DoesNotContain("SearchBox.Visibility = isSettingsPage", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("CompactSearchBox.Visibility = isSettingsPage", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsNavAudio", overlayXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsCompactCategoryAudio", overlayXaml, StringComparison.Ordinal);
        Assert.Contains("SettingsSearchResultsScrollViewer.Visibility = Visibility.Visible", overlay, StringComparison.Ordinal);
        Assert.Contains("\"Agents\", \"Notifications\", \"Keyboard\", \"Identity\", \"Usage\"", overlay, StringComparison.Ordinal);
        Assert.Contains("IdentityScrollViewer.Visibility = Visibility.Collapsed", overlay, StringComparison.Ordinal);
        Assert.Contains("SettingsSearchResultsHeading", overlay, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SearchEmptyState\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SettingsErrorBar\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SystemNotificationsUnavailableInfoBar\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("AppNotificationCapability.IsAvailable", settingsCode, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"InterfaceTextSizeChoice\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("SetInterfaceTextSizeAsync", settingsCode, StringComparison.Ordinal);
        Assert.Contains("AudioSection.Visibility = isGeneral", settingsCode, StringComparison.Ordinal);
        Assert.Contains("interface text size", settingsCode, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SaveSettingAsync", settingsCode, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UsageErrorBar\"", usageXaml, StringComparison.Ordinal);
        Assert.Contains("UsageErrorBar.IsOpen = true", usageCode, StringComparison.Ordinal);
        Assert.Contains("await Vm.SetRangeAsync(range)", usageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("catch { /* the panel is informational", usageCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_TitleBarHasACompactState_AndModeShortcutIsLocalized()
    {
        var window = Read("ConnectOnion.WinUIClient", "MainWindow.xaml.cs");
        var windowXaml = Read("ConnectOnion.WinUIClient", "MainWindow.xaml");
        var localizer = Read("ConnectOnion.WinUIClient", "Common", "KeyboardTextLocalizer.cs");
        var chinese = XDocument.Load(PathFor(
            "ConnectOnion.WinUIClient", "Strings", "zh-CN", "Resources.resw"));

        Assert.Contains("UpdateTitleBarLayout(windowWidth)", window, StringComparison.Ordinal);
        Assert.Contains("windowWidth < 720", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CompactMenuBarItem\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("CompactMenuBarItem.Visibility = compact ? Visibility.Visible", window, StringComparison.Ordinal);
        Assert.Contains("[\"Cycle approval mode\"] = \"KeyboardCycleChatMode\"", localizer, StringComparison.Ordinal);
        Assert.Contains(chinese.Descendants("data"), element =>
            element.Attribute("name")?.Value == "KeyboardCycleChatMode"
            && !string.IsNullOrWhiteSpace(element.Element("value")?.Value));
    }

    [Fact]
    public void HighRiskFlows_KeepActionsVisibleScrollableAndClipboardSafe()
    {
        var sidebar = Read("ConnectOnion.WinUIClient", "Controls", "Shell", "ShellSidebar.xaml");
        var addAgent = Read("ConnectOnion.WinUIClient", "Controls", "Agents", "AddAgentForm.xaml");
        var search = Read("ConnectOnion.WinUIClient", "Controls", "Shell", "SessionSearchOverlay.xaml");
        var clipboard = Read("ConnectOnion.WinUIClient", "Services", "ClipboardService.cs");
        var recovery = Read("ConnectOnion.WinUIClient", "Controls", "Settings", "RecoveryPhraseDialog.xaml.cs");

        Assert.Contains("x:Uid=\"SidebarAgentNewChatButton\"", sidebar, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility=\"{x:Bind AreActionsVisible", sidebar, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AddAgentFormScrollViewer\"", addAgent, StringComparison.Ordinal);
        Assert.Contains("SessionSearchShortTranscriptHint", search, StringComparison.Ordinal);
        Assert.Contains("IsAllowedInHistory = false", clipboard, StringComparison.Ordinal);
        Assert.Contains("IsRoamable = false", clipboard, StringComparison.Ordinal);
        Assert.Contains("CopySensitiveText", recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupFocusAndHomeWideLayout_DoNotSelectShellChromeOrClipContent()
    {
        var window = Read("ConnectOnion.WinUIClient", "MainWindow.xaml.cs");
        var home = Read("ConnectOnion.WinUIClient", "Views", "HomePage.xaml.cs");

        Assert.Contains(
            "ContentFrame.CurrentSourcePageType == typeof(HomePage)",
            window,
            StringComparison.Ordinal);
        Assert.Contains("if (IsModalOverlayOpen) return", window, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(focused, SidebarToggleButton)", window, StringComparison.Ordinal);
        Assert.Contains("ContentFrame.Focus(FocusState.Programmatic)", window, StringComparison.Ordinal);

        Assert.Contains("WideContentWidth = 1120", home, StringComparison.Ordinal);
        Assert.Contains("WideContentSideMargin = 48", home, StringComparison.Ordinal);
        Assert.Contains(
            "WideViewportMinWidth = WideContentWidth + (2 * WideContentSideMargin)",
            home,
            StringComparison.Ordinal);
        Assert.DoesNotContain("WideViewportMinWidth = 1056", home, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_DefaultsToHome_AndOnlyTheBenchmarkOpensAConversation()
    {
        var window = Read("ConnectOnion.WinUIClient", "MainWindow.xaml.cs");

        Assert.Contains(
            "NavigateTo(benchmarkConversation ? typeof(ChatPage) : typeof(HomePage));",
            window,
            StringComparison.Ordinal);
        Assert.DoesNotContain("restoreConversation", window, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "var sessions = await _sessionRepository.LoadAsync();",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TrimmedChatSurfaces_UseEventFirstTemplatesAndCompiledSuggestionBindings()
    {
        var selector = Read("ConnectOnion.WinUIClient", "Common", "ChatMessageTemplateSelector.cs");
        var composer = Read("ConnectOnion.WinUIClient", "Controls", "Chat", "ChatComposer.xaml");

        Assert.True(
            selector.IndexOf("if (message.IsToolActivityEvent)", StringComparison.Ordinal)
            < selector.IndexOf("if (message.IsAgent)", StringComparison.Ordinal));
        Assert.Contains("x:DataType=\"controls:ComposerSuggestion\"", composer, StringComparison.Ordinal);
        Assert.Contains("Tag=\"{x:Bind Prompt, Mode=OneTime}\"", composer, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"{Binding Prompt}\"", composer, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) => File.ReadAllText(PathFor(parts));

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
