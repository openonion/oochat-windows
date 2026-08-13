namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

/// <summary>
/// Full-window modal overlays size themselves through layout, not through code.
///
/// <para>Each of these cards used to compute <c>ModalContainer.Width</c> from its own
/// <c>ActualWidth</c> with a private margin constant. Two things made that wrong. The constant
/// disagreed with the <c>Margin</c> the XAML actually applied, so the card was never quite
/// "available minus margin". And <c>ActualWidth</c> is not the window width: <c>MainWindow</c>
/// scales <c>FloatingOverlayLayer</c> by <c>EffectiveContentScale</c> (user zoom × OS text scale),
/// so the overlay measures <c>window ÷ scale</c>.</para>
///
/// <para><c>SettingsOverlay</c> is where that surfaced. Its adaptive layout is driven by
/// <c>AdaptiveTrigger.MinWindowWidth</c>, which measures the <b>window</b>, using the same 860/640
/// breakpoints the code used against the <b>scaled</b> width — so away from 100% the visual state
/// could choose the wide layout (210px navigation column, 32px margin) while the code sized the
/// card for a narrower bucket, and Settings opened visibly too narrow.</para>
/// </summary>
public class OverlaySizingContractTests
{
    private static readonly string[][] Overlays =
    [
        ["ConnectOnion.WinUIClient", "Controls", "Settings", "SettingsOverlay"],
        ["ConnectOnion.WinUIClient", "Controls", "Settings", "KeyboardShortcutsDialog"],
        ["ConnectOnion.WinUIClient", "Controls", "Shell", "AboutOverlay"],
        ["ConnectOnion.WinUIClient", "Controls", "Shell", "SessionSearchOverlay"],
    ];

    /// <summary>The one rule: nothing assigns the card's width from code.
    ///
    /// <para><c>MaxHeight</c> is deliberately still allowed — two of these cap height at a
    /// <i>proportion</i> of the available space, which XAML cannot express, and a proportion has no
    /// breakpoint to land in the wrong side of.</para></summary>
    [Theory]
    [MemberData(nameof(OverlayNames))]
    public void Overlay_NeverAssignsItsCardWidthFromCode(string folder, string subfolder, string name)
    {
        var source = ReadRepositoryFile(
            "ConnectOnion.WinUIClient", "Controls", subfolder, $"{name}.xaml.cs");

        Assert.DoesNotContain("ModalContainer.Width", source, StringComparison.Ordinal);
        // safeMargin was the private constant that drifted from the XAML Margin.
        Assert.DoesNotContain("safeMargin", source, StringComparison.Ordinal);
        _ = folder;
    }

    /// <summary>Stretch is what makes the declarative form equivalent: it fills the space the
    /// margin leaves, and WinUI centres it once <c>MaxWidth</c> binds. <c>Center</c> means "size to
    /// content", which is why the code had to force a width in the first place.</summary>
    [Theory]
    [MemberData(nameof(OverlayNames))]
    public void OverlayCard_StretchesHorizontally_AndIsCappedByMaxWidth(
        string folder, string subfolder, string name)
    {
        var xaml = ReadRepositoryFile(
            "ConnectOnion.WinUIClient", "Controls", subfolder, $"{name}.xaml");

        var card = CardDeclaration(xaml);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", card, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=", card, StringComparison.Ordinal);
        // A fixed Width would defeat the margin the same way the code did.
        Assert.DoesNotContain(" Width=", card, StringComparison.Ordinal);
        _ = folder;
    }

    /// <summary>Settings is the case the breakpoints collided in, so pin that its adaptive sizing
    /// lives only in the visual states.</summary>
    [Fact]
    public void SettingsOverlay_KeepsItsBreakpointsInOnePlace()
    {
        var xaml = ReadRepositoryFile(
            "ConnectOnion.WinUIClient", "Controls", "Settings", "SettingsOverlay.xaml");
        var source = ReadRepositoryFile(
            "ConnectOnion.WinUIClient", "Controls", "Settings", "SettingsOverlay.xaml.cs");

        // The breakpoints exist, in XAML, once.
        Assert.Contains("MinWindowWidth=\"860\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWindowWidth=\"640\"", xaml, StringComparison.Ordinal);

        // And nowhere in executable code, where they would be compared against the scaled width.
        // Comments are stripped first: this file's own explanation of the bug names the numbers,
        // and an assertion that a comment cannot mention them would be a trap, not a gate.
        var code = StripComments(source);
        Assert.DoesNotContain("860", code, StringComparison.Ordinal);
        Assert.DoesNotContain("640", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ModalContainer.Margin", code, StringComparison.Ordinal);
    }

    private static string StripComments(string source) => string.Join(
        '\n',
        source.Split('\n')
            .Select(line => line.TrimStart())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal)));

    public static TheoryData<string, string, string> OverlayNames()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var parts in Overlays) data.Add(parts[0], parts[2], parts[3]);
        return data;
    }

    /// <summary>The ModalContainer element's opening tag, so an assertion about the card cannot be
    /// satisfied by some unrelated element elsewhere in the file.</summary>
    private static string CardDeclaration(string xaml)
    {
        var start = xaml.IndexOf("<Border x:Name=\"ModalContainer\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "Every overlay card is a Border named ModalContainer.");
        var end = xaml.IndexOf('>', start);
        return xaml[start..end];
    }

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
