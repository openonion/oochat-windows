using System.Text.Json;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Data;

/// <summary>All persisted values needed before the first window, read through one connection.</summary>
public sealed record StartupSnapshot(
    PreferencesSnapshot Preferences,
    string? NotificationSettingsJson,
    string? ApplicationLanguage,
    string? WindowPlacement);

public sealed class StartupSnapshotRepository
{
    public async Task<StartupSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT value FROM app_meta WHERE key = 'notification_settings'),
                (SELECT value FROM app_meta WHERE key = 'application_language'),
                (SELECT value FROM app_meta WHERE key = 'main_window_position'),
                preferences.theme,
                preferences.sidebar_visible,
                preferences.message_font_size,
                preferences.shortcut_overrides_json,
                preferences.microphone_device_id
            FROM (SELECT 1) AS seed
            LEFT JOIN preferences ON preferences.id = 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new StartupSnapshot(new PreferencesSnapshot(), null, null, null);

        var preferences = new PreferencesSnapshot();
        if (!reader.IsDBNull(3))
        {
            preferences.Theme = Enum.TryParse<ThemeMode>(reader.GetString(3), out var theme)
                ? theme
                : ThemeMode.System;
            preferences.SidebarVisible = reader.GetInt64(4) != 0;
            preferences.MessageFontSize = Enum.TryParse<MessageFontSize>(reader.GetString(5), out var size)
                ? size
                : MessageFontSize.Md;
            preferences.MicrophoneDeviceId = reader.IsDBNull(7) ? "" : reader.GetString(7);
            try
            {
                preferences.ShortcutOverrides = reader.IsDBNull(6)
                    ? new()
                    : JsonSerializer.Deserialize(
                        reader.GetString(6), AppJsonContext.Default.DictionaryStringString) ?? new();
            }
            catch (JsonException)
            {
                preferences.ShortcutOverrides = new();
            }
        }

        return new StartupSnapshot(
            preferences,
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }
}
