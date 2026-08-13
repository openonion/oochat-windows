namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

/// <summary>
/// Pins the two halves of "clicking the sidebar's blank area selects the first agent".
///
/// <para>An agent row has no accent rail — <c>RowStateItem.ShowRowBackground</c> is its whole
/// selected appearance — so any path that arms a row's interactive state without the user
/// choosing it reads as a selection that never happened.</para>
/// </summary>
public sealed class SidebarRowFocusContractTests
{
    [Fact]
    public void Sidebar_IsNotItselfAFocusTarget()
    {
        var xaml = ReadAppSource("Controls", "Shell", "ShellSidebar.xaml");
        var rootEnd = xaml.IndexOf('>', xaml.IndexOf("<UserControl", StringComparison.Ordinal));
        var rootTag = xaml[..rootEnd];

        // Control.IsTabStop defaults to true and a UserControl has no template, so without this
        // the control resolves focus into its first focusable descendant when the hit-testable
        // root Grid is clicked.
        Assert.Contains("IsTabStop=\"False\"", rootTag, StringComparison.Ordinal);
    }

    [Fact]
    public void RowFocusWash_IsArmedByKeyboardTraversalOnly()
    {
        var source = ReadAppSource("Controls", "Shell", "ShellSidebar.Events.cs");
        var start = source.IndexOf("private void Row_GotFocus", StringComparison.Ordinal);
        Assert.True(start >= 0, "Row_GotFocus must exist.");
        var body = source[start..source.IndexOf("private void Row_LostFocus", start, StringComparison.Ordinal)];

        Assert.Contains("FocusState.Keyboard", body, StringComparison.Ordinal);
        Assert.Contains("return", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PointerHover_StillArmsTheRowWithoutAFocusCheck()
    {
        // The keyboard gate above is only safe because the pointer path is untouched: a mouse
        // user's wash and action buttons come from PointerEntered, never from focus.
        var source = ReadAppSource("Controls", "Shell", "ShellSidebar.Events.cs");
        var start = source.IndexOf("private void Row_PointerEntered", StringComparison.Ordinal);
        var body = source[start..source.IndexOf("private void Row_PointerExited", start, StringComparison.Ordinal)];

        Assert.Contains("SetRowInteractive(sender, true)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("FocusState", body, StringComparison.Ordinal);
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
