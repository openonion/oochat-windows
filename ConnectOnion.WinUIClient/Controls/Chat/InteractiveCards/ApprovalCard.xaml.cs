using ConnectOnion.WinUIClient.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// The <c>approval_needed</c> decision as its own transcript row, alongside
/// <see cref="AskUserCard"/> and <see cref="PlanReviewCard"/> over the shared
/// <see cref="InteractiveCard"/> chrome.
///
/// <para>Unlike those two it raises no events: an approval answers through the commands on its own
/// <see cref="ChatMessage"/> (<c>AllowOnceCommand</c> and friends), which route to the
/// <c>ApprovalResponder</c> the view model wires at add time. Everything here is local — copying
/// the command, and Esc closing a disclosure.</para>
/// </summary>
public sealed partial class ApprovalCard : UserControl
{
    public ApprovalCard() => InitializeComponent();

    public ChatMessage Message
    {
        get => (ChatMessage)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(ChatMessage), typeof(ApprovalCard), new PropertyMetadata(null));

    private void ApprovalCopyCommand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ChatMessage approval
            || string.IsNullOrWhiteSpace(approval.ApprovalCommandText))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(approval.ApprovalCommandText);
        Clipboard.SetContent(package);
    }

    /// <summary>
    /// Shows the disclosure only when the three-line preview actually hides rendered text. A
    /// character-count estimate cannot account for card width, font metrics, DPI, or localisation,
    /// and produced a button that did nothing for commands which already fitted in full.
    /// </summary>
    private void ApprovalCommandText_IsTextTrimmedChanged(
        TextBlock sender,
        IsTextTrimmedChangedEventArgs args)
    {
        _ = args;
        ApprovalCommandToggleButton.Visibility =
            sender.IsTextTrimmed || Message?.IsApprovalCommandExpanded == true
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>Esc closes whatever disclosure is open — the details panel, the expanded command,
    /// or the stop confirmation. It is deliberately not bound to a decision: Decline is still an
    /// answer sent to the agent, and a key people press reflexively to dismiss things must not
    /// answer for them.</summary>
    private void ApprovalRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape || Message is null) return;

        Message.CloseApprovalDisclosures();
        e.Handled = true;
    }
}
