using System.ComponentModel;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// One shortcut in Settings → Keyboard. Wired in code rather than through <c>x:Bind</c> because the
/// row is two mutually exclusive shapes — a capture control for something the app dispatches, a
/// plain read-only keycap plus a reason for something it does not — and expressing that as bindings
/// plus visibility converters obscures which one a given row actually is.
/// </summary>
public sealed partial class KeyboardShortcutEditorRow : UserControl
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row),
        typeof(KeyboardShortcutRow),
        typeof(KeyboardShortcutEditorRow),
        new PropertyMetadata(null, OnRowChanged));

    public KeyboardShortcutRow? Row
    {
        get => (KeyboardShortcutRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public KeyboardShortcutEditorRow()
    {
        InitializeComponent();
        Hotkey.ChordCaptured += OnChordCaptured;
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Moves the PropertyChanged subscription from the old row to the new one. Rows are
    /// swapped as the settings list virtualizes, so unsubscribing from the outgoing row is what
    /// stops a recycled control from re-rendering on behalf of a shortcut it no longer shows.</summary>
    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (KeyboardShortcutEditorRow)d;
        if (e.OldValue is KeyboardShortcutRow old) old.PropertyChanged -= control.OnRowPropertyChanged;
        if (e.NewValue is KeyboardShortcutRow row) row.PropertyChanged += control.OnRowPropertyChanged;
        control.Render();
    }

    private void Detach()
    {
        Hotkey.ChordCaptured -= OnChordCaptured;
        if (Row is not null) Row.PropertyChanged -= OnRowPropertyChanged;
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private async void OnChordCaptured(KeyChord chord)
    {
        if (Row is null) return;

        // The row decides: a refused chord leaves the live binding alone and sets Conflict, and
        // Render snaps the button back to what is really bound.
        await Row.TryRebindAsync(chord);
        Render();
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (Row is null) return;
        await Row.ResetAsync();
        Render();
    }

    /// <summary>Rebuilds the whole row from the model. Idempotent and total — every element's
    /// text and visibility is assigned on each pass — so it can be called from the property
    /// change, the rebind, and the reset without any of them tracking what actually moved.</summary>
    private void Render()
    {
        if (Row is null) return;

        NameText.Text = KeyboardTextLocalizer.Localize(Row.Name);

        var reason = Row.IsCustomizable ? "" : Row.ReadOnlyReason;
        ReasonText.Text = KeyboardTextLocalizer.Localize(reason);
        ReasonText.Visibility = reason.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        ConflictText.Text = Row.Conflict;
        ConflictText.Visibility = Row.HasConflict ? Visibility.Visible : Visibility.Collapsed;

        Hotkey.Visibility = Row.IsCustomizable ? Visibility.Visible : Visibility.Collapsed;
        FixedKeys.Visibility = Row.IsCustomizable ? Visibility.Collapsed : Visibility.Visible;
        // Reset only appears once the shortcut actually differs from the factory chord — there
        // is nothing to reset otherwise, and a permanent button implies there is.
        ResetButton.Visibility = Row.IsCustomizable && Row.IsRebound ? Visibility.Visible : Visibility.Collapsed;

        if (Row.IsCustomizable)
        {
            // Shows what is *bound*, not what was typed: a refused rebind (the chord belongs to
            // another action) must snap the capture control back rather than display a
            // combination that will never fire.
            Hotkey.ShowChord(Row.Chord);
            AutomationProperties.SetName(Hotkey, $"{Row.Name} shortcut, {Row.Binding.DisplayText}");
        }
        else
        {
            FixedKeysText.Text = Row.Binding.DisplayText;
        }
    }
}
