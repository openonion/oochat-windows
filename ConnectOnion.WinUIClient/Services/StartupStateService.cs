using System;
using System.Threading;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Loads every pre-window preference through one SQLite connection, then hydrates the in-memory
/// services consumed by startup. Registered before notification activation so those policies see
/// the persisted values without each service opening the database independently.
/// </summary>
public sealed class StartupStateService : IHostedService
{
    private static readonly Action<ILogger, Exception?> LogSnapshotLoadFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1, "StartupSnapshotLoadFailed"),
            "Startup state could not be loaded; using defaults");

    private readonly StartupSnapshotRepository _repository;
    private readonly NotificationSettingsStore _notifications;
    private readonly KeyboardShortcutService _shortcuts;
    private readonly LanguagePreferenceStore _language;
    private readonly WindowPlacementStore _placement;
    private readonly ILogger<StartupStateService> _logger;

    public PreferencesSnapshot Preferences { get; private set; } = new();

    public StartupStateService(
        StartupSnapshotRepository repository,
        NotificationSettingsStore notifications,
        KeyboardShortcutService shortcuts,
        LanguagePreferenceStore language,
        WindowPlacementStore placement,
        ILogger<StartupStateService> logger)
    {
        _repository = repository;
        _notifications = notifications;
        _shortcuts = shortcuts;
        _language = language;
        _placement = placement;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        StartupSnapshot snapshot;
        try
        {
            snapshot = await _repository.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSnapshotLoadFailed(_logger, ex);
            snapshot = new StartupSnapshot(new PreferencesSnapshot(), null, null, null);
        }
        Preferences = snapshot.Preferences;
        _notifications.ApplySerialized(snapshot.NotificationSettingsJson);
        _shortcuts.ApplySnapshot(snapshot.Preferences);
        _language.ApplyLoaded(snapshot.ApplicationLanguage);
        _placement.ApplyLoaded(snapshot.WindowPlacement);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
