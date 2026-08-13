using System.IO;

namespace ConnectOnion.WinUIClient.UnitTests.Performance;

/// <summary>Prevents conversation navigation from retaining one native ListView recycle pool
/// across every chat opened during the process lifetime.</summary>
public sealed class ChatPageLifetimeGuardTests
{
    [Fact]
    public void ChatPage_IsRecreatedAndCleanedUpPerConversation()
    {
        var source = ReadChatPage();

        Assert.Contains("NavigationCacheMode = NavigationCacheMode.Disabled", source);
        Assert.DoesNotContain("ChatPage : Page, IFindHost, IReloadablePage", source);
        Assert.Contains("Vm.Cleanup()", source);
        Assert.Contains("_pageLifetimeCts.Cancel()", source);
        Assert.Contains("MessageList.ItemsSource = null", source);
        Assert.Contains("Dispose();", source);
        Assert.Contains("NativeReclaimPrivateBytesThreshold", source);
        Assert.Contains("NativeReclaimCooldownTicks", source);
        Assert.Contains("ThreadPool.QueueUserWorkItem", source);
        Assert.Contains("GCCollectionMode.Optimized", source);
        Assert.DoesNotContain("GC.WaitForPendingFinalizers()", source);
        Assert.Contains("blocking: false", source);
        Assert.Contains("compacting: false", source);

        var scrolling = ReadSource("Views", "ChatPage.Scrolling.cs");
        Assert.Contains("DismissOverlayWhenItemsReadyAsync(CancellationToken cancellationToken)", scrolling);
        Assert.Contains("Task.Delay(timeout, cancellationToken)", scrolling);

        var viewModel = ReadSource("ViewModels", "ChatViewModel.cs");
        Assert.Contains("message.ApprovalResponder = null", viewModel);
        Assert.Contains("Messages.Clear()", viewModel);

        var imageConverter = ReadSource("Common", "LocalPathToImageSourceConverter.cs");
        Assert.Contains("WeakReference<BitmapImage>", imageConverter);

        var composerXaml = ReadSource("Controls", "Chat", "ChatComposer.xaml");
        Assert.DoesNotContain("<canvas:CanvasAnimatedControl", composerXaml);
        Assert.Contains("<Canvas x:Name=\"WaveformCanvas\"", composerXaml);

        // This used to assert the indicator called Canvas.RemoveFromVisualTree(), which guarded
        // the wrong thing: it only ever ran from Dispose(), and Dispose() is reached by walking
        // ChatPage's live visual tree — so it could not see the instances x:Load had already
        // detached. Unlike the composer's one-per-page canvas, this control is realized inside a
        // virtualized ListView item template, and every realization leaked its D3D device, swap
        // chain and render thread: ~55 threads and ~23 MB per chat turn, taking a 40-turn
        // text-only conversation from 133 MB to 984 MB. The fix is that there is no canvas here
        // at all. See docs/MEMORY_LEAK_INVESTIGATION.md.
        var indicatorXaml = ReadSource("Controls", "Primitives", "ThinkingIndicator.xaml");
        // The element form, matching the composer assertion above: the file's own comment names
        // the type to explain why it is gone, and a bare substring match would trip on that.
        Assert.DoesNotContain("<canvas:CanvasAnimatedControl", indicatorXaml);
        Assert.Contains("<Storyboard x:Key=\"SpinnerStoryboard\"", indicatorXaml);

        var indicator = ReadSource("Controls", "Primitives", "ThinkingIndicator.xaml.cs");
        Assert.DoesNotContain("Microsoft.Graphics.Canvas", indicator);
        // Stop, not Pause: a recycled row must leave no animation clock running behind it.
        Assert.Contains("_spinner.Stop()", indicator);

        var markdown = ReadSource("Controls", "Primitives", "MarkdownTextBlock.cs");
        Assert.Contains("Unloaded += (_, _) => ReleaseDocumentTree()", markdown);
        Assert.Contains("_parsedDocument = null", markdown);

        var home = ReadSource("Views", "HomePage.xaml.cs");
        Assert.Contains("HomePage : Page, IReloadablePage, IShutdownDisarmable, IDisposable", home);
        Assert.Contains("public void DisarmForShutdown()", home);
        Assert.Contains("DisposeCore(clearBoundItems: false)", home);
        Assert.Contains("Volatile.Read(ref _disposed)", home);
        Assert.Contains("protected override void OnNavigatedFrom", home);
        Assert.Contains("Task ReloadAsync()", home);
        Assert.Contains("Agents.Clear()", home);
        Assert.DoesNotContain("new Controls.AddAgentForm()", home);
        Assert.DoesNotContain("OpenAddAgentParameter", home);
        Assert.Contains("ShowAddAgentOverlay", home);
        Assert.DoesNotContain("DispatcherTimer", home);

        var sidebar = ReadSource("Controls", "Shell", "ShellSidebar.xaml.cs");
        Assert.Contains("public void Shutdown()", sidebar);
        Assert.Contains("Volatile.Read(ref _shutdown)", sidebar);

        var mainWindow = ReadSource("MainWindow.xaml.cs");
        Assert.Contains("ShellSidebar.Shutdown()", mainWindow);

        var tray = ReadSource("Shell", "MainWindow.Tray.cs");
        Assert.Contains("Interlocked.Exchange(ref _exitStarted, 1)", tray);

        var addAgentDialog = ReadSource("Controls", "Agents", "AddAgentForm.xaml.cs");
        Assert.Contains("CancelPendingOperation()", addAgentDialog);
        Assert.Contains("public void Shutdown()", addAgentDialog);
        Assert.Contains("CancellationTokenSource", addAgentDialog);
        Assert.DoesNotContain("ContentDialog", addAgentDialog);
        Assert.DoesNotContain("DispatcherTimer", addAgentDialog);

        var mainWindowXaml = ReadSource("MainWindow.xaml");
        Assert.DoesNotContain("<controls:AddAgentForm", mainWindowXaml);
        Assert.DoesNotContain("<controls:SettingsOverlay", mainWindowXaml);
        var overlays = ReadSource("Shell", "MainWindow.Overlays.cs");
        Assert.Contains("EnsureAddAgentOverlay()", overlays);
        Assert.Contains("EnsureSettingsOverlay()", overlays);
        Assert.Contains("FloatingOverlayLayer.Children.Add", overlays);
    }

    private static string ReadChatPage()
        => ReadSource("Views", "ChatPage.xaml.cs");

    private static string ReadSource(params string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                new[] { dir.FullName, "ConnectOnion.WinUIClient" }.Concat(relativePath).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(relativePath)} from {AppContext.BaseDirectory}");
    }
}
