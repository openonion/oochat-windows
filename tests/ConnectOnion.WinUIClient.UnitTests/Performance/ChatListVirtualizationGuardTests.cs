using System.IO;
using System.Text.RegularExpressions;

namespace ConnectOnion.WinUIClient.UnitTests.Performance;

/// <summary>
/// A source-level guard for the single most expensive regression this transcript could suffer:
/// swapping the message <see cref="Microsoft.UI.Xaml.Controls.ListView"/>'s virtualizing panel for a
/// plain <c>StackPanel</c>. A <c>StackPanel</c> ItemsPanel turns virtualization <b>off</b> — every
/// message realizes a full visual tree and none are ever recycled — so a 500-message conversation
/// would hold 500 bubbles (each with its Markdown/RichTextBlock, tool cards and attachments) live.
///
/// <para>This can't be asserted from the compiled assembly (an ItemsPanel is XAML, not a type), so
/// the test reads the XAML source. It runs headless in the unit project rather than needing the
/// FlaUI harness, which keeps it in CI.</para>
/// </summary>
public sealed class ChatListVirtualizationGuardTests
{
    private static string ChatPageXaml()
    {
        // Walk up from the test binary to the repo root (identified by the app project folder),
        // so the test is independent of build configuration/TFM path segments.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "ConnectOnion.WinUIClient", "Views", "ChatPage.xaml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate ConnectOnion.WinUIClient/Views/ChatPage.xaml from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void MessageListView_DoesNotOverrideItsPanelWithAPlainStackPanel()
    {
        var xaml = ChatPageXaml();

        // The message list must not declare an <ItemsPanel> at all: the default ListView panel is
        // the virtualizing ItemsStackPanel, and any override here would be the thing that turns it
        // off. (A StackPanel inside a *DataTemplate* row is fine — that lays out one item; this
        // looks only for a panel handed to the ListView as its ItemsPanel.)
        Assert.DoesNotContain("<ListView.ItemsPanel", xaml);

        // And specifically no plain StackPanel wired up as an items panel anywhere in the page.
        Assert.DoesNotMatch(new Regex(@"<ItemsPanelTemplate>\s*<StackPanel", RegexOptions.Singleline), xaml);
    }

    [Fact]
    public void MessageListView_IsPresent_SoThisGuardIsActuallyCheckingSomething()
    {
        // If the ListView were ever renamed/removed the two asserts above would pass vacuously; pin
        // the element this guard is protecting so the guard can't silently stop guarding.
        var xaml = ChatPageXaml();
        Assert.Contains("x:Name=\"MessageList\"", xaml);
    }
}
