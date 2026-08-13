using System;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Persists the main window's last normal screen position and whether it was maximized. The compact
/// invariant <c>x,y,maximized</c> value lives in <c>app_meta</c>, so placement needs no migration and is
/// shared by packaged and unpackaged runs through the existing database abstraction.
/// </summary>
public sealed class WindowPlacementStore
{
    private const string MetaKey = "main_window_position";

    public WindowPlacement? Current { get; private set; }

    public void ApplyLoaded(string? value) => Current = WindowPlacementPolicy.TryParse(value);

    public async Task LoadAsync()
    {
        try
        {
            await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
            var value = await AppDatabase.GetMetaAsync(connection, MetaKey).ConfigureAwait(false);
            ApplyLoaded(value);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Window position could not be loaded; using the system default");
        }
    }

    public async Task SaveAsync(WindowPlacement placement)
    {
        Current = placement;
        try
        {
            await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
            var value = WindowPlacementPolicy.Serialize(placement);
            await AppDatabase.SetMetaAsync(connection, null, MetaKey, value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Window placement is a convenience. A locked/corrupt database must never turn Exit
            // into a crash or prevent the rest of the application shutdown from completing.
            Serilog.Log.Warning(ex, "Window position could not be saved");
        }
    }

}
