using System.Xml.Linq;

namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

/// <summary>
/// Guards against the "invisible text" class of bug.
///
/// <para><c>UserControl</c> has no default style, so <c>Control.Foreground</c> is <b>null</b>
/// unless a caller sets it. Forwarding that null to an inner <c>TextBlock</c>/<c>RichTextBlock</c>
/// does not mean "use the default" — a null <c>Brush</c> means do not paint. Every markdown body
/// outside <c>ChatPage</c> (the plan card, the plan section dialog, the tool log) rendered
/// completely blank because of this, and the symptom pointed nowhere near the cause: the text
/// appeared only while selected, since the selection layer draws its own foreground, and inline
/// code stayed readable throughout because the renderer gives it an explicit brush.</para>
/// </summary>
public sealed class TextControlForegroundTests
{
    /// <summary>Controls that wrap a text element and forward the outer Foreground to it.</summary>
    private static readonly string[] ForegroundForwardingControls =
    [
        "MarkdownTextBlock",
        "HighlightedTextBlock",
    ];

    [Fact]
    public void ForegroundForwardingControls_NeverPassNullThrough()
    {
        foreach (var control in ForegroundForwardingControls)
        {
            var source = ReadPrimitive(control);

            // A binding forwards null verbatim; the sync method is what applies the fallback.
            Assert.DoesNotContain(
                "ForegroundProperty, new Binding",
                source,
                StringComparison.Ordinal);
            Assert.Contains("SyncForeground", source, StringComparison.Ordinal);
            Assert.Contains(
                "Foreground ?? Presentation.ThemeBrushResolver.Resolve(\"TextPrimaryBrush\")",
                source,
                StringComparison.Ordinal);

            // The fallback is resolved, not captured once, so a live theme flip moves it.
            Assert.Contains("ActualThemeChanged", source, StringComparison.Ordinal);
        }
    }

    /// <summary>The renderer copies the target's Foreground onto each table cell. That copy is
    /// only safe while the target itself can never be null — which is what the sync above
    /// guarantees. Recorded here so the two stay connected.</summary>
    [Fact]
    public void MarkdownRenderer_CopiesTargetForegroundForTableCells()
    {
        var source = ReadAppSource("Rendering", "WinUiMarkdownRenderer.cs");

        Assert.Contains("Foreground = _target.Foreground,", source, StringComparison.Ordinal);
    }

    private static string ReadPrimitive(string control)
        => ReadAppSource("Controls", "Primitives", control + ".cs");

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

/// <summary>
/// The interactive cards' fold/unfold contract.
/// </summary>
public sealed class InteractiveCardDisclosureTests
{
    /// <summary>Folding is per-message, not per-control.
    ///
    /// <para>These cards live in a virtualized <c>ListView</c>, so their containers are recycled
    /// onto other messages while scrolling. Held as control state, folding one card would fold
    /// whichever unrelated card later reused that container — the same reason
    /// <c>ToolActivityViewModel</c> owns its own <c>IsExpanded</c>.</para></summary>
    [Fact]
    public void FoldState_LivesOnTheMessage()
    {
        var model = ReadCoreSource("Models", "ChatMessage.InteractiveCards.cs");

        Assert.Contains("IsInteractiveCardExpanded", model, StringComparison.Ordinal);
        // A session view preference, not part of what a conversation replays as.
        var declaration = model[model.IndexOf(
            "public partial bool IsInteractiveCardExpanded", StringComparison.Ordinal)..];
        var precedingAttributes = model[..model.IndexOf(
            "public partial bool IsInteractiveCardExpanded", StringComparison.Ordinal)];
        Assert.Contains("[JsonIgnore]", precedingAttributes[^200..], StringComparison.Ordinal);
        Assert.NotEmpty(declaration);
    }

    /// <summary>Cards default to unfolded: one that appeared collapsed would hide a question the
    /// agent is currently blocked on.</summary>
    [Fact]
    public void Cards_DefaultToUnfolded()
    {
        var model = ReadCoreSource("Models", "ChatMessage.cs");

        Assert.Contains("IsInteractiveCardExpanded = true;", model, StringComparison.Ordinal);
    }

    /// <summary>Plan and ask_user fold from the header; the diff card deliberately does not,
    /// because it already owns a "View changes"/"Hide changes" toggle over the same content and
    /// two disclosures on one card would let the header hide the button that unfolds it.</summary>
    [Fact]
    public void OnlyCardsWithoutTheirOwnDisclosure_BindTheHeaderToggle()
    {
        Assert.Contains(
            "IsInteractiveCardExpanded",
            ReadCardXaml("PlanReviewCard"),
            StringComparison.Ordinal);
        Assert.Contains(
            "IsInteractiveCardExpanded",
            ReadCardXaml("AskUserCard"),
            StringComparison.Ordinal);

        var diff = ReadCardXaml("DiffPreviewCard");
        Assert.DoesNotContain(
            "IsExpanded=\"{x:Bind Message.IsInteractiveCardExpanded",
            diff,
            StringComparison.Ordinal);
        Assert.Contains("AllowHeaderDisclosure=\"False\"", diff, StringComparison.Ordinal);
        Assert.Contains("Text=\"{x:Bind DisplayLineNumber}\"", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{x:Bind OldLineNumber}\"", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{x:Bind NewLineNumber}\"", diff, StringComparison.Ordinal);
    }

    /// <summary>The header is the disclosure control, and the body is gated on both facts: the
    /// card having a body at all (<c>ShowBody</c>) and the user not having folded it
    /// (<c>IsExpanded</c>). The footer is deliberately not gated — folding a plan you are still
    /// being asked about must not also hide Approve/Reject.</summary>
    [Fact]
    public void Header_TogglesTheBodyButNeverTheFooter()
    {
        var xaml = XDocument.Parse(ReadCardXaml("InteractiveCard"));
        var text = xaml.ToString();

        Assert.Contains("IsBodyVisible", text, StringComparison.Ordinal);
        Assert.Contains("HeaderToggle_Click", text, StringComparison.Ordinal);
        // The footer still keys off ShowFooter alone.
        Assert.Contains("x:Bind ShowFooter", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IsBodyVisible, Mode=OneWay, Converter={StaticResource BoolToVis}}\" x:Name=\"Footer",
            text, StringComparison.Ordinal);

        var code = ReadAppSource(
            "Controls", "Chat", "InteractiveCards", "InteractiveCard.xaml.cs");
        Assert.Contains(
            "IsBodyVisible = ShowBody && (!AllowHeaderDisclosure || IsExpanded);",
            code,
            StringComparison.Ordinal);
        // No body, or a card-owned footer disclosure, means no header affordance.
        Assert.Contains(
            "CanCollapse = ShowBody && AllowHeaderDisclosure;",
            code,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_OwnsPendingApprovalAndCompletedPlanRemainsInspectable()
    {
        var chatPage = ReadAppSource("Views", "ChatPage.xaml");
        Assert.Contains("x:Load=\"{x:Bind ShowRelatedDiffApprovalCard, Mode=OneWay}\"",
            chatPage, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding IsTranscriptRowVisible, Converter={StaticResource BoolToVis}}\"",
            chatPage, StringComparison.Ordinal);

        var ask = ReadCardXaml("AskUserCard");
        Assert.Contains("Message.ShowAskUserSkipAction", ask, StringComparison.Ordinal);

        var plan = ReadCardXaml("PlanReviewCard");
        var reviewButton = XDocument.Parse(plan).Descendants().Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == "ReviewSectionsButton"));
        Assert.DoesNotContain(reviewButton.Attributes(), attribute =>
            attribute.Name.LocalName == "IsEnabled");

        var dialog = ReadAppSource(
            "Controls", "Chat", "InteractiveCards", "PlanSectionReviewDialog.xaml");
        Assert.Contains("IsReadOnly", dialog, StringComparison.Ordinal);
    }

    private static string ReadCardXaml(string name)
        => ReadAppSource("Controls", "Chat", "InteractiveCards", name + ".xaml");

    private static string ReadCoreSource(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var root = Path.Combine(directory.FullName, "ConnectOnion.WinUIClient.Core");
            if (Directory.Exists(root))
                return File.ReadAllText(Path.Combine([root, .. relativeParts]));
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Core source directory.");
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
