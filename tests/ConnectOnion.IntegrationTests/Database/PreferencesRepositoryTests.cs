using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.IntegrationTests.Database;

[Collection(DatabaseCollection.Name)]
public sealed class PreferencesRepositoryTests
{
    private readonly PreferencesRepository _repository = new();

    [Fact]
    public async Task LoadAsync_NoStoredRow_ReturnsDocumentedDefaults()
    {
        await DeleteRowAsync();

        var preferences = await _repository.LoadAsync();

        Assert.Equal(ThemeMode.System, preferences.Theme);
        Assert.True(preferences.SidebarVisible);
        Assert.Equal(MessageFontSize.Md, preferences.MessageFontSize);
        Assert.Equal(InterfaceTextSize.Medium, preferences.InterfaceTextSize);
        Assert.Empty(preferences.ShortcutOverrides);
        Assert.Equal("", preferences.MicrophoneDeviceId);
        Assert.False(preferences.VoiceCloudTranscriptionConsent);
        Assert.True(preferences.EnterToSend);
    }

    [Fact]
    public async Task SaveAsync_AllFields_RoundTripsExactly()
    {
        var expected = new PreferencesSnapshot
        {
            Theme = ThemeMode.Dark,
            SidebarVisible = false,
            MessageFontSize = MessageFontSize.Lg,
            MicrophoneDeviceId = "microphone-1",
            ShortcutOverrides = new Dictionary<string, string>
            {
                ["composer.enterKey"] = "newline",
                ["custom"] = "value",
            },
            InterfaceTextSize = InterfaceTextSize.Large,
            VoiceCloudTranscriptionConsent = true,
        };

        await _repository.SaveAsync(expected);
        var actual = await _repository.LoadAsync();

        Assert.Equal(expected.Theme, actual.Theme);
        Assert.Equal(expected.SidebarVisible, actual.SidebarVisible);
        Assert.Equal(expected.MessageFontSize, actual.MessageFontSize);
        Assert.Equal(expected.InterfaceTextSize, actual.InterfaceTextSize);
        Assert.Equal(expected.MicrophoneDeviceId, actual.MicrophoneDeviceId);
        Assert.Equal(expected.ShortcutOverrides, actual.ShortcutOverrides);
        Assert.True(actual.VoiceCloudTranscriptionConsent);
        Assert.False(actual.EnterToSend);
    }

    [Fact]
    public async Task SaveAsync_CalledTwice_UpdatesSingleConstrainedRow()
    {
        await _repository.SaveAsync(new PreferencesSnapshot { Theme = ThemeMode.Light });
        await _repository.SaveAsync(new PreferencesSnapshot { Theme = ThemeMode.Dark });

        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), MAX(theme) FROM preferences;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal("Dark", reader.GetString(1));
    }

    [Fact]
    public async Task LoadAsync_InvalidEnumsAndShortcutJson_FallsBackWithoutThrowing()
    {
        await using (var connection = await AppDatabase.OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO preferences (id, theme, sidebar_visible, message_font_size, shortcut_overrides_json, microphone_device_id)
                VALUES (1, 'UnknownTheme', 1, 'Huge', '{bad json', '')
                ON CONFLICT(id) DO UPDATE SET
                    theme = excluded.theme,
                    sidebar_visible = excluded.sidebar_visible,
                    message_font_size = excluded.message_font_size,
                    shortcut_overrides_json = excluded.shortcut_overrides_json,
                    microphone_device_id = excluded.microphone_device_id;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var preferences = await _repository.LoadAsync();

        Assert.Equal(ThemeMode.System, preferences.Theme);
        Assert.Equal(MessageFontSize.Md, preferences.MessageFontSize);
        Assert.Empty(preferences.ShortcutOverrides);
    }

    [Fact]
    public async Task StartupSnapshot_LoadsAllPreWindowStateThroughOneRepository()
    {
        await _repository.SaveAsync(new PreferencesSnapshot
        {
            Theme = ThemeMode.Dark,
            SidebarVisible = false,
            MessageFontSize = MessageFontSize.Lg,
            ShortcutOverrides = new Dictionary<string, string> { ["file.newChat"] = "Ctrl+Shift+N" },
            InterfaceTextSize = InterfaceTextSize.Small,
            MicrophoneDeviceId = "microphone-startup",
        });
        await using (var connection = await AppDatabase.OpenAsync())
        {
            await AppDatabase.SetMetaAsync(connection, null, "notification_settings", "{\"enabled\":true}");
            await AppDatabase.SetMetaAsync(connection, null, "application_language", "zh-CN");
            await AppDatabase.SetMetaAsync(connection, null, "main_window_position", "1,2,800,600");
        }

        var snapshot = await new StartupSnapshotRepository().LoadAsync();

        Assert.Equal(ThemeMode.Dark, snapshot.Preferences.Theme);
        Assert.False(snapshot.Preferences.SidebarVisible);
        Assert.Equal(MessageFontSize.Lg, snapshot.Preferences.MessageFontSize);
        Assert.Equal(InterfaceTextSize.Small, snapshot.Preferences.InterfaceTextSize);
        Assert.Equal("Ctrl+Shift+N", snapshot.Preferences.ShortcutOverrides["file.newChat"]);
        Assert.Equal("microphone-startup", snapshot.Preferences.MicrophoneDeviceId);
        Assert.Equal("{\"enabled\":true}", snapshot.NotificationSettingsJson);
        Assert.Equal("zh-CN", snapshot.ApplicationLanguage);
        Assert.Equal("1,2,800,600", snapshot.WindowPlacement);
    }

    private static async Task DeleteRowAsync()
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM preferences;";
        await command.ExecuteNonQueryAsync();
    }
}
