using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

/// <summary>
/// The approval card's safety-facing behaviour: that its warning says something true about the
/// command in front of it, and that its visual defaults do not push the user toward allowing a
/// destructive one.
/// </summary>
public class ApprovalCardSafetyTests
{
    [Fact]
    public void DryRunCommand_IsNotDescribedAsDestructive()
    {
        var card = Approval("cd /srv/app && git clean -d -n");

        Assert.Equal(CommandRisk.ReadOnly, card.ApprovalRisk);
        Assert.Equal(InteractiveVisualTone.Success, card.ApprovalRiskTone);
        // The line must not be framed as a risk at all. Matching on "delete" would be wrong here:
        // the read-only wording legitimately contains it ("Nothing is changed or deleted").
        Assert.DoesNotContain("Risk:", card.ApprovalRiskText, StringComparison.Ordinal);
        Assert.Equal("Checkmark", card.ApprovalRiskGlyph);
    }

    [Fact]
    public void DestructiveCommand_KeepsTheWarning()
    {
        var card = Approval("rm -rf /var/data");

        Assert.Equal(CommandRisk.Destructive, card.ApprovalRisk);
        Assert.Equal(InteractiveVisualTone.Danger, card.ApprovalRiskTone);
        Assert.Contains("Risk:", card.ApprovalRiskText, StringComparison.Ordinal);
        Assert.Equal("Warning", card.ApprovalRiskGlyph);
    }

    /// <summary>An unrecognised command is a caution, never an all-clear — the assessor has no
    /// inference in the safe direction, and the card must not invent one.</summary>
    [Fact]
    public void UnrecognisedCommand_ReadsAsCautionNotSafe()
    {
        var card = Approval("./scripts/migrate.sh --apply");

        Assert.Equal(CommandRisk.Unknown, card.ApprovalRisk);
        Assert.Equal(InteractiveVisualTone.Warning, card.ApprovalRiskTone);
    }

    /// <summary>The inversion that stops the card arguing against its own warning. Under a red
    /// risk line the accent-filled, play-glyphed, last-in-tab-order button used to be <b>Allow
    /// once</b> — the card's whole visual language said "go" at the moment it was asking the user
    /// to stop.</summary>
    [Fact]
    public void DestructiveCommand_MovesEmphasisOffAllow()
    {
        var card = Approval("rm -rf /var/data");

        Assert.False(card.IsAllowTheSafeChoice);
        Assert.Equal("InteractiveCardPrimaryButtonStyle", card.ApprovalDeclineButtonStyle);
        Assert.Equal("InteractiveCardButtonStyle", card.ApprovalAllowButtonStyle);
        Assert.False(card.ShowApprovalAllowGlyph);
    }

    [Theory]
    [InlineData("ls -la")]
    [InlineData("git clean -d -n")]
    [InlineData("./scripts/migrate.sh")]
    public void NonDestructiveCommand_LeavesAllowAsThePrimaryAction(string command)
    {
        var card = Approval(command);

        Assert.True(card.IsAllowTheSafeChoice);
        Assert.Equal("InteractiveCardPrimaryButtonStyle", card.ApprovalAllowButtonStyle);
        Assert.Equal("InteractiveCardButtonStyle", card.ApprovalDeclineButtonStyle);
        Assert.True(card.ShowApprovalAllowGlyph);
    }

    /// <summary>Stop ends the whole turn. It gets the same second step plan_review already gives
    /// its far milder Reject, and the ordinary decisions stand down while it is asked.</summary>
    [Fact]
    public void Stop_AsksBeforeItStops()
    {
        var card = Approval("rm -rf /var/data");
        var sent = 0;
        card.ApprovalResponder = (_, _) => { sent++; return Task.CompletedTask; };

        card.StopCommand.Execute(null);
        Assert.True(card.IsApprovalStopConfirmOpen);
        Assert.False(card.ShowApprovalDecisionActions);
        Assert.Equal(0, sent);

        card.CancelApprovalStopCommand.Execute(null);
        Assert.False(card.IsApprovalStopConfirmOpen);
        Assert.True(card.ShowApprovalDecisionActions);
        Assert.Equal(0, sent);

        card.StopCommand.Execute(null);
        card.ConfirmApprovalStopCommand.Execute(null);
        Assert.False(card.IsApprovalStopConfirmOpen);
        Assert.Equal(1, sent);
    }

    /// <summary>Esc backs out of the confirmation — the safe direction. It is deliberately not
    /// wired to any decision: Decline is still an answer sent to the agent, and a key people press
    /// reflexively to dismiss things must not answer for them.</summary>
    [Fact]
    public void Escape_CancelsTheStopConfirmationAndAnswersNothing()
    {
        var card = Approval("rm -rf /var/data");
        var sent = 0;
        card.ApprovalResponder = (_, _) => { sent++; return Task.CompletedTask; };

        card.StopCommand.Execute(null);
        card.CloseApprovalDisclosures();

        Assert.False(card.IsApprovalStopConfirmOpen);
        Assert.Equal(ApprovalCardPhase.Waiting, card.ApprovalPhase);
        Assert.Equal(0, sent);
    }

    /// <summary>The card stated one fact four times. The risk line is now the only one of them
    /// derived from the command, so the subtitle that repeated it in weaker words stands down.</summary>
    [Fact]
    public void CommandApproval_DropsTheSubtitleThatRepeatedTheRiskLine()
    {
        var card = Approval("ls -la");

        Assert.False(card.HasApprovalPromptSubtitle);
        Assert.Equal("", card.ApprovalPromptSubtitle);
        // A non-command approval has no command to describe, so it keeps its generic guidance.
        var fileCard = new ChatMessage { Role = ChatRole.Event, EventKind = "approval" };
        Assert.True(fileCard.HasApprovalPromptSubtitle);
    }

    private static ChatMessage Approval(string command) => new()
    {
        Role = ChatRole.Event,
        EventKind = "approval",
        EventTitle = "Run command",
        Status = EventStatus.Running,
        ApprovalTargetInfo = new ApprovalTarget(ApprovalTargetKind.Command, command, command),
    };
}
