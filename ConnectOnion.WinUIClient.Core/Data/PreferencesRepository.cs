using System.Text.Json;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Data;

/// <summary>
/// Local persistence for desktop preferences. Port of <c>preferencesStorage.ts</c>.
/// The web app kept each setting under its own key; here they live in one
/// SQLite row.
///
/// Exactly one row, pinned by <c>CHECK (id = 1)</c> in the schema — so "load preferences" is
/// a single-row read and a save is one upsert, with no possibility of a second conflicting
/// row appearing.
/// </summary>
public sealed class PreferencesRepository
{
    /// <summary>Reads the preference row, or a fully-defaulted snapshot on first run.
    /// Never throws for bad *content*: an unrecognized enum or unreadable JSON falls back to
    /// its default, because a preference the app can't parse must not stop it starting.</summary>
    public async Task<PreferencesSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT theme, sidebar_visible, message_font_size, shortcut_overrides_json, microphone_device_id
            FROM preferences
            WHERE id = 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        // No row yet (first run) — the defaults on PreferencesSnapshot are the answer, and
        // nothing is written until the user actually changes something.
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new PreferencesSnapshot();

        // Enums are stored by name (see SaveAsync), so TryParse is what tolerates a value
        // written by a newer build or a member that has since been renamed — falling back to
        // the default rather than throwing on an enum the user can just re-pick.
        var snapshot = new PreferencesSnapshot
        {
            Theme = Enum.TryParse<ThemeMode>(reader.GetString(0), out var theme) ? theme : ThemeMode.System,
            SidebarVisible = reader.GetInt32(1) != 0,
            MessageFontSize = Enum.TryParse<MessageFontSize>(reader.GetString(2), out var size) ? size : MessageFontSize.Md,
            MicrophoneDeviceId = reader.IsDBNull(4) ? "" : reader.GetString(4),
        };

        try
        {
            snapshot.ShortcutOverrides =
                JsonSerializer.Deserialize(reader.GetString(3), AppJsonContext.Default.DictionaryStringString) ?? new();
        }
        catch
        {
            // Unreadable override map → no overrides, i.e. every shortcut falls back to its
            // catalog default. KeyboardShortcutService is built to tolerate exactly this, so
            // the app stays fully operable rather than losing its keyboard entirely.
            snapshot.ShortcutOverrides = new();
        }

        return snapshot;
    }

    public async Task SaveAsync(PreferencesSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO preferences (id, theme, sidebar_visible, message_font_size, shortcut_overrides_json, microphone_device_id)
            VALUES (1, $theme, $sidebar_visible, $message_font_size, $shortcut_overrides_json, $mic)
            ON CONFLICT(id) DO UPDATE SET
                theme = excluded.theme,
                sidebar_visible = excluded.sidebar_visible,
                message_font_size = excluded.message_font_size,
                shortcut_overrides_json = excluded.shortcut_overrides_json,
                microphone_device_id = excluded.microphone_device_id;
            """;
        // Enums persisted by name, not ordinal: the table stays legible in a SQLite browser,
        // and reordering or inserting an enum member can't silently change what a stored
        // preference means.
        AppDatabase.Add(command, "$theme", snapshot.Theme.ToString());
        AppDatabase.Add(command, "$sidebar_visible", snapshot.SidebarVisible ? 1 : 0);
        AppDatabase.Add(command, "$message_font_size", snapshot.MessageFontSize.ToString());
        // shortcut_overrides_json is a shared extensible map: it holds keyboard rebinds plus
        // small compatibility preferences such as composer.enterKey and window.closeBehavior.
        // Callers must merge into the existing map rather than replace it wholesale, or one
        // feature's save will wipe another feature's setting.
        AppDatabase.Add(command, "$shortcut_overrides_json", JsonSerializer.Serialize(snapshot.ShortcutOverrides, AppJsonContext.Default.DictionaryStringString));
        AppDatabase.Add(command, "$mic", snapshot.MicrophoneDeviceId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
