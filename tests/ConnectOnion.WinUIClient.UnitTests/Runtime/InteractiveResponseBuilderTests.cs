using System.Text.Json;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Runtime;

public sealed class InteractiveResponseBuilderTests
{
    [Fact]
    public void AskUser_SingleSelect_SubmitsSelectedValue()
    {
        var message = Ask(options: ["Agents", "Logs"]);
        message.AskUserOptionEntries[1].Toggle();
        Assert.Equal("Logs", InteractiveResponseBuilder.BuildAskUserAnswer(message));
    }

    [Fact]
    public void AskUser_MultiSelect_SubmitsAllSelectedValues()
    {
        var message = Ask(true, "Agents", "Logs", "Status");
        message.AskUserOptionEntries[0].Toggle();
        message.AskUserOptionEntries[2].Toggle();
        Assert.Equal(["Agents", "Status"], Assert.IsType<string[]>(
            InteractiveResponseBuilder.BuildAskUserAnswer(message)));
    }

    [Fact]
    public void AskUser_WithOptions_HidesUnspecifiedFreeTextAndOnlyMultiShowsCount()
    {
        var single = Ask(options: ["Yes", "No"]);
        var multi = Ask(true, "A", "B");

        Assert.False(single.ShowAskUserFreeText);
        Assert.False(single.ShowAskUserSelectionSummary);
        Assert.True(multi.ShowAskUserSelectionSummary);
    }

    [Fact]
    public void FileChangeApproval_HidesAmbiguousSkipAction()
    {
        var approval = Ask(options: ["Yes, apply this change", "No, reject and give feedback"]);
        approval.EventTitle = "Apply changes to src/app.py?";

        Assert.True(approval.ShowInteractiveActions);
        Assert.False(approval.ShowAskUserSkipAction);
        Assert.True(Ask(options: ["Continue", "Stop"]).ShowAskUserSkipAction);
    }

    [Fact]
    public void AskUser_FreeText_TrimsAndSubmitsText()
    {
        var message = Ask();
        message.AskUserFreeText = "  active in the last hour  ";
        Assert.Equal("active in the last hour", InteractiveResponseBuilder.BuildAskUserAnswer(message));
    }

    [Fact]
    public void AskUser_Form_UsesDocumentedJsonObjectAnswer()
    {
        var message = Ask();
        message.AskUserFields.Add(Field(message, "host", "Host", true, " server-1 "));
        message.AskUserFields.Add(Field(message, "note", "Note", false, " blue "));

        using var json = JsonDocument.Parse(Assert.IsType<string>(
            InteractiveResponseBuilder.BuildAskUserAnswer(message)));
        Assert.Equal("server-1", json.RootElement.GetProperty("host").GetString());
        Assert.Equal("blue", json.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public void AskUser_RequiredField_BlocksSubmissionAndShowsFieldError()
    {
        var message = Ask();
        message.AskUserFields.Add(Field(message, "host", "Host", true, ""));
        Assert.False(message.ValidateAskUser());
        Assert.False(message.CanSubmitAskUser);
        Assert.Equal("Host is required.", message.AskUserFields[0].ValidationError);
    }

    [Fact]
    public void InteractiveSubmit_LocksDuplicateAndBecomesReadOnlyAfterSuccess()
    {
        var message = Ask(options: ["Continue"]);
        message.AskUserOptionEntries[0].Toggle();
        Assert.True(message.TryBeginInteractiveSubmit());
        Assert.False(message.TryBeginInteractiveSubmit());
        message.CompleteInteractiveSubmit("Continue");
        Assert.False(message.IsInteractiveEditable);
        Assert.True(message.ShowInteractiveResult);
        Assert.Equal("Submitted", message.InteractiveStateLabel);
    }

    [Fact]
    public void ConnectionLost_PreservesDraftAndDisablesSubmitUntilRevalidated()
    {
        var message = Ask();
        message.AskUserFreeText = "do not lose this";
        message.MarkInteractiveConnectionLost();
        Assert.Equal("do not lose this", message.AskUserFreeText);
        Assert.False(message.CanSubmitAskUser);
        Assert.Equal("Connection lost", message.InteractiveStateLabel);

        message.RevalidateInteractiveRequest();
        Assert.True(message.CanSubmitAskUser);
        Assert.Equal("do not lose this", message.AskUserFreeText);
    }

    [Fact]
    public void LoadedSkippedQuestion_DoesNotClaimAResponseWasSubmitted()
    {
        var message = new ChatMessage
        {
            EventKind = "ask_user",
            Status = EventStatus.Done,
            EventMeta = "Skipped",
        };

        Assert.Equal("Skipped", message.InteractiveStateLabel);
        Assert.Equal("Question skipped", message.AskUserResultTitle);
        Assert.Equal("No response was submitted.", message.InteractiveStateDescription);
        Assert.False(message.HasInteractiveAnswer);
    }

    [Theory]
    [InlineData(PlanReviewAction.Approve, "", "", false)]
    [InlineData(PlanReviewAction.RequestChanges, "Use port 8080", "Use port 8080", false)]
    [InlineData(PlanReviewAction.Reject, "Too risky", "rejected: Too risky", true)]
    public void PlanReview_UsesExistingMessageOnlySchema(
        PlanReviewAction action, string feedback, string expectedMessage, bool rejected)
    {
        var response = InteractiveResponseBuilder.BuildPlanReviewResponse(action, feedback);
        Assert.NotNull(response);
        Assert.Equal(expectedMessage, response.Message);
        Assert.Equal(rejected, response.Rejected);
    }

    [Fact]
    public void PlanReview_RequestChangesRequiresFeedback()
        => Assert.Null(InteractiveResponseBuilder.BuildPlanReviewResponse(PlanReviewAction.RequestChanges, "  "));

    // ---- Answer summaries: what reaches the transcript and the messages table -----------------
    //
    // The wire answer and the summary are different values on purpose. These tests pin that
    // difference, because collapsing them back together is what wrote credentials to disk.

    [Fact]
    public void AskUserSummary_MasksSecretFieldsWhileTheWireAnswerKeepsThem()
    {
        var message = Ask();
        message.AskUserFields.Add(Field(message, "username", "Username", true, "bob"));
        var password = Field(message, "password", "Password", true, "hunter2");
        password.Type = "password";
        message.AskUserFields.Add(password);

        // The agent asked for the password because it needs the password.
        var wire = Assert.IsType<string>(InteractiveResponseBuilder.BuildAskUserAnswer(message));
        Assert.Contains("hunter2", wire, StringComparison.Ordinal);

        // The summary is displayed in the transcript and written to event_meta, where it outlives
        // the session. It must not carry the secret.
        var summary = InteractiveResponseBuilder.BuildAskUserAnswerSummary(message);
        Assert.DoesNotContain("hunter2", summary, StringComparison.Ordinal);
        Assert.Equal("Username=bob · Password=••••••", summary);
    }

    [Fact]
    public void AskUserSummary_MaskIsFixedLengthSoItLeaksNothingAboutTheSecret()
    {
        var shortSecret = Ask();
        var a = Field(shortSecret, "password", "Password", true, "x");
        a.Type = "password";
        shortSecret.AskUserFields.Add(a);

        var longSecret = Ask();
        var b = Field(longSecret, "password", "Password", true, "a-very-long-passphrase-indeed");
        b.Type = "password";
        longSecret.AskUserFields.Add(b);

        Assert.Equal(
            InteractiveResponseBuilder.BuildAskUserAnswerSummary(shortSecret),
            InteractiveResponseBuilder.BuildAskUserAnswerSummary(longSecret));
    }

    [Theory]
    // The agent's own type is the primary signal...
    [InlineData("anything", "Anything", "password", true)]
    // ...but nothing forces an agent to set it, and these are just as dangerous as plain text.
    [InlineData("api_key", "API key", null, true)]
    [InlineData("apiKey", "Key", null, true)]
    [InlineData("login_password", "Login", null, true)]
    [InlineData("refresh_token", "Token", null, true)]
    [InlineData("account_secret", "Secret", null, true)]
    [InlineData("otp", "One-time code", null, true)]
    // Ordinary fields must not be masked — an unreadable transcript is its own failure.
    [InlineData("username", "Username", null, false)]
    [InlineData("email", "Email", null, false)]
    [InlineData("invite_code", "Invite code", null, false)]
    public void IsSecretField_ClassifiesByTypeThenByName(
        string name, string label, string? type, bool expected)
    {
        var message = Ask();
        var field = Field(message, name, label, false, "value");
        field.Type = type;

        Assert.Equal(expected, InteractiveResponseBuilder.IsSecretField(field));
    }

    [Fact]
    public void AskUserSummary_RendersFieldsAsProseRatherThanJson()
    {
        var message = Ask();
        message.AskUserFields.Add(Field(message, "host", "Host", true, "example.com"));
        message.AskUserFields.Add(Field(message, "note", "Note", false, ""));

        // The summary is read by a person; {"host":"example.com","note":""} never was.
        Assert.Equal("Host=example.com · Note=(blank)",
            InteractiveResponseBuilder.BuildAskUserAnswerSummary(message));
    }

    [Theory]
    [InlineData("password", "Password", null, true)]
    [InlineData("api_key", "API key", "text", true)]
    [InlineData("username", "Username", "text", false)]
    public void AskUserFieldEntry_ExposesSecretPresentation(
        string name, string label, string? type, bool expected)
    {
        var field = Field(Ask(), name, label, false, "");
        field.Type = type;

        Assert.Equal(expected, field.IsSecret);
        Assert.Equal(expected, InteractiveResponseBuilder.IsSecretField(field));
    }

    [Fact]
    public void AskUserSummary_JoinsSelectedOptionsAndPassesFreeTextThrough()
    {
        var options = Ask(true, "Agents", "Logs", "Status");
        options.AskUserOptionEntries[0].Toggle();
        options.AskUserOptionEntries[2].Toggle();
        Assert.Equal("Agents, Status", InteractiveResponseBuilder.BuildAskUserAnswerSummary(options));

        var free = Ask();
        free.AskUserFreeText = "  ship it  ";
        Assert.Equal("ship it", InteractiveResponseBuilder.BuildAskUserAnswerSummary(free));
    }

    private static ChatMessage Ask(bool multi = false, params string[] options)
    {
        var message = new ChatMessage
        {
            EventKind = "ask_user",
            Status = EventStatus.Running,
            AskUserMultiSelect = multi,
        };
        foreach (var option in options)
            message.AskUserOptionEntries.Add(new AskUserOptionEntry { Text = option, Owner = message });
        return message;
    }

    private static AskUserFieldEntry Field(
        ChatMessage owner, string name, string label, bool required, string value)
        => new() { Owner = owner, Name = name, Label = label, Required = required, Value = value };
}
