using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

public sealed class ToolIconsTests
{
    /// <summary>The reference agents' own tools, pinned by the exact table.</summary>
    [Theory]
    [InlineData("open_browser", ToolIconKind.Browser)]
    [InlineData("go_to", ToolIconKind.Browser)]
    [InlineData("click", ToolIconKind.Click)]
    [InlineData("type_text", ToolIconKind.Type)]
    [InlineData("take_screenshot", ToolIconKind.Screenshot)]
    [InlineData("search_web", ToolIconKind.Search)]
    [InlineData("read_file", ToolIconKind.FileRead)]
    [InlineData("write_file", ToolIconKind.FileWrite)]
    [InlineData("delete_file", ToolIconKind.FileDelete)]
    [InlineData("send_email", ToolIconKind.Mail)]
    [InlineData("run_command", ToolIconKind.Terminal)]
    [InlineData("upload_file", ToolIconKind.Upload)]
    [InlineData("download_file", ToolIconKind.Download)]
    [InlineData("ask_user", ToolIconKind.Ask)]
    public void KnownTools_MapExactly(string tool, ToolIconKind expected)
        => Assert.Equal(expected, ToolIcons.ForTool(tool));

    /// <summary><c>get_text</c> reads the open page, not a file — the one name in the exact table
    /// whose keywords would otherwise mislead.</summary>
    [Fact]
    public void GetText_ReadsAsBrowser_NotAFile()
        => Assert.Equal(ToolIconKind.Browser, ToolIcons.ForTool("get_text"));

    /// <summary>The tools this project's own agent actually calls — none of which the exact table
    /// lists, so they prove the keyword scan carries the real workload.</summary>
    [Theory]
    [InlineData("remote_write_file", ToolIconKind.FileWrite)]
    [InlineData("remote_read_file", ToolIconKind.FileRead)]
    [InlineData("remote_bash", ToolIconKind.Terminal)]
    public void RemotePrefixedTools_StillResolve(string tool, ToolIconKind expected)
        => Assert.Equal(expected, ToolIcons.ForTool(tool));

    /// <summary>
    /// The keyword order is the contract: each of these names carries two or more matching words,
    /// and the more identifying one has to win. A reordering of the table breaks exactly here.
    /// </summary>
    [Theory]
    [InlineData("delete_file", ToolIconKind.FileDelete)]      // deletion beats "file"
    [InlineData("download_file", ToolIconKind.Download)]      // transfer beats "file"
    [InlineData("upload_file", ToolIconKind.Upload)]
    [InlineData("write_file", ToolIconKind.FileWrite)]        // the verb beats "file"
    [InlineData("bash_write_file", ToolIconKind.Terminal)]    // a shell is a shell, not an edit
    [InlineData("search_database", ToolIconKind.Database)]    // the store beats the verb
    [InlineData("screenshot_page", ToolIconKind.Screenshot)]  // beats "browser"-ish wording
    public void AmbiguousNames_ResolveToTheMoreIdentifyingWord(string tool, ToolIconKind expected)
        => Assert.Equal(expected, ToolIcons.ForTool(tool));

    /// <summary>Names vary in case and separator across agents; the same capability must not
    /// split across two icons because one agent spells it in camelCase.</summary>
    [Theory]
    [InlineData("writeFile")]
    [InlineData("WriteFile")]
    [InlineData("fs.writeFile")]
    public void Spelling_DoesNotChangeTheIcon(string tool)
        => Assert.Equal(ToolIconKind.FileWrite, ToolIcons.ForTool(tool));

    /// <summary>An unmapped tool degrades to the neutral wrench rather than borrowing a category
    /// it does not belong to.</summary>
    [Theory]
    [InlineData("frobnicate")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void UnknownOrEmpty_IsGeneric(string? tool)
        => Assert.Equal(ToolIconKind.Generic, ToolIcons.ForTool(tool));

    /// <summary>
    /// The guard on short keywords. "ls" and "dir" are real directory commands, but as substrings
    /// they fire on ordinary words — so they live in the exact table only, and a tool merely
    /// *containing* those letters must not be drawn as a folder.
    /// </summary>
    [Theory]
    [InlineData("tools_list")]
    [InlineData("get_labels")]
    [InlineData("fetch_results")]
    public void ShortFragments_DoNotMatchByAccident(string tool)
        => Assert.NotEqual(ToolIconKind.Folder, ToolIcons.ForTool(tool));

    /// <summary>Exact-table directory commands still resolve, which is why the fragments above
    /// can safely stay out of the substring scan.</summary>
    [Theory]
    [InlineData("ls")]
    [InlineData("dir")]
    [InlineData("list_dir")]
    public void DirectoryCommands_ResolveThroughTheExactTable(string tool)
        => Assert.Equal(ToolIconKind.Folder, ToolIcons.ForTool(tool));

    /// <summary>The step model derives its icon from the persisted tool name, so a conversation
    /// saved before icons existed draws them on reopen with no migration.</summary>
    [Fact]
    public void StepModel_DerivesIconFromItsToolName()
    {
        var step = new ToolStepViewModel { ToolName = "remote_bash" };

        Assert.Equal(ToolIconKind.Terminal, step.IconKind);
    }
}
