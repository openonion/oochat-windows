namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// What a tool <i>does</i>, reduced to the handful of categories the timeline draws differently.
///
/// <para>Deliberately not a FluentIcons <c>Icon</c>: this is the half of the mapping that is worth
/// testing (which tool reads as which kind of action) and it has to stay WinUI-free to live in
/// Core. The app project turns a kind into an actual glyph — see <c>ToolIconConverter</c> — the
/// same way <c>KeyNames</c> holds the platform-free key table while the layout translation stays
/// in the app.</para>
///
/// <para><see cref="Generic"/> is 0 so an unmapped or future tool degrades to the neutral wrench
/// rather than silently borrowing whichever category happened to be first.</para>
/// </summary>
public enum ToolIconKind
{
    Generic = 0,
    Browser,
    Search,
    FileRead,
    FileWrite,
    FileDelete,
    Folder,
    Terminal,
    Code,
    Mail,
    Image,
    Screenshot,
    Download,
    Upload,
    Database,
    Http,
    Ask,
    Click,
    Type,
}

/// <summary>
/// Maps a raw tool name to the <see cref="ToolIconKind"/> its timeline row should draw.
///
/// <para><b>Two passes, because tool names are not a controlled vocabulary.</b> Agents name the
/// same capability <c>write_file</c>, <c>remote_write_file</c>, or <c>fs.writeFile</c>, so an
/// exact table alone would leave most real tools on the fallback icon. The exact table runs first
/// and pins the tools the reference agents actually ship (it is also where a name whose keywords
/// would mislead gets its answer — <c>get_text</c> reads a *page*, not a file). Everything else
/// falls through to an ordered keyword scan.</para>
///
/// <para><b>The keyword order is the contract, not a style choice.</b> Real names carry several
/// matching words at once and the first hit wins, so the list runs most-identifying first:
/// <c>delete_file</c> is a deletion before it is a file, <c>download_file</c> is a download
/// before it is a file, and the bare <c>file</c> catch-all sits at the very bottom precisely
/// because almost every file tool also says what it is doing to the file. Reordering these pairs
/// silently changes what a tool draws.</para>
/// </summary>
public static class ToolIcons
{
    /// <summary>
    /// Keyword → kind, scanned in order; first substring hit wins. Substrings rather than whole
    /// words so <c>remote_write_file</c> and <c>writeFile</c> both land on the same row.
    ///
    /// <para>Every entry is at least four characters on purpose. Short fragments match by
    /// accident — <c>"ls"</c> for a directory listing also fires on <c>tools</c>, <c>labels</c>,
    /// and <c>results</c> — so abbreviations are handled by the exact table above instead.</para>
    /// </summary>
    private static readonly (string Keyword, ToolIconKind Kind)[] Keywords =
    {
        // Actions that own their name outright, whatever else it mentions.
        ("screenshot", ToolIconKind.Screenshot),
        ("download", ToolIconKind.Download),
        ("upload", ToolIconKind.Upload),
        ("delete", ToolIconKind.FileDelete),
        ("remove", ToolIconKind.FileDelete),
        ("unlink", ToolIconKind.FileDelete),
        ("email", ToolIconKind.Mail),
        ("mail", ToolIconKind.Mail),
        ("smtp", ToolIconKind.Mail),

        // The noun beats the verb: "search_database" is a database tool, and putting the store
        // first keeps every query dialect on one icon instead of splitting across Search.
        ("database", ToolIconKind.Database),
        ("sqlite", ToolIconKind.Database),
        ("mysql", ToolIconKind.Database),
        ("postgres", ToolIconKind.Database),
        ("mongo", ToolIconKind.Database),
        ("redis", ToolIconKind.Database),
        ("query", ToolIconKind.Database),

        ("browser", ToolIconKind.Browser),
        ("navigate", ToolIconKind.Browser),
        ("webpage", ToolIconKind.Browser),
        ("website", ToolIconKind.Browser),

        ("search", ToolIconKind.Search),
        ("grep", ToolIconKind.Search),
        ("lookup", ToolIconKind.Search),

        // Before the file verbs: "run_command" and "remote_bash" are shells that happen to touch
        // files, and reading them as file edits would hide the fact that arbitrary code ran.
        ("bash", ToolIconKind.Terminal),
        ("shell", ToolIconKind.Terminal),
        ("terminal", ToolIconKind.Terminal),
        ("command", ToolIconKind.Terminal),
        ("execute", ToolIconKind.Terminal),

        ("write", ToolIconKind.FileWrite),
        ("patch", ToolIconKind.FileWrite),
        ("append", ToolIconKind.FileWrite),

        ("read", ToolIconKind.FileRead),

        ("folder", ToolIconKind.Folder),
        ("directory", ToolIconKind.Folder),
        ("glob", ToolIconKind.Folder),

        ("image", ToolIconKind.Image),
        ("photo", ToolIconKind.Image),
        ("picture", ToolIconKind.Image),

        ("click", ToolIconKind.Click),
        ("keyboard", ToolIconKind.Type),
        ("type", ToolIconKind.Type),
        ("input", ToolIconKind.Type),

        ("question", ToolIconKind.Ask),
        ("confirm", ToolIconKind.Ask),
        ("prompt", ToolIconKind.Ask),

        ("http", ToolIconKind.Http),
        ("fetch", ToolIconKind.Http),
        ("request", ToolIconKind.Http),
        ("curl", ToolIconKind.Http),

        ("python", ToolIconKind.Code),
        ("script", ToolIconKind.Code),
        ("compile", ToolIconKind.Code),
        ("code", ToolIconKind.Code),

        // Last resort: a tool that only says "file" and nothing about what it does with one.
        ("file", ToolIconKind.FileRead),
    };

    /// <summary>The kind for a tool name. Never throws and never returns anything but a defined
    /// member — an unrecognised tool is <see cref="ToolIconKind.Generic"/>.</summary>
    public static ToolIconKind ForTool(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return ToolIconKind.Generic;

        var name = toolName.ToLowerInvariant();

        // The reference agents' own tools, pinned so a keyword rule can never quietly re-point
        // one of them. `get_text` is the reason this pass exists at all: it reads the open page,
        // so the file-ish reading of its name would be wrong.
        switch (name)
        {
            case "open_browser" or "go_to" or "navigate" or "get_text": return ToolIconKind.Browser;
            case "click": return ToolIconKind.Click;
            case "type_text" or "type": return ToolIconKind.Type;
            case "take_screenshot": return ToolIconKind.Screenshot;
            case "search_web" or "search": return ToolIconKind.Search;
            case "read_file": return ToolIconKind.FileRead;
            case "write_file": return ToolIconKind.FileWrite;
            case "delete_file": return ToolIconKind.FileDelete;
            case "send_email": return ToolIconKind.Mail;
            case "run_command" or "execute_command": return ToolIconKind.Terminal;
            case "upload_file": return ToolIconKind.Upload;
            case "download_file": return ToolIconKind.Download;
            case "ask_user": return ToolIconKind.Ask;
            case "ls" or "dir" or "list_dir": return ToolIconKind.Folder;
            case "cat": return ToolIconKind.FileRead;
        }

        foreach (var (keyword, kind) in Keywords)
        {
            if (name.Contains(keyword, StringComparison.Ordinal)) return kind;
        }

        return ToolIconKind.Generic;
    }
}
