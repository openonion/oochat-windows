using System.Globalization;

namespace ConnectOnion.WinUIClient.Models;

public readonly record struct WindowPosition(int X, int Y);

/// <summary>The main window's saved placement.
/// <para><see cref="Size"/> is nullable because it is genuinely absent from placements written
/// by builds before it was persisted — those reopen at the platform default rather than at some
/// invented size.</para></summary>
public readonly record struct WindowPlacement(
    WindowPosition Position, bool IsMaximized, PixelSize? Size = null);

public readonly record struct PixelSize(int Width, int Height);
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>WinUI-free parsing and visibility rules for persisted main-window placement.</summary>
public static class WindowPlacementPolicy
{
    /// <summary>Smallest window the shell still lays out correctly, in epx.
    /// <para>Below roughly this width the composer's fixed-size control strip and the chat
    /// bubbles' own minimums stop fitting, and the sidebar — which becomes an overlay at the
    /// compact breakpoint — covers the entire content area. Without a floor the user can drag
    /// the window down to a few pixels and has to guess their way back out.</para></summary>
    public const int MinimumWidth = 640;

    /// <summary>Smallest usable height: the title bar, a readable transcript, and the composer
    /// at its own minimum.</summary>
    public const int MinimumHeight = 480;

    public static string Serialize(WindowPlacement placement)
        => placement.Size is { } size
            ? string.Create(CultureInfo.InvariantCulture,
                $"{placement.Position.X},{placement.Position.Y}," +
                $"{(placement.IsMaximized ? 1 : 0)},{size.Width},{size.Height}")
            : string.Create(CultureInfo.InvariantCulture,
                $"{placement.Position.X},{placement.Position.Y},{(placement.IsMaximized ? 1 : 0)}");

    public static WindowPlacement? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(',');
        if (parts.Length is not (2 or 3 or 5)
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            return null;

        // Two fields are placements written by older builds; they reopen restored.
        var maximized = false;
        if (parts.Length >= 3)
        {
            if (parts[2] == "1") maximized = true;
            else if (parts[2] != "0") return null;
        }

        // Five fields carry the restored size. A malformed or nonsensical size is dropped rather
        // than rejecting the whole record — the position is still worth restoring.
        PixelSize? size = null;
        if (parts.Length == 5
            && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            && int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            && width > 0 && height > 0)
        {
            size = ClampToMinimum(new PixelSize(width, height));
        }

        return new WindowPlacement(new WindowPosition(x, y), maximized, size);
    }

    /// <summary>Raises a size to the shell's minimum. Applied on read as well as on write, so a
    /// window saved by a build with a smaller floor (or a hand-edited preference row) still
    /// reopens usable.</summary>
    public static PixelSize ClampToMinimum(PixelSize size)
        => new(Math.Max(MinimumWidth, size.Width), Math.Max(MinimumHeight, size.Height));

    /// <summary>Keeps the full window in the work area when it fits; otherwise anchors it at the
    /// top-left so the caption remains reachable after a monitor, resolution, or DPI change.</summary>
    public static WindowPosition ClampToWorkArea(
        WindowPosition position, PixelSize window, PixelRect workArea)
    {
        var maxX = workArea.X + Math.Max(0, workArea.Width - window.Width);
        var maxY = workArea.Y + Math.Max(0, workArea.Height - window.Height);
        return new WindowPosition(
            Math.Clamp(position.X, workArea.X, maxX),
            Math.Clamp(position.Y, workArea.Y, maxY));
    }
}
