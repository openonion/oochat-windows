namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

/// <summary>
/// Source-level guards for optimizations whose correctness argument is invisible at the call
/// site. Each of these is a case where the fast version and the slow version look identical on
/// screen until a specific, easily-missed condition occurs.
/// </summary>
public sealed class RenderHotPathContractTests
{
    /// <summary>The brush cache is only safe because a theme flip clears it. Without that, every
    /// converter-driven colour in the app keeps painting the previous theme until its control is
    /// rebuilt — and the app supports live light/dark switching, so that is a visible bug rather
    /// than a theoretical one.</summary>
    [Fact]
    public void ThemeBrushCache_IsClearedWhenTheThemeChanges()
    {
        var source = ReadAppSource("Common", "ThemeBrushResolver.cs");

        Assert.Contains("ThemeService.ThemeApplied", source, StringComparison.Ordinal);
        Assert.Contains("Cache.Clear()", source, StringComparison.Ordinal);
    }

    /// <summary>Both the explicit picker and following the system theme have to invalidate it.
    /// <c>PublishTheme</c> is the single funnel that raises <c>ThemeApplied</c>; if a future
    /// change starts setting <c>_currentTheme</c> without going through it, the cache silently
    /// stops being invalidated on that path.</summary>
    [Fact]
    public void EveryThemeChange_GoesThroughThePublishFunnel()
    {
        var source = ReadAppSource("Services", "ThemeService.cs");

        // The explicit picker and the ActualTheme (follow-system) handler both publish.
        Assert.Contains("PublishTheme(requestedTheme", source, StringComparison.Ordinal);
        Assert.Contains("PublishTheme(actualTheme)", source, StringComparison.Ordinal);
        // ThemeApplied is raised in exactly one place.
        Assert.Equal(
            1,
            source.Split("ThemeApplied?.Invoke", StringSplitOptions.None).Length - 1);
    }

    /// <summary>The markdown renderer is now reused across renders instead of rebuilt. Every
    /// field it accumulates during a pass must therefore be reset — and <c>_matchIndex</c> is the
    /// one that matters: it counts search hits as they are emitted and decides which one gets the
    /// "current match" styling, so carrying it over puts that highlight on the wrong occurrence.
    /// Nothing about the rendered text would look wrong; only the highlight lands elsewhere.</summary>
    [Fact]
    public void ReusedMarkdownRenderer_ResetsItsPerPassState()
    {
        var source = ReadAppSource("Rendering", "WinUiMarkdownRenderer.cs");
        var reset = source[source.IndexOf("internal void Reset(", StringComparison.Ordinal)..];
        reset = reset[..reset.IndexOf("public override object Render", StringComparison.Ordinal)];

        Assert.Contains("_matchIndex = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_uiContainerBlockedDepth = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_quoteDepth = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_listDepth = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_inlineScopes.Clear();", reset, StringComparison.Ordinal);
    }

    /// <summary>Markdig's <c>Setup</c> lets extensions register their own object renderers. On a
    /// reused renderer it must run once, at construction — re-running it per render can append
    /// duplicates that then all fire for the same node.</summary>
    [Fact]
    public void MarkdownPipelineSetup_RunsOnlyWhenTheRendererIsCreated()
    {
        var source = ReadAppSource("Controls", "Primitives", "MarkdownTextBlock.cs");
        var setupIndex = source.IndexOf("Pipeline.Setup(", StringComparison.Ordinal);

        Assert.True(setupIndex > 0, "Pipeline.Setup call not found.");
        // It sits inside the `_renderer is null` construction branch.
        var construction = source.IndexOf("_renderer is null", StringComparison.Ordinal);
        var reset = source.IndexOf("_renderer.Reset(", StringComparison.Ordinal);
        Assert.True(construction > 0 && construction < setupIndex);
        Assert.True(reset > setupIndex, "Setup must belong to the construction branch only.");
        Assert.Equal(1, source.Split("Pipeline.Setup(", StringSplitOptions.None).Length - 1);
    }

    /// <summary>The window's layout scale has one definition. These two computed the same thing
    /// differently — one with the zoom alone, one with zoom × OS text scale — and both feed
    /// <c>SidebarLayoutPolicy.IsCompactWindow</c>, so above 100% system text a resize and a
    /// navigation could disagree about whether the sidebar is an overlay.</summary>
    [Fact]
    public void WindowLogicalWidth_UsesTheSameScaleOnEveryPath()
    {
        var source = ReadAppSource("MainWindow.xaml.cs");

        Assert.Contains("e.NewSize.Width / EffectiveContentScale", source, StringComparison.Ordinal);
        Assert.Contains(
            "RootGrid.ActualWidth / EffectiveContentScale", source, StringComparison.Ordinal);
        Assert.DoesNotContain("e.NewSize.Width / _zoomFactor", source, StringComparison.Ordinal);
    }

    /// <summary>Title-bar metrics read the caption inset and the display DPI — neither depends on
    /// the window's size, and <c>SizeChanged</c> fires per frame during a drag. The guard is what
    /// keeps a COM property write and a P/Invoke off that path; the invalidations are what keep
    /// the values correct when they genuinely change.</summary>
    [Fact]
    public void TitleBarMetrics_AreNotRecomputedOnEveryResize()
    {
        var viewMenu = ReadAppSource("Shell", "MainWindow.ViewMenu.cs");
        var placement = ReadAppSource("Shell", "MainWindow.Placement.cs");

        Assert.Contains("if (!_titleBarMetricsValid) UpdateTitleBarScale();", viewMenu, StringComparison.Ordinal);
        // Invalidated by the two things that actually change them.
        Assert.Contains("_titleBarMetricsValid = false;", viewMenu, StringComparison.Ordinal);
        Assert.Contains("_titleBarMetricsValid = false;", placement, StringComparison.Ordinal);
        // And the DPI read behind them is cached rather than P/Invoked per call.
        Assert.Contains("_cachedDisplayScale", placement, StringComparison.Ordinal);
        Assert.Contains("XamlRoot_Changed", placement, StringComparison.Ordinal);
    }

    /// <summary>At the default scale — no zoom, no OS text scaling, i.e. most users most of the
    /// time — the zoom pass must not allocate. It used to build a ScaleTransform and a Point for
    /// each of three layers and then immediately clear the transform away again, per resize frame.</summary>
    [Fact]
    public void ZoomPass_DoesNotAllocateAtTheDefaultScale()
    {
        var source = ReadAppSource("Shell", "MainWindow.ViewMenu.cs");
        var body = source[source.IndexOf(
            "private void ApplyZoomTo(", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("private void UpdateZoomPercentText", StringComparison.Ordinal)];

        var identityReturn = body.IndexOf("ClearValue(UIElement.RenderTransformProperty)", StringComparison.Ordinal);
        var allocation = body.IndexOf("new ScaleTransform", StringComparison.Ordinal);

        Assert.True(identityReturn > 0 && allocation > 0);
        Assert.True(
            identityReturn < allocation,
            "The identity case must return before any ScaleTransform is allocated.");
    }

    /// <summary>Logging happens on whatever thread ran the code being logged, including the UI
    /// thread. The file sink is synchronous and `shared: true` takes a cross-process mutex per
    /// write, so it belongs behind the async sink — and `shared` itself has to stay, because
    /// toast activation launches a second process that writes the same file.</summary>
    [Fact]
    public void FileLogging_IsAsynchronousAndStillCrossProcessSafe()
    {
        var source = ReadAppSource("App.xaml.cs");

        Assert.Contains("WriteTo.Async(", source, StringComparison.Ordinal);
        Assert.Contains("shared: true", source, StringComparison.Ordinal);
        // An async sink is only safe to lose nothing from if shutdown drains it.
        Assert.Contains("Log.CloseAndFlushAsync()", source, StringComparison.Ordinal);
    }

    /// <summary>InvariantGlobalization would break the zh-CN locale, the RTL check's
    /// <c>CultureInfo.GetCultureInfo</c> call, and every CurrentCulture-formatted figure
    /// including an onboarding card's payment amount. It is the usual companion to the other
    /// runtime switches here, so its absence needs to be deliberate and stay that way.</summary>
    [Fact]
    public void RuntimeSettings_EnablePgoButNotInvariantGlobalization()
    {
        var project = ReadAppSource("ConnectOnion.WinUIClient.csproj");

        Assert.Contains("<TieredPGO>true</TieredPGO>", project, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<InvariantGlobalization>true</InvariantGlobalization>", project, StringComparison.Ordinal);
    }

    /// <summary>Opening a conversation does not close the sidebar.
    ///
    /// <para>Removed by request. It only ever fired at the compact width, where the sidebar is an
    /// overlay — so the trade is real and deliberate: the sidebar now stays where the user put it
    /// and covers the conversation it just opened until they close it themselves. The explicit
    /// ways out (the title-bar toggle, Ctrl+B, tapping the backdrop) all remain, as does the
    /// responsive collapse when the window itself crosses the compact breakpoint, which is a
    /// layout change rather than a navigation.</para></summary>
    [Fact]
    public void Navigation_DoesNotCollapseTheSidebar()
    {
        var source = ReadAppSource("MainWindow.xaml.cs");
        var navigated = source[source.IndexOf(
            "ContentFrame.Navigated +=", StringComparison.Ordinal)..];
        navigated = navigated[..navigated.IndexOf("private ", StringComparison.Ordinal)];

        Assert.DoesNotContain("SetSidebarVisible(false)", navigated, StringComparison.Ordinal);

        // The backdrop and the resize-to-zero paths are explicit user actions and stay.
        Assert.Contains("SidebarDismissLayer_Tapped", source, StringComparison.Ordinal);
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
