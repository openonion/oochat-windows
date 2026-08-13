using System;
using System.Text.Json;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models.Notifications;

namespace ConnectOnion.WinUIClient.Services.Notifications;

/// <summary>
/// Persists <see cref="NotificationSettings"/> as JSON in the <c>app_meta</c>
/// key-value table (no <c>preferences</c> schema change). Holds the current
/// snapshot in memory so the policy reads it synchronously via
/// <see cref="INotificationSettingsProvider"/>.
/// </summary>
public sealed class NotificationSettingsStore : INotificationSettingsProvider
{
    private const string MetaKey = "notification_settings";
    private volatile NotificationSettings _current = new();

    public NotificationSettings Current => _current;

    public void ApplySerialized(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize(json, AppJsonContext.Default.NotificationSettings);
            if (loaded is not null) _current = loaded;
        }
        catch (JsonException ex)
        {
            NotificationLog.Warn("settings content was unreadable; using defaults", ex);
        }
    }

    /// <summary>Loads persisted settings; falls back to defaults on any error.</summary>
    public async Task LoadAsync()
    {
        try
        {
            await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
            var json = await AppDatabase.GetMetaAsync(connection, MetaKey).ConfigureAwait(false);
            ApplySerialized(json);
        }
        catch (Exception ex)
        {
            NotificationLog.Warn("settings load failed; using defaults", ex);
        }
    }

    /// <summary>Updates the in-memory snapshot immediately and persists it.</summary>
    public async Task SaveAsync(NotificationSettings settings)
    {
        _current = settings;
        try
        {
            await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
            var json = JsonSerializer.Serialize(settings, AppJsonContext.Default.NotificationSettings);
            await AppDatabase.SetMetaAsync(connection, null, MetaKey, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            NotificationLog.Warn("settings save failed", ex);
        }
    }
}
