namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

/// <summary>
/// The composer's send button is computed imperatively by <c>RefreshSendButtonState</c> from data
/// that is observable, but it does not observe it: the only hook is
/// <c>PendingAttachments.CollectionChanged</c>, and nothing subscribes to an individual
/// <c>PendingAttachment.PropertyChanged</c>. So an attachment whose <c>Status</c> settles *after*
/// it joins the collection leaves the button reading stale state.
///
/// <para>That shipped: <c>AddAttachments</c> added first and set <c>Status = Ready</c> afterwards,
/// so attaching a file with an empty draft left <c>hasReadyAttachment</c> false and the send button
/// disabled until the user typed a character. Enter still submitted, because
/// <c>SubmitCurrentText</c> re-reads the collection instead of trusting the button — which is
/// exactly what made it look like a drawing bug rather than stale state.</para>
///
/// <para>These are source-text assertions because the subject is a WinUI <c>UserControl</c>: a
/// headless host cannot load the Windows App SDK, and the FlaUI suite runs the app out of process
/// with no file picker it can drive. See <c>docs/TEST_PLAN.md</c> §4 on the automation boundary.</para>
/// </summary>
public sealed class ComposerAttachmentSendContractTests
{
    [Fact]
    public void AddAttachments_SettlesStatusBeforeJoiningTheCollection()
    {
        var body = ComposerMethod("private void AddAttachments(", "// ---- Drag and drop ----");

        var statusAssignment = body.IndexOf("attachment.Status =", StringComparison.Ordinal);
        var collectionAdd = body.IndexOf("PendingAttachments.Add(attachment)", StringComparison.Ordinal);

        Assert.True(statusAssignment >= 0, "AddAttachments must set the attachment's status.");
        Assert.True(collectionAdd >= 0, "AddAttachments must add the attachment to the rail.");
        Assert.True(
            statusAssignment < collectionAdd,
            "Status must be settled before the Add. CollectionChanged is the only thing that "
            + "recomputes the send button, and it runs during the Add — so adding first publishes "
            + "a Pending attachment and the button never learns it became Ready.");
    }

    [Fact]
    public void AddAttachments_RefreshesTheSendButtonBeforeReturning()
    {
        var body = ComposerMethod("private void AddAttachments(", "// ---- Drag and drop ----");

        // Order-independent guarantee, kept alongside the ordering rule above rather than instead
        // of it: neither caller (the file picker and the drop handler) refreshes on its own.
        Assert.Contains("RefreshSendButtonState()", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The paired half. <c>SubmitCurrentText</c> must keep deciding from the collection rather than
    /// from the button's cached verdict — the button is a hint about what is possible, not the
    /// authority on it, and a send path that trusted it would have been broken by the same bug
    /// instead of merely looking odd next to it.
    /// </summary>
    [Fact]
    public void SubmitCurrentText_DecidesFromTheCollectionNotTheButton()
    {
        var body = ComposerMethod("private void SubmitCurrentText()", "private void RefreshSendButtonState()");

        Assert.Contains(
            "PendingAttachments.Where(a => a.Status == AttachmentStatus.Ready)",
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SendButton.IsEnabled", body, StringComparison.Ordinal);
    }

    /// <summary>An attachment-only message is a real message. Both gates that can refuse a send
    /// must accept an empty draft when something is attached.</summary>
    [Fact]
    public void EmptyDraftWithAReadyAttachment_IsSendable()
    {
        var composer = ReadAppSource("Controls", "Chat", "ChatComposer.xaml.cs");
        Assert.Contains(
            "(!string.IsNullOrWhiteSpace(InputBox.Text) || hasReadyAttachment)",
            composer,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (text.Length == 0 && readyAttachments.Count == 0) return;",
            composer,
            StringComparison.Ordinal);

        // And the view model behind it, which Retry and edit-and-resend also reach.
        var viewModel = ReadAppSource("ViewModels", "ChatViewModel.Run.cs");
        Assert.Contains("(text.Length == 0 && !hasAttachments)", viewModel, StringComparison.Ordinal);
    }

    private static string ComposerMethod(string signature, string terminator)
    {
        var source = ReadAppSource("Controls", "Chat", "ChatComposer.xaml.cs");
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not found in ChatComposer.");
        var end = source.IndexOf(terminator, start, StringComparison.Ordinal);
        Assert.True(end > start, $"'{terminator}' was not found after '{signature}'.");
        return source[start..end];
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
