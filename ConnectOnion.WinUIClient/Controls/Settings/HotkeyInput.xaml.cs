using System;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// Captures one key combination. Click (or Enter/Space) arms it; the next real keystroke becomes
/// the chord and is reported through <see cref="ChordCaptured"/>. It only proposes — the caller
/// decides whether the chord is acceptable and calls back with <see cref="ShowChord"/>, so a
/// refused rebind never leaves the button showing something that isn't live.
///
/// <para>Esc and Tab are deliberately <b>not</b> capturable: Esc has to keep closing the settings
/// overlay and Tab has to keep moving focus, or arming this control in a modal would trap the
/// user with no way out. They cancel capture instead. Modifier keys on their own are ignored so
/// the control waits for a real key rather than binding "Ctrl".</para>
/// </summary>
public sealed partial class HotkeyInput : UserControl
{
    /// <summary>Raised with a candidate chord. The caller applies or refuses it.</summary>
    public event Action<KeyChord>? ChordCaptured;

    private KeyChord _chord = KeyChord.None;
    private bool _capturing;

    public HotkeyInput()
    {
        InitializeComponent();
        Render();
    }

    /// <summary>Shows a chord without raising <see cref="ChordCaptured"/>. The caller uses this to
    /// seed the row and to snap back after refusing a capture.</summary>
    public void ShowChord(KeyChord chord)
    {
        _chord = chord;
        EndCapture();
        Render();
    }

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        Render();
    }

    private void CaptureButton_LostFocus(object sender, RoutedEventArgs e)
    {
        // Clicking away is a cancel — leaving it armed would swallow the next keystroke
        // somewhere else entirely.
        if (!_capturing) return;
        EndCapture();
        Render();
    }

    private void CaptureButton_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing) return;

        // Let the overlay keep its dismissal and its focus order.
        if (e.Key is VirtualKey.Escape or VirtualKey.Tab)
        {
            EndCapture();
            Render();
            return;   // deliberately not Handled — Esc/Tab still reach the overlay
        }

        var chord = KeyChord.FromKeyEvent(
            LayoutKeys.Normalize((int)e.Key),
            IsDown(VirtualKey.Control),
            IsDown(VirtualKey.Shift),
            IsDown(VirtualKey.Menu));

        // A modifier on its own, or a key outside the bindable table: keep waiting.
        if (chord.IsEmpty)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        EndCapture();
        ChordCaptured?.Invoke(chord);
    }

    private void EndCapture() => _capturing = false;

    private static bool IsDown(VirtualKey key)
        => InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(CoreVirtualKeyStates.Down);

    private void Render()
    {
        if (_capturing)
        {
            ChordText.Text = Common.LocalizedStrings.Get("HotkeyPressKey", "Press a key…");
            return;
        }

        ChordText.Text = _chord.IsEmpty ? "Not set" : _chord.Canonical;
    }
}
