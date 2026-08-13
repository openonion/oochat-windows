using CommunityToolkit.Mvvm.ComponentModel;

namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// Shared selection/hover/action-visibility state for the desktop sidebar's
/// agent and session rows. A single <c>RowStateToBackground</c>-driven Border
/// per row reads <see cref="ShowRowBackground"/>; hover and keyboard focus both
/// drive <see cref="SetInteractive"/>.
/// </summary>
public abstract partial class RowStateItem : Common.ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRowBackground))]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRowBackground))]
    public partial bool IsHovered { get; set; }

    [ObservableProperty]
    public partial bool AreActionsVisible { get; set; }

    /// <summary>True when the row should paint its unified background layer (selected and/or hovered) — see <c>RowStateToBackgroundConverter</c>.</summary>
    public bool ShowRowBackground => IsSelected || IsHovered;

    /// <summary>Enter/leave the interactive (hovered or focused) state: reveal the
    /// row's action buttons and light up its hover background together.</summary>
    public void SetInteractive(bool active)
    {
        AreActionsVisible = active;
        IsHovered = active;
    }
}
