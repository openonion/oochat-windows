namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

/// <summary>
/// The approval-mode picker means the same thing on both pages that show a composer.
///
/// <para>Mode is conversation-owned state (<c>sessions.mode</c>), but the first message to an agent
/// is composed on <c>AgentDetailPage</c>, which has no conversation — it creates one. So the picked
/// mode has to survive a page navigation, and every link in that chain is a place it can be dropped
/// silently, leaving a control that visibly says "Plan" while the conversation it started runs in
/// Safe. A source contract because none of these types can be loaded headlessly: the view models
/// and pages live in the app project, which drags in the Windows App SDK.</para>
/// </summary>
public class ComposerModeContractTests
{
    [Fact]
    public void AgentDetailPage_ShowsTheModePicker_AndBindsItToTheViewModel()
    {
        var xaml = ReadRepositoryFile("ConnectOnion.WinUIClient", "Views", "AgentDetailPage.xaml");

        Assert.Contains("ShowModeSelector=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Mode=\"{x:Bind Vm.CurrentMode", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "ModeChangeRequested=\"Composer_ModeChangeRequested\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>Link 1: the composer stamps what it was showing onto the submission. Without this
    /// the record carries its default and the picker is decorative.</summary>
    [Fact]
    public void Composer_StampsItsModeOntoEverySubmission()
    {
        var source = ReadRepositoryFile(
            "ConnectOnion.WinUIClient", "Controls", "Chat", "ChatComposer.xaml.cs");

        Assert.Contains(
            "new ComposerSubmission(text, readyAttachments, Mode)", source, StringComparison.Ordinal);
    }

    /// <summary>Link 2: the detail page rebuilds the submission (it re-trims the prompt), which is
    /// exactly where the mode was being dropped.</summary>
    [Fact]
    public void AgentDetailPage_CarriesTheModeThroughTheRebuiltSubmission()
    {
        var source = ReadRepositoryFile(
            "ConnectOnion.WinUIClient", "Views", "AgentDetailPage.xaml.cs");

        Assert.Contains(
            "new ComposerSubmission(initialPrompt, submission.Attachments, submission.Mode)",
            source, StringComparison.Ordinal);
    }

    /// <summary>Link 3: the chat page applies it to the conversation <b>before</b> the first send,
    /// so the turn it starts actually runs under it rather than switching a moment too late.</summary>
    [Fact]
    public void ChatPage_AppliesTheCarriedMode_BeforeTheFirstSend()
    {
        var source = ReadRepositoryFile("ConnectOnion.WinUIClient", "Views", "ChatPage.xaml.cs");

        var setMode = source.IndexOf("SetModeAsync(pending.Mode)", StringComparison.Ordinal);
        var send = source.IndexOf(
            "SendAsync(pending.Text, pending.Attachments)", StringComparison.Ordinal);

        Assert.True(setMode >= 0, "ChatPage must apply the mode carried on the initial submission.");
        Assert.True(send >= 0, "ChatPage must still send the initial submission.");
        Assert.True(setMode < send, "The mode must be applied before the first send, not after it.");
    }

    // Each contract test file carries its own copy, matching StopUiContractTests and the rest of
    // this folder; there is no shared helper to reach for.
    private static string ReadRepositoryFile(params string[] relativeParts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null)
        {
            var candidate = Path.Combine([root.FullName, .. relativeParts]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            root = root.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, relativeParts));
    }
}
