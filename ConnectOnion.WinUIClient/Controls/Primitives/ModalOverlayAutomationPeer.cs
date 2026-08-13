using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// Gives a full-window overlay the UI Automation identity of a dialog.
///
/// <para>A plain <c>UserControl</c> produces no automation peer of its own. That has two
/// consequences this fixes at once. A screen reader announces the overlay's contents as though
/// they were simply more of the page behind it — there is no "dialog" boundary, no name, and
/// nothing to tell the user that the rest of the window has stopped being reachable. And
/// <c>AutomationProperties.AutomationId</c> set in XAML is never exposed, which is why
/// <c>ByAutomationId</c> returns null for <c>SettingsOverlay</c>, <c>AboutOverlay</c> and
/// <c>KeyboardShortcutsDialog</c>, and why the FlaUI smoke test has to reach past them to a
/// child that happens to be exposed.</para>
///
/// <para><see cref="AutomationControlType.Window"/> is the control type UIA uses for a dialog;
/// there is no separate Dialog type. Pair it with an <c>AutomationProperties.Name</c> on the
/// control so the announcement says which dialog opened.</para>
/// </summary>
public sealed class ModalOverlayAutomationPeer : FrameworkElementAutomationPeer
{
    public ModalOverlayAutomationPeer(FrameworkElement owner) : base(owner)
    {
    }

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.Window;

    protected override string GetClassNameCore() => Owner.GetType().Name;
}
