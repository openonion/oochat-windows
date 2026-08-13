using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

/// <summary>The embedded approval card's phase machine and command routing, driven the same way the
/// unified tool-activity card drives it — commands flip the phase synchronously, the responder does
/// the (here faked) network, and the phase settles.</summary>
public sealed class ChatMessageApprovalTests
{
    /// <summary>An approval whose responder records the actions it was asked to send (and does not
    /// settle the phase, so the tests can inspect the Submitting state).</summary>
    private static ChatMessage Approval(out System.Collections.Generic.List<ApprovalAction> seen)
    {
        var captured = new System.Collections.Generic.List<ApprovalAction>();
        seen = captured;
        var message = new ChatMessage
        {
            Role = ChatRole.Event,
            EventKind = "approval",
            EventTitle = "Remote Write File",
            EventArgs = "{}",
        };
        message.ApprovalResponder = (m, action) => { captured.Add(action); return Task.CompletedTask; };
        return message;
    }

    [Fact]
    public async Task AllowOnce_FlipsToSubmittingSynchronously_AndInvokesTheResponderOnce()
    {
        var message = Approval(out var seen);

        Assert.True(message.IsApprovalWaiting);
        Assert.True(message.AllowOnceCommand.CanExecute(null));

        await message.AllowOnceCommand.ExecuteAsync(null);

        Assert.Equal(ApprovalCardPhase.Submitting, message.ApprovalPhase);
        Assert.Equal(new[] { ApprovalAction.AllowOnce }, seen);
        // While submitting, every decision command is disabled — no double-send.
        Assert.False(message.AllowOnceCommand.CanExecute(null));
        Assert.False(message.RejectCommand.CanExecute(null));
        Assert.False(message.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task RapidSecondClick_WhileSubmitting_DoesNotSendTwice()
    {
        var message = Approval(out var seen);

        // Fire both without settling — the second sees the guard closed by the first.
        var first = message.AllowOnceCommand.ExecuteAsync(null);
        var second = message.AllowOnceCommand.ExecuteAsync(null);
        await Task.WhenAll(first, second);

        Assert.Single(seen);
    }

    [Theory]
    [InlineData(ApprovalAction.AllowOnce, ApprovalCardPhase.ApprovedOnce)]
    [InlineData(ApprovalAction.TrustSession, ApprovalCardPhase.TrustedSession)]
    [InlineData(ApprovalAction.Reject, ApprovalCardPhase.Rejected)]
    [InlineData(ApprovalAction.Stop, ApprovalCardPhase.Stopped)]
    [InlineData(ApprovalAction.Explain, ApprovalCardPhase.ExplanationRequested)]
    public void CompleteApproval_MapsEachActionToItsResolvedPhase(ApprovalAction action, ApprovalCardPhase expected)
    {
        var message = new ChatMessage { EventKind = "approval", EventTitle = "Remote Write File" };

        message.CompleteApproval(action);

        Assert.Equal(expected, message.ApprovalPhase);
        Assert.True(message.IsApprovalResolved);
    }

    [Fact]
    public void ResolvedText_NamesTheToolAndScope_FromLiveData()
    {
        var message = new ChatMessage { EventKind = "approval", EventTitle = "Remote Write File" };

        message.CompleteApproval(ApprovalAction.TrustSession);
        Assert.Equal("Allowed for this session", message.ApprovalResolvedText);
        Assert.True(message.IsApprovalApprovedResult);

        message.CompleteApproval(ApprovalAction.Reject);
        Assert.Equal("Declined", message.ApprovalResolvedText);
        Assert.True(message.IsApprovalRejectedResult);

        message.CompleteApproval(ApprovalAction.Stop);
        Assert.Equal("Task stopped", message.ApprovalResolvedText);
        Assert.True(message.IsApprovalStoppedResult);

        message.CompleteApproval(ApprovalAction.Explain);
        Assert.Equal("Explanation requested", message.ApprovalResolvedText);
        Assert.True(message.IsApprovalExplanationResult);
    }

    [Fact]
    public void AllowOnce_SubmittingVerb_MatchesTheAction()
    {
        var message = new ChatMessage { EventKind = "approval" };

        message.ApprovalPendingAction = ApprovalAction.Reject;
        Assert.Equal("Rejecting…", message.ApprovalSubmittingVerb);

        message.ApprovalPendingAction = ApprovalAction.Stop;
        Assert.Equal("Stopping…", message.ApprovalSubmittingVerb);

        message.ApprovalPendingAction = ApprovalAction.AllowOnce;
        Assert.Equal("Approving…", message.ApprovalSubmittingVerb);

        message.ApprovalPendingAction = ApprovalAction.Explain;
        Assert.Equal("Requesting explanation…", message.ApprovalSubmittingVerb);
    }

    [Fact]
    public async Task FailedSubmit_ReturnsToActionable_AndRetryReissuesTheSameAction()
    {
        var seen = new System.Collections.Generic.List<ApprovalAction>();
        var message = new ChatMessage { EventKind = "approval", EventTitle = "Remote Write File" };
        message.ApprovalResponder = (m, action) => { seen.Add(action); return Task.CompletedTask; };

        await message.TrustSessionCommand.ExecuteAsync(null);
        Assert.Equal(ApprovalCardPhase.Submitting, message.ApprovalPhase);

        message.FailApproval("Socket closed");

        Assert.Equal(ApprovalCardPhase.Failed, message.ApprovalPhase);
        Assert.Equal("Socket closed", message.ApprovalErrorText);
        Assert.True(message.IsApprovalActionable);
        Assert.True(message.RetryApprovalCommand.CanExecute(null));

        await message.RetryApprovalCommand.ExecuteAsync(null);

        Assert.Equal(new[] { ApprovalAction.TrustSession, ApprovalAction.TrustSession }, seen);
    }

    [Fact]
    public void WithNoResponder_TheCommandsCannotExecute()
    {
        // The headless persist path never sets a responder, so the decision controls are inert.
        var message = new ChatMessage { EventKind = "approval" };

        Assert.False(message.AllowOnceCommand.CanExecute(null));
        Assert.False(message.RejectCommand.CanExecute(null));
    }

    [Fact]
    public void ResponderArrivingAfterTheCardWasBound_ReenablesDecisionCommands()
    {
        var message = new ChatMessage { EventKind = "approval" };
        var canExecuteChanged = 0;
        message.AllowOnceCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

        Assert.False(message.AllowOnceCommand.CanExecute(null));

        message.ApprovalResponder = (_, _) => Task.CompletedTask;

        Assert.True(message.AllowOnceCommand.CanExecute(null));
        Assert.True(message.RejectCommand.CanExecute(null));
        Assert.True(canExecuteChanged > 0);
    }

    [Fact]
    public void ToggleDetails_And_ExplainRisk_AreLocalDisclosureOnly()
    {
        var message = new ChatMessage { EventKind = "approval", EventArgs = "{\"path\":\"a.txt\"}", EventDetail = "risky" };

        Assert.False(message.IsApprovalDetailsOpen);
        message.ToggleApprovalDetailsCommand.Execute(null);
        Assert.True(message.IsApprovalDetailsOpen);

        Assert.False(message.ShowApprovalRisk);
        message.ExplainApprovalRiskCommand.Execute(null);
        Assert.True(message.ShowApprovalRisk);   // explain open

        // Neither disclosure resolves the approval.
        Assert.True(message.IsApprovalWaiting);
    }

    [Fact]
    public async Task ExplainWhy_IsASeparateProtocolAction_AndSuppliesItsPromptAutomatically()
    {
        var message = Approval(out var seen);

        await message.ExplainCommand.ExecuteAsync(null);

        Assert.Equal(new[] { ApprovalAction.Explain }, seen);
        Assert.Equal(
            "Explain why this operation is required, what it will do, and what risks it has.",
            message.ApprovalFeedback);
        Assert.Equal(ApprovalCardPhase.Submitting, message.ApprovalPhase);
    }

    [Fact]
    public void ExplainRisk_RaisesShowApprovalRisk_SoThePanelActuallyAppears()
    {
        // The bug this pins: ToggleApprovalExplain raised only IsApprovalExplainOpen, so the panel's
        // ShowApprovalRisk binding never updated and "Explain this risk" did nothing.
        var message = new ChatMessage { EventKind = "approval" };
        var raised = new System.Collections.Generic.List<string>();
        message.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        message.ExplainApprovalRiskCommand.Execute(null);

        Assert.Contains(nameof(ChatMessage.ShowApprovalRisk), raised);
        Assert.True(message.ShowApprovalRisk);
    }

    [Fact]
    public void RiskText_FallsBackToAGenericCaution_WhenTheRequestCarriedNoDescription()
    {
        var withDesc = new ChatMessage { EventKind = "approval", EventDetail = "Blocked: deletes the file." };
        Assert.DoesNotContain("Blocked", withDesc.ApprovalRiskText);

        var noDesc = new ChatMessage { EventKind = "approval" };
        Assert.False(string.IsNullOrWhiteSpace(noDesc.ApprovalRiskText));   // never a dead-end link
    }

    [Fact]
    public void LongCommand_UsesFullWireValue_AndCanExpandWithoutTruncatingIt()
    {
        var command = "find /srv -name '*.tmp' -delete\n" + new string('x', 180);
        var message = new ChatMessage
        {
            EventKind = "approval",
            ApprovalTargetInfo = new ApprovalTarget(ApprovalTargetKind.Command, "short…", command),
        };

        Assert.Equal(command, message.ApprovalCommandText);
        Assert.Equal(3, message.ApprovalCommandMaxLines);

        message.ToggleApprovalCommandCommand.Execute(null);

        Assert.Equal(0, message.ApprovalCommandMaxLines);
        Assert.Equal("Collapse command", message.ApprovalCommandToggleLabel);
    }

}
