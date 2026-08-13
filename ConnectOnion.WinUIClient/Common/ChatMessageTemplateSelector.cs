using ConnectOnion.WinUIClient.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient.Presentation;

/// <summary>
/// Picks the one template a <see cref="ChatMessage"/> actually needs, instead of building every
/// bubble kind into every row and hiding all but one.
///
/// This is safe to resolve once per container because a message's kind is fixed by the time it
/// reaches the list: <c>Role</c>, <c>EventKind</c> and <c>ToolActivity</c> are all assigned in the
/// object initializer (see <c>ChatTurnProjection</c>) or while loading history
/// (<c>ConversationRepository.RowToMessage</c>, <c>ToolActivityMigration</c>) — never after the
/// message has been added. Everything that <i>does</i> mutate live (Content, Status, EventMeta,
/// attachments) only changes what a bubble shows, not which bubble it is, and stays on ordinary
/// bindings inside the chosen template. If a kind ever becomes mutable post-add, this selector
/// will not re-run for it and the row would keep its original template.
/// </summary>
public sealed partial class ChatMessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? User { get; set; }
    public DataTemplate? Agent { get; set; }
    public DataTemplate? Usage { get; set; }
    public DataTemplate? Activity { get; set; }
    public DataTemplate? ToolActivity { get; set; }
    public DataTemplate? AskUser { get; set; }
    public DataTemplate? Approval { get; set; }
    public DataTemplate? PlanReview { get; set; }
    public DataTemplate? DiffPreview { get; set; }

    /// <summary>Renders nothing. Covers event kinds this build has no card for — a legacy
    /// <c>"tool"</c> row that migration left behind, or a <c>tool_activity</c> row whose payload
    /// failed to deserialize — which the previous all-in-one template also drew as a blank row.</summary>
    public DataTemplate? Empty { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is not ChatMessage message) return Empty;

        if (message.IsUser) return User;
        // Persisted event rows from older builds can carry Role=Agent. EventKind is the more
        // specific discriminator and must win; checking IsAgent first silently turns restored
        // tool timelines and interactive cards into generic assistant bubbles.
        if (message.IsTurnUsageEvent) return Usage;
        if (message.IsActivityEvent) return Activity;
        if (message.IsToolActivityEvent) return ToolActivity;
        // A file-change ask_user is rendered directly under its diff so the proposal and its
        // decision cannot be separated by another streamed event. Its message still stays in the
        // collection for protocol response and persistence alignment.
        if (message.IsAskUserEvent)
            return message.RelatedDiffPreview is null ? AskUser : Empty;
        // An approval owns its own row. It used to be rendered by the turn's ToolActivityView,
        // which anchors at the turn's FIRST tool call — so a turn that had since appended a plan,
        // a question or a diff drew the live decision back up above all of them, mid-conversation.
        // The tool card still shows "Approval required" through ToolActivityViewModel.Approval;
        // it just no longer draws the decision.
        if (message.IsApprovalEvent) return Approval;
        if (message.IsPlanReviewEvent) return PlanReview;
        if (message.IsDiffPreviewEvent) return DiffPreview;
        if (message.IsAgent) return Agent;

        return Empty;
    }
}
