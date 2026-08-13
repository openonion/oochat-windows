namespace ConnectOnion.WinUIClient.Views;

/// <summary>
/// Implemented by any page that supports in-content search (Ctrl+F).
///
/// There is exactly one find UI, and it lives on <c>MainWindow</c>
/// (<c>MainWindow.ViewMenu.cs</c>): the window attaches to whatever <c>ContentFrame.Content</c>
/// currently is, and drives it through this interface. So making a new page searchable means
/// implementing <c>IFindHost</c> on it — <b>not</b> building a page-local search box, which
/// would give the app two find bars with two different keybindings and two different looks.
///
/// The window owns the query text and the overlay; the page owns the matching, the
/// highlighting, and which match is current.
/// </summary>
public interface IFindHost
{
    /// <summary>Raised when the match count or current match changes, so the window can
    /// refresh <see cref="FindStatusText"/> and the next/previous buttons. The page decides
    /// when this fires — it is the only way the window learns that a search finished, since
    /// matching may be debounced or run off the keystroke.</summary>
    event System.EventHandler? FindStateChanged;

    /// <summary>Human-readable position, e.g. "3 of 12" or "No results" — rendered as-is in
    /// the find bar, so the page controls the wording for its own content.</summary>
    string FindStatusText { get; }

    /// <summary>Whether next/previous can do anything; the window uses it to enable or
    /// disable those buttons.</summary>
    bool HasFindMatches { get; }

    /// <summary>Ctrl+F arrived while this page is showing. The page prepares its own state
    /// (scroll position, existing highlights); the window shows the overlay and takes focus.</summary>
    void OpenFind();

    /// <summary>Esc, or navigation away. The page must drop its highlights here — they are
    /// page state, and a page that keeps them will show stale matches when reopened.</summary>
    void CloseFind();

    /// <summary>The query changed. Called on every (debounced) keystroke, so this is the hot
    /// path: it must be cheap enough to run per character over the whole page content.</summary>
    void SetFindQuery(string query);

    void SelectNextFindMatch();

    void SelectPreviousFindMatch();
}
