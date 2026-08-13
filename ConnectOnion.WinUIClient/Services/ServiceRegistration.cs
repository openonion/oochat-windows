using System;
using System.Net.Http;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Services.Attachments;
using ConnectOnion.WinUIClient.Services.Notifications;
using ConnectOnion.WinUIClient.Services.Runtime;
using ConnectOnion.WinUIClient.Services.Speech;
using ConnectOnion.WinUIClient.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// The application's composition root. Every process-wide service and every view model is
/// registered here, then resolved from the <see cref="System.IServiceProvider"/> the Generic
/// Host builds (see <c>App</c>). Declaration order is irrelevant — unlike the old hand-rolled
/// locator, the container resolves the dependency graph on demand — but the *lifetime* is not:
/// the services below are the app's shared singletons, and the view models are transient so each
/// page gets a fresh one (a page's <c>Cleanup</c> disposes its subscriptions; a new navigation
/// resolves a new instance, matching the previous <c>new XyzViewModel()</c> per page).
/// </summary>
public static class ServiceRegistration
{
    /// <summary>Name of the resilient HttpClient every agent HTTP call flows through.</summary>
    private const string AgentHttpClient = "agent";
    private const string VoiceTranscriptionHttpClient = "voice-transcription";

    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        // ---- Shared infrastructure ----
        // Every agent HTTP call funnels through one shared HttpClient — the direct /info probe,
        // the relay lookup EndpointResolver makes with it, and agent-image downloads. Giving that
        // one client a Polly resilience handler (via Microsoft.Extensions.Http.Resilience) makes
        // all of them retry transient failures with jittered exponential backoff and bounds each
        // attempt, with no call-site changes. Deliberately no circuit breaker: presence probes must
        // keep trying a flaky agent rather than latch it "offline" until a breaker half-opens, and
        // the callers already impose their own overall timeouts.
        services.AddHttpClient(AgentHttpClient)
            .AddResilienceHandler("agent-http", pipeline =>
            {
                pipeline
                    .AddRetry(new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = 2,
                        Delay = TimeSpan.FromMilliseconds(300),
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                    })
                    .AddTimeout(TimeSpan.FromSeconds(10));
            });
        // Keep the app's single shared client (as before), now backed by the resilient handler
        // chain the factory builds for the named client above.
        services.AddSingleton<HttpClient>(sp =>
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(AgentHttpClient));
        // A transcription can legitimately take longer than an agent metadata probe, and retrying
        // a paid POST behind the user's back can charge twice. Keep this client bounded but without
        // the agent client's retry pipeline.
        services.AddHttpClient(VoiceTranscriptionHttpClient, client =>
            client.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<IUriLauncher, SystemUriLauncher>();
        services.AddSingleton<OpenOnionAccountService>();
        services.AddSingleton(sp => new VoiceTranscriptionService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(VoiceTranscriptionHttpClient),
            sp.GetRequiredService<OpenOnionAccountService>()));
        services.AddSingleton<ConnectionTester>();
        services.AddSingleton<IAgentIconService, AgentIconService>();

        // ---- SQLite repositories ----
        services.AddSingleton<AgentRepository>();
        services.AddSingleton<SessionRepository>();
        services.AddSingleton<SidebarStateRepository>();
        services.AddSingleton<PreferencesRepository>();
        services.AddSingleton<ConversationRepository>();
        services.AddSingleton<UsageRepository>();
        services.AddSingleton<StartupSnapshotRepository>();
        services.AddSingleton<WindowPlacementStore>();
        services.AddSingleton<LanguagePreferenceStore>();

        // ---- Keyboard ----
        // Singleton because it is the live binding table the window's key handlers consult on
        // every keystroke, and a rebind made in Settings has to be visible to them at once.
        services.AddSingleton<KeyboardShortcutService>();

        // ---- Accessibility ----
        // Singleton: it holds one OS event subscription, and the window reads it on every
        // layout pass. See TextScaleService for why WinUI 3 needs this done by hand.
        services.AddSingleton<TextScaleService>();

        // ---- Presence + runtime ----
        services.AddSingleton<AgentPresenceService>();
        // AgentSessionManager(HttpClient, ConversationRepository, UsageRepository?, ILogger?,
        // NotificationCoordinator?) — auto-wired. The optional parameters are filled from the
        // container when the type is registered and fall back to their defaults when it isn't,
        // which is what lets a headless test construct this with two arguments. The coordinator is
        // registered below; order does not matter to the container, and there is no cycle back to
        // this type.
        services.AddSingleton<AgentSessionManager>();

        // ---- Notification subsystem ----
        services.AddSingleton<WindowPresenceService>();
        services.AddSingleton<NotificationSettingsStore>();
        services.AddSingleton<WindowsNotificationService>();
        services.AddSingleton<InAppNotificationService>();
        services.AddSingleton<INotificationScheduler, ThreadingScheduler>();
        // The coordinator takes two INotificationChannel parameters (system + in-app), which the
        // container can't disambiguate positionally, and an onError delegate — so it's built by hand.
        services.AddSingleton<NotificationCoordinator>(sp => new NotificationCoordinator(
            systemChannel: sp.GetRequiredService<WindowsNotificationService>(),
            inAppChannel: sp.GetRequiredService<InAppNotificationService>(),
            presence: sp.GetRequiredService<WindowPresenceService>(),
            settings: sp.GetRequiredService<NotificationSettingsStore>(),
            scheduler: sp.GetRequiredService<INotificationScheduler>(),
            onError: NotificationLog.Warn,
            attentionStore: sp.GetRequiredService<SessionRepository>()));
        services.AddSingleton<ConversationNavigationService>();
        services.AddSingleton<NotificationActivationRouter>();

        // ---- Startup wiring (runs when the host starts) ----
        services.AddSingleton<StartupStateService>();
        services.AddHostedService(sp => sp.GetRequiredService<StartupStateService>());
        services.AddHostedService<NotificationStartupService>();
        services.AddHostedService<ImageCacheMaintenanceService>();

        // ---- View models (transient: one per page/dialog, resolved in each view's ctor) ----
        services.AddTransient<ChatViewModel>();
        services.AddTransient<AddAgentViewModel>();
        services.AddTransient<AgentDetailViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<UsageViewModel>();
        services.AddTransient<KeyboardShortcutsViewModel>();
        services.AddTransient<SessionSearchViewModel>();

        return services;
    }
}
