using System;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace ConnectOnion.WinUIClient;

/// <summary>
/// The Edit menu: undo/redo/cut/copy/paste/delete/select-all against whatever text box the user
/// was last in.
///
/// The whole file exists to serve the <b>menu items</b>, not the keyboard. A focused
/// <c>TextBox</c> already implements every one of these natively, so the shortcuts here are
/// deliberately a fallback that stands down whenever a text control has focus (see
/// <see cref="EditMenuShortcut_KeyDown"/>) — which is also why the catalog lists Ctrl+C and
/// friends as non-customizable: the app does not own those keys.
///
/// Clicking a menu item moves focus to the menu, so "the text box the command applies to" has
/// to be remembered rather than read at invoke time — hence
/// <see cref="_lastFocusedTextBox"/>.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The most recent text box to hold focus. This is what makes the menu items work
    /// at all: by the time a click reaches them the text box has already lost focus to the menu,
    /// so <see cref="FocusManager"/> alone would report no target.</summary>
    private TextBox? _lastFocusedTextBox;

    private void RegisterEditMenuAccelerators()
    {
        RootGrid.GotFocus += EditTarget_GotFocus;
        RootGrid.KeyDown += EditMenuShortcut_KeyDown;
    }

    // Tracks focus window-wide from the root, so every text box in every page is covered without
    // each one having to opt in. Only assigns on a TextBox — focus moving to a button or the
    // menu must leave the last remembered target intact, which is the entire point.
    private void EditTarget_GotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox textBox)
        {
            _lastFocusedTextBox = textBox;
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
        => UndoText();

    private void Redo_Click(object sender, RoutedEventArgs e)
        => RedoText();

    private void Cut_Click(object sender, RoutedEventArgs e)
        => CutText();

    private void Copy_Click(object sender, RoutedEventArgs e)
        => CopyText();

    private async void Paste_Click(object sender, RoutedEventArgs e)
        => await PasteTextAsync();

    private void Delete_Click(object sender, RoutedEventArgs e)
        => DeleteSelectedText();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
        => SelectAllText();

    private async void EditMenuShortcut_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsModalOverlayOpen) return;

        // TextBox natively handles Ctrl+C/V/X/Z/Y/A/Delete.  Don't double-fire
        // by routing the same key through our custom handler while the TextBox
        // (or a child of one) is focused.
        if (e.OriginalSource is TextBox or PasswordBox
            || (e.OriginalSource is FrameworkElement fe && FindParentTextBox(fe) is not null))
            return;

        var target = CurrentTextBoxTarget();
        if (target is null) return;

        // Modifiers are matched exactly, not merely tested for presence: Ctrl+Shift+C and
        // Ctrl+Alt+C are different commands elsewhere, and a loose check here would swallow them.
        // (VirtualKey.Menu is Alt — the Win32 name, not a menu key.)
        var hasControl = IsKeyDown(VirtualKey.Control);
        var hasShift = IsKeyDown(VirtualKey.Shift);
        var hasAlt = IsKeyDown(VirtualKey.Menu);

        // Hardcoded key codes here rather than the shortcut catalog, unlike every other menu:
        // these chords are OS text-editing conventions the app does not own and cannot rebind.
        if (!hasAlt && !hasShift && hasControl)
        {
            switch (e.Key)
            {
                case VirtualKey.Z:
                    UndoText(target);
                    e.Handled = true;
                    return;
                case VirtualKey.Y:
                    RedoText(target);
                    e.Handled = true;
                    return;
                case VirtualKey.X:
                    CutText(target);
                    e.Handled = true;
                    return;
                case VirtualKey.C:
                    CopyText(target);
                    e.Handled = true;
                    return;
                case VirtualKey.V:
                    await PasteTextAsync(target);
                    e.Handled = true;
                    return;
                case VirtualKey.A:
                    SelectAllText(target);
                    e.Handled = true;
                    return;
            }
        }

        if (!hasControl && !hasShift && !hasAlt && e.Key == VirtualKey.Delete)
        {
            DeleteSelectedText(target);
            e.Handled = true;
        }
    }

    private TextBox? CurrentTextBoxTarget()
        => FocusManager.GetFocusedElement(RootGrid.XamlRoot) as TextBox ?? _lastFocusedTextBox;

    /// <summary>Walk up the visual tree to find a parent TextBox (useful when the
    /// event source is a child element like a Grid or Border inside a TextBox template).</summary>
    private static TextBox? FindParentTextBox(DependencyObject element)
    {
        while (element is not null)
        {
            if (element is TextBox tb) return tb;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    // Each command comes in a pair: a parameterless overload that resolves the target (what the
    // menu items call) and a static one that acts on a given TextBox (what the key handler
    // calls, since it has already resolved the target to decide whether to stand down). Keeping
    // the resolution in exactly one place is what stops the two entry points from diverging.

    private void UndoText()
    {
        if (CurrentTextBoxTarget() is { } target)
            UndoText(target);
    }

    private static void UndoText(TextBox target)
    {
        if (target.CanUndo)
            target.Undo();
    }

    private void RedoText()
    {
        if (CurrentTextBoxTarget() is { } target)
            RedoText(target);
    }

    private static void RedoText(TextBox target)
    {
        // No CanRedo guard as Undo has: TextBox exposes CanUndo but no CanRedo, and Redo on an
        // empty redo stack is a documented no-op.
        target.Redo();
    }

    private void CutText()
    {
        if (CurrentTextBoxTarget() is { } target)
            CutText(target);
    }

    private static void CutText(TextBox target)
    {
        // Bail on an empty selection rather than cutting nothing: proceeding would clear the
        // clipboard, destroying content the user may still want to paste.
        if (target.SelectionLength == 0) return;

        CopyText(target);
        DeleteSelectedText(target);
    }

    private void CopyText()
    {
        if (CurrentTextBoxTarget() is { } target)
            CopyText(target);
    }

    private static void CopyText(TextBox target)
        => ClipboardService.CopyText(target.SelectedText);

    private async Task PasteTextAsync()
    {
        if (CurrentTextBoxTarget() is { } target)
            await PasteTextAsync(target);
    }

    private static async Task PasteTextAsync(TextBox target)
    {
        var content = Clipboard.GetContent();
        // Text only. A clipboard holding an image or a file drop is silently ignored here —
        // attachments come in through the composer's own picker/drop path, not through Edit.
        if (!content.Contains(StandardDataFormats.Text)) return;

        var text = await content.GetTextAsync();
        ReplaceSelection(target, text);
    }

    private void DeleteSelectedText()
    {
        if (CurrentTextBoxTarget() is { } target)
            DeleteSelectedText(target);
    }

    private static void DeleteSelectedText(TextBox target)
    {
        if (target.SelectionLength > 0)
        {
            ReplaceSelection(target, string.Empty);
        }
    }

    private void SelectAllText()
    {
        if (CurrentTextBoxTarget() is { } target)
            SelectAllText(target);
    }

    private static void SelectAllText(TextBox target)
        => target.SelectAll();

    /// <summary>Swaps the selected range for <paramref name="text"/> and leaves the caret after
    /// the inserted text. Rebuilding <c>Text</c> wholesale (rather than using SelectedText) is
    /// what makes paste and delete share one implementation — deleting is pasting "".
    /// <para>Note the caret is restored explicitly: assigning Text resets it to 0, which would
    /// send the user back to the start of the box after every paste.</para></summary>
    private static void ReplaceSelection(TextBox target, string text)
    {
        var start = target.SelectionStart;
        var length = target.SelectionLength;
        var value = target.Text ?? string.Empty;

        target.Text = value.Remove(start, length).Insert(start, text);
        target.SelectionStart = start + text.Length;
        target.SelectionLength = 0;
    }
}
