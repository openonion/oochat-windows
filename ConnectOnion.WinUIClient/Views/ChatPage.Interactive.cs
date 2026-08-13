using ConnectOnion.WinUIClient.Controls;
using ConnectOnion.WinUIClient.Models;
using Microsoft.UI.Xaml;

namespace ConnectOnion.WinUIClient.Views;

/// <summary>
/// <see cref="ChatPage"/>: the interactive-turn cards (ask_user / approval_needed / plan_review).
/// These are the handlers that answer an agent which is blocked waiting on the user, so each one
/// resolves a pending decision rather than merely updating the view.
/// </summary>
public sealed partial class ChatPage
{
    private async void AskUserCard_SubmitRequested(object sender, RoutedEventArgs e)
    {
        if (sender is AskUserCard { Message: { } message })
            await Vm.SubmitAskUserAnswerAsync(message);
    }

    private async void AskUserCard_SkipRequested(object sender, RoutedEventArgs e)
    {
        if (sender is AskUserCard { Message: { } message })
            await Vm.SkipAskUserAsync(message);
    }

    private async void PlanReviewCard_ApproveRequested(object sender, RoutedEventArgs e)
    {
        if (sender is PlanReviewCard { Message: { } message })
            await Vm.SubmitPlanReviewAsync(message, PlanReviewAction.Approve);
    }

    private async void PlanReviewCard_ChangesRequested(object sender, RoutedEventArgs e)
    {
        if (sender is PlanReviewCard { Message: { } message })
            await Vm.SubmitPlanReviewAsync(message, PlanReviewAction.RequestChanges);
    }

    private async void PlanReviewCard_RejectRequested(object sender, RoutedEventArgs e)
    {
        if (sender is PlanReviewCard { Message: { } message })
            await Vm.SubmitPlanReviewAsync(message, PlanReviewAction.Reject);
    }

}
