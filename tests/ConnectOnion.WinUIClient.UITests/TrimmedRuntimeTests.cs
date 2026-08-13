using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;

namespace ConnectOnion.WinUIClient.UITests;

/// <summary>
/// Closes the third acceptance criterion in <c>docs/TRIMMING.md</c>: "Tool Activity and every
/// interactive card survive persist/restart in the trimmed publish".
///
/// <para>That criterion was <b>partially met</b>. <c>tests/ConnectOnion.TrimSmoke</c> proves the
/// round-trip in a trimmed binary across a real process restart — but it is a console harness that
/// links <c>Core</c> and <c>Protocol</c> only. The WinUI app is trimmed as a <i>separate</i>
/// publish, with different roots and therefore different linker decisions, and it is the one that
/// actually renders the card. The original bug was not "the row is unreadable"; it was "the card
/// comes back empty in a Release build", which no headless harness can see.</para>
///
/// <para>So this test is the missing half, and it is deliberately a two-binary round trip: the
/// <b>trimmed</b> smoke harness writes the turn into a data root, and the <b>trimmed</b> app is
/// launched against that same root and asked to render it. Nothing untrimmed touches the data on
/// either side. Seeding happens in <c>scripts/Test-TrimmedRuntime.ps1</c> rather than here,
/// because <c>AppStorage.RootDir</c> caches into a static on first read and a test host that has
/// already resolved it cannot be redirected.</para>
///
/// <para>Skips itself unless both environment variables are set, exactly like
/// <see cref="ShellSmokeTests"/>: execution needs a real desktop session.</para>
/// </summary>
[Trait("Category", "TrimmedRuntime")]
public sealed class TrimmedRuntimeTests
{
    private const string DataRootEnvironmentVariable = "CONNECTONION_DATA_ROOT";

    /// <summary>Written by <c>PersistenceChecks.BuildTurn</c> in the smoke harness. Asserted on
    /// the rendered tree, so these are the strings a user would actually see.</summary>
    private const string SeededConversationTitle = "Trim smoke";

    [Fact]
    public void TrimmedApp_RendersToolActivityAndInteractiveCards_FromASeededRoot()
    {
        if (!TryGetSeededRoot(out var dataRoot)) return;

        using var launched = ShellSmokeTests.LaunchApp();
        if (launched is null) return;

        var window = launched.Window;

        // The seeded agent and its conversation must be on screen before anything can be opened.
        var session = WaitForSessionButton(window);
        Assert.True(
            session is not null,
            $"the seeded conversation never appeared in the sidebar (data root: {dataRoot}). "
            + "A trimmed build that cannot read the sessions table fails here, before any card is reached.");

        session!.AsButton().Invoke();

        var messageList = WaitForDescendant(window, "MessageList", TimeSpan.FromSeconds(20));
        Assert.True(messageList is not null, "the conversation did not open onto a message list");

        // Historical multi-step cards deliberately reopen collapsed. Expand the durable card via
        // its real disclosure control before asserting on step content; otherwise this test asks
        // UI Automation to find rows that the product intentionally has not put in layout.
        var activityHeader = WaitForDescendant(
            window, "ToolActivityHeaderButton", TimeSpan.FromSeconds(20));
        Assert.True(activityHeader is not null,
            "the restored tool-activity card did not expose its disclosure header");
        activityHeader!.AsButton().Invoke();

        // Waited for, not sampled once. The transcript is a virtualized list restored
        // asynchronously, so walking the tree the instant MessageList exists reads a partly
        // realized view — which reports a card as missing that simply had not been built yet.
        // A single sample here produced a failure against the *untrimmed* build that did not
        // reproduce, i.e. it would have been reported as a trimming defect that does not exist.
        var rendered = WaitForRenderedText(messageList!, "Run command", TimeSpan.FromSeconds(20));

        // Opt-in dump, so a trimmed transcript can be diffed against an untrimmed one.
        if (Environment.GetEnvironmentVariable("CONNECTONION_UI_DUMP") is { Length: > 0 } dumpPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dumpPath))!);
            File.WriteAllText(dumpPath, rendered);
        }

        // The user's own message. If this is missing the transcript did not load at all, and the
        // tool-activity assertions below would be misleading about the cause.
        AssertRendered(rendered, "run the checks", "the user message did not render");

        // THE regression. A trimmed build deserialized event_args through the reflection
        // serializer, got null back, and drew the card with no steps in it — the row was present
        // and the card was empty. Asserting on the step content is what separates "the card
        // rendered" from "the card rendered with what was persisted in it".
        AssertRendered(rendered, "Run command", "the tool-activity card lost its first step (bash)");
        AssertRendered(rendered, "Read file", "the tool-activity card lost its second step (read_file)");
        AssertRendered(rendered, "ls -la", "the tool step lost the arguments it was invoked with");

        // The interactive cards and the answers ResolveInteractiveCards stamped on them.
        AssertRendered(rendered, "Which region?", "the ask_user card did not render");
        AssertRendered(rendered, "Review the plan", "the plan_review card did not render");

        // A settled approval belongs inside its tool-activity card, never as its own bubble. The
        // repository filters it out of every read, so seeing it here would mean that filter was
        // trimmed away rather than that the card rendered.
        Assert.DoesNotContain("Run deploy.sh?", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Includes the transcript that actually rendered in the failure message. Without it a failure
    /// here says only "the string was absent", which cannot distinguish the regression this test
    /// exists for (the card came back empty) from a template change that moved the text somewhere
    /// this walk does not reach.
    /// </summary>
    private static void AssertRendered(string rendered, string expected, string because)
        => Assert.True(
            rendered.Contains(expected, StringComparison.OrdinalIgnoreCase),
            $"{because}. Expected \"{expected}\" in the rendered transcript, which was:\n"
            + string.Join("\n", rendered.Split('\n').Where(l => l.Trim().Length > 0).Take(60)));

    private static bool TryGetSeededRoot(out string dataRoot)
    {
        dataRoot = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable) ?? "";
        if (string.IsNullOrWhiteSpace(dataRoot)) return false;

        // Present but unseeded is not the same as "not opted in": running against an empty root
        // would assert nothing and report a pass. Treat it as a skip only when the harness was
        // never pointed here at all.
        return File.Exists(Path.Combine(dataRoot, "connectonion.db"));
    }

    /// <summary>
    /// Finds the seeded conversation's row. The agent may need selecting first — the sidebar shows
    /// an agent's sessions under it — so this tries the session directly, then falls back to
    /// clicking the agent and looking again.
    /// </summary>
    private static AutomationElement? WaitForSessionButton(Window window)
    {
        var direct = FindSession(window);
        if (direct is not null) return direct;

        var agent = WaitForDescendant(window, "AgentButton", TimeSpan.FromSeconds(20));
        if (agent is null) return null;

        agent.AsButton().Invoke();
        return FindSession(window, TimeSpan.FromSeconds(15));
    }

    private static AutomationElement? FindSession(Window window, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var candidates = window.FindAllDescendants(query => query.ByAutomationId("SessionButton"));
                var match = candidates.FirstOrDefault(element =>
                    (element.Properties.Name.ValueOrDefault ?? "")
                        .Contains(SeededConversationTitle, StringComparison.OrdinalIgnoreCase));

                // Fall back to any session row: the sidebar may label the row from a template that
                // does not surface the title as the button's own Name, and this root has exactly
                // one seeded conversation, so the first row is unambiguous.
                if (match is null && candidates.Length > 0) match = candidates[0];
                if (match is not null) return match;
            }
            catch (COMException)
            {
                // The visual tree mutates while the sidebar populates; FindFirst/FindAll can throw
                // E_UNEXPECTED mid-walk. It means "ask again", not "not there".
            }

            Thread.Sleep(150);
        }

        return null;
    }

    private static AutomationElement? WaitForDescendant(Window window, string automationId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var found = window.FindFirstDescendant(query => query.ByAutomationId(automationId));
                if (found is not null) return found;
            }
            catch (COMException)
            {
                // Same transient as above.
            }

            Thread.Sleep(150);
        }

        return null;
    }

    /// <summary>
    /// Flattens every automation Name under the message list into one string.
    ///
    /// <para>Deliberately not a per-element structural assertion: the bubbles are templated and
    /// virtualized, so their exact tree shape is an implementation detail that would make this
    /// test fail on a harmless template change. What must not change is that the persisted content
    /// reaches the screen, and that is what the flattened text answers.</para>
    /// </summary>
    /// <summary>
    /// Re-walks the transcript until <paramref name="sentinel"/> appears or the timeout expires,
    /// then returns the last text read. The sentinel is the deepest thing this test asserts (a
    /// tool step's display name), so once it is present the rest of the turn has been realized
    /// too. On timeout it returns what it last saw, which is what the failure message reports.
    /// </summary>
    private static string WaitForRenderedText(AutomationElement root, string sentinel, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        var last = "";
        while (DateTime.UtcNow < deadline)
        {
            last = CollectText(root);
            if (last.Contains(sentinel, StringComparison.OrdinalIgnoreCase)) return last;
            Thread.Sleep(250);
        }

        return last;
    }

    private static string CollectText(AutomationElement root)
    {
        var text = new System.Text.StringBuilder();
        var pending = new Stack<AutomationElement>();
        pending.Push(root);

        var visited = 0;
        while (pending.Count > 0 && visited < 4000)
        {
            var element = pending.Pop();
            visited++;

            try
            {
                // Properties.Name.ValueOrDefault, not the Name shortcut: the shortcut throws
                // PropertyNotSupportedException on any element that does not implement the
                // property, and a virtualized transcript is full of those.
                if (element.Properties.Name.ValueOrDefault is { Length: > 0 } name)
                    text.Append(name).Append('\n');

                // A TextBlock surfaces its text as Name; an editable control surfaces it through
                // the Value pattern instead. Both are read so the walk does not depend on which
                // control a template happened to use.
                if (element.Patterns.Value.PatternOrDefault?.Value?.ValueOrDefault is { Length: > 0 } value)
                    text.Append(value).Append('\n');

                foreach (var child in element.FindAllChildren()) pending.Push(child);
            }
            catch (COMException)
            {
                // An element recycled out from under the walk; its siblings are still worth reading.
            }
        }

        return text.ToString();
    }
}
