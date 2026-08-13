namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class SidebarSelectionRefreshContractTests
{
    [Fact]
    public void SamePageAgentNavigation_PaintsSelectionBeforeWaitingForPageReload()
    {
        var source = ReadAppSource("MainWindow.xaml.cs");
        var samePageStart = source.IndexOf("if (forceReload", StringComparison.Ordinal);
        var nextNavigationBranch = source.IndexOf(
            "if (forceReload || ContentFrame.CurrentSourcePageType != page)",
            samePageStart,
            StringComparison.Ordinal);
        var samePageBranch = source[samePageStart..nextNavigationBranch];

        var sidebarRefresh = samePageBranch.IndexOf(
            "await ShellSidebar.RefreshAsync()",
            StringComparison.Ordinal);
        var pageReload = samePageBranch.IndexOf(
            "await reloadable.ReloadAsync()",
            StringComparison.Ordinal);

        Assert.True(sidebarRefresh >= 0, "The same-page path must refresh sidebar selection.");
        Assert.True(pageReload >= 0, "The same-page path must reload the current page.");
        Assert.True(
            sidebarRefresh < pageReload,
            "Sidebar selection must render before AgentDetailPage waits for /info.");
    }

    [Fact]
    public void SidebarRefresh_DropsResultsFromSupersededAttempts()
    {
        var source = ReadAppSource("Controls", "Shell", "ShellSidebar.xaml.cs");

        Assert.Contains("Interlocked.Increment(ref _refreshGeneration)", source, StringComparison.Ordinal);
        Assert.Contains("IsRefreshSuperseded(refreshGeneration)", source, StringComparison.Ordinal);

        var finalAwait = source.IndexOf(
            "await AppServices.SidebarState.LoadAsync()",
            StringComparison.Ordinal);
        var revisionCommit = source.IndexOf("_lastLoadedRevision = revision", StringComparison.Ordinal);
        Assert.True(finalAwait >= 0 && revisionCommit > finalAwait);
    }

    [Fact]
    public void SidebarRenderSignature_IncludesUnreadAndAttentionBadges()
    {
        var source = ReadAppSource("Controls", "Shell", "ShellSidebar.xaml.cs");
        var addSessionsStart = source.IndexOf(
            "void AddSessions(IReadOnlyList<SessionSummary> sessions)",
            StringComparison.Ordinal);
        Assert.True(addSessionsStart >= 0, "The session render-signature helper was not found.");

        var addSessions = source[addSessionsStart..];
        Assert.Contains("hash.Add(session.UnreadCount)", addSessions, StringComparison.Ordinal);
        Assert.Contains("hash.Add(session.RequiresAttention)", addSessions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Selecting a conversation must repaint, never rebuild.
    ///
    /// <para>Both selection writes call <c>StorageRevision.Bump</c>, so the cheap revision guard at
    /// the top of <c>RefreshAsync</c> always misses on a click. When the render signature also
    /// folded the selection in, the second guard missed too and the most frequent interaction in
    /// the app went all the way to <c>Clear()</c> + rebuild: every item replaced, <c>ReplaceAll</c>
    /// raising a Reset, the repeater re-realizing every visible row — which discarded the in-place
    /// paint <c>SelectSessionAsync</c> had just done, and made agents with a custom icon flash as
    /// their avatar re-decoded.</para>
    /// </summary>
    [Fact]
    public void SelectionSignature_IsTrackedApartFromTheStructureSignature()
    {
        var source = ReadAppSource("Controls", "Shell", "ShellSidebar.xaml.cs");

        Assert.Contains("private int ComputeStructureSignature(", source, StringComparison.Ordinal);
        Assert.Contains("private int ComputeSelectionSignature(", source, StringComparison.Ordinal);

        // The structure signature must not carry the selection, or the split buys nothing.
        var structureStart = source.IndexOf(
            "private int ComputeStructureSignature(", StringComparison.Ordinal);
        var structureEnd = source.IndexOf("void AddSessions(", structureStart, StringComparison.Ordinal);
        var structure = source[structureStart..structureEnd];
        Assert.DoesNotContain("activeSessionId", structure, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedAgentId", structure, StringComparison.Ordinal);
        Assert.DoesNotContain("_currentPageType", structure, StringComparison.Ordinal);

        // Page type belongs to selection: IsAgentSelected/IsSessionSelected both gate on it, so
        // moving between AgentDetailPage and ChatPage changes which row is lit with no storage
        // change at all. Dropping it would leave the wrong row highlighted.
        var selectionStart = source.IndexOf(
            "private int ComputeSelectionSignature(", StringComparison.Ordinal);
        var selection = source[selectionStart..source.IndexOf(
            "ComputeStructureSignature(", selectionStart, StringComparison.Ordinal)];
        Assert.Contains("_currentPageType", selection, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionOnlyRefresh_RepaintsInPlaceWithoutClearingAnyCollection()
    {
        var source = ReadAppSource("Controls", "Shell", "ShellSidebar.xaml.cs");

        var guardStart = source.IndexOf(
            "if (_lastStructureSignature == structureSignature)", StringComparison.Ordinal);
        Assert.True(guardStart >= 0, "The structure-unchanged fast path was not found.");

        // The body of the fast path, up to its return.
        var guardEnd = source.IndexOf("_lastStructureSignature = structureSignature;", guardStart, StringComparison.Ordinal);
        Assert.True(guardEnd > guardStart, "The fast path must return before the rebuild.");
        var fastPath = source[guardStart..guardEnd];

        Assert.Contains("ApplySelection(", fastPath, StringComparison.Ordinal);
        Assert.DoesNotContain("Clear()", fastPath, StringComparison.Ordinal);
        Assert.DoesNotContain("RebuildSidebarRows()", fastPath, StringComparison.Ordinal);

        // ApplySelection must derive from the same helpers the rebuild uses, so the repainted
        // result is identical to what a rebuild would have produced.
        var applyStart = source.IndexOf(
            "private void ApplySelection(", StringComparison.Ordinal);
        Assert.True(applyStart >= 0, "ApplySelection was not found.");
        var apply = source[applyStart..(applyStart + 900)];
        Assert.Contains("IsAgentSelected(", apply, StringComparison.Ordinal);
        Assert.Contains("IsSessionSelected(", apply, StringComparison.Ordinal);
    }

    /// <summary>The flicker's other half: a cache that evaporates exactly when it is about to be
    /// used is not a cache. See the note in AgentAvatar.</summary>
    [Fact]
    public void AgentAvatarCache_HoldsBitmapsStronglyWithinItsBound()
    {
        var source = ReadAppSource("Controls", "Agents", "AgentAvatar.xaml.cs");

        Assert.Contains("Dictionary<string, BitmapImage> ImageCache", source, StringComparison.Ordinal);
        // Asserted on the lookup rather than on the type name: the file's own comment explains what
        // it used to hold, so a bare "WeakReference<BitmapImage>" search matches the prose.
        Assert.DoesNotContain("TryGetTarget", source, StringComparison.Ordinal);
        // Strong references are only safe while the bound and the decode cap both hold.
        Assert.Contains("MaxCachedImages", source, StringComparison.Ordinal);
        Assert.Contains("MaxDecodePixels", source, StringComparison.Ordinal);
    }

    private static string ReadAppSource(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var root = Path.Combine(directory.FullName, "ConnectOnion.WinUIClient");
            if (Directory.Exists(root))
                return File.ReadAllText(Path.Combine([root, .. relativeParts]));
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the WinUI app source directory.");
    }
}
