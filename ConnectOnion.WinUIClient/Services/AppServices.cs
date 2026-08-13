using System;
using System.Net.Http;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Services.Notifications;
using ConnectOnion.WinUIClient.Services.Runtime;
using ConnectOnion.WinUIClient.Services.Speech;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Typed accessor over the application's DI container (built by the Generic Host in <c>App</c>;
/// registrations live in <see cref="ServiceRegistration"/>). It exists because WinUI instantiates
/// windows, pages and user controls itself — through their parameterless constructors, never the
/// container — so framework-created code-behind that needs a shared service resolves it here rather
/// than through constructor injection (view models, which the container *does* create, take their
/// dependencies as ctor parameters instead). Every member below returns the same singleton the
/// container holds.
/// </summary>
public static class AppServices
{
    private static IServiceProvider Provider => App.Services;

    public static AgentRepository Agents => Provider.GetRequiredService<AgentRepository>();
    public static SessionRepository Sessions => Provider.GetRequiredService<SessionRepository>();
    public static SidebarStateRepository SidebarState => Provider.GetRequiredService<SidebarStateRepository>();
    public static PreferencesRepository Preferences => Provider.GetRequiredService<PreferencesRepository>();
    public static ConversationRepository Conversations => Provider.GetRequiredService<ConversationRepository>();
    public static WindowPlacementStore WindowPlacement => Provider.GetRequiredService<WindowPlacementStore>();
    public static TextScaleService TextScale => Provider.GetRequiredService<TextScaleService>();
    public static LanguagePreferenceStore Language => Provider.GetRequiredService<LanguagePreferenceStore>();
    public static LanguagePreferenceStore LanguagePreference => Provider.GetRequiredService<LanguagePreferenceStore>();
    public static StartupStateService StartupState => Provider.GetRequiredService<StartupStateService>();

    /// <summary>Token-usage ledger. Deliberately independent of conversation/agent lifetime —
    /// deleting a chat never erases the record that its tokens were spent.</summary>
    public static UsageRepository Usage => Provider.GetRequiredService<UsageRepository>();

    /// <summary>Session-lived cache of each agent's online status.</summary>
    public static AgentPresenceService Presence => Provider.GetRequiredService<AgentPresenceService>();

    /// <summary>Picks, processes, commits and deletes the user's chosen agent icons.</summary>
    public static IAgentIconService AgentIcons => Provider.GetRequiredService<IAgentIconService>();

    /// <summary>Logger source for framework-created code-behind, which cannot be handed an
    /// <c>ILogger&lt;T&gt;</c> through a constructor the way an injected type is.</summary>
    public static ILoggerFactory Logging => Provider.GetRequiredService<ILoggerFactory>();

    /// <summary>The live shortcut bindings (catalog defaults overlaid with the user's rebinds).
    /// The window's key handlers ask this rather than testing hard-coded key codes.</summary>
    public static KeyboardShortcutService Shortcuts => Provider.GetRequiredService<KeyboardShortcutService>();

    /// <summary>Single shared HttpClient for health probes and endpoint resolution.</summary>
    public static HttpClient Http => Provider.GetRequiredService<HttpClient>();

    /// <summary>Process-memory OpenOnion authentication and account balance.</summary>
    public static OpenOnionAccountService OpenOnionAccount
        => Provider.GetRequiredService<OpenOnionAccountService>();

    /// <summary>Cloud speech-to-text client authenticated by the installation identity.</summary>
    public static VoiceTranscriptionService VoiceTranscription
        => Provider.GetRequiredService<VoiceTranscriptionService>();

    // Opens external links. A test can substitute a fake by assigning this override — the
    // underlying Launcher is a static WinRT type that cannot be mocked directly; when unset the
    // container's registered IUriLauncher is used.
    private static IUriLauncher? _uriLauncherOverride;
    public static IUriLauncher UriLauncher
    {
        get => _uriLauncherOverride ?? Provider.GetRequiredService<IUriLauncher>();
        set => _uriLauncherOverride = value;
    }

    /// <summary>
    /// App-level owner of agent connections and in-flight turns. Pages/view models submit
    /// sends here and subscribe to run updates rather than holding a connection or a
    /// background task, so a reply keeps streaming (and persists) across page switches,
    /// tray minimize, and re-opening the conversation.
    /// </summary>
    public static AgentSessionManager RunManager => Provider.GetRequiredService<AgentSessionManager>();

    /// <summary>Per-window focus/visibility + which conversation each window shows.</summary>
    public static WindowPresenceService WindowPresence => Provider.GetRequiredService<WindowPresenceService>();

    /// <summary>Persisted notification preferences (also the policy's settings source).</summary>
    public static NotificationSettingsStore NotificationSettings => Provider.GetRequiredService<NotificationSettingsStore>();

    /// <summary>The one place that decides whether/how to notify.</summary>
    public static NotificationCoordinator Notifications => Provider.GetRequiredService<NotificationCoordinator>();

    /// <summary>Routes a clicked notification to the right window/conversation.</summary>
    public static ConversationNavigationService Navigation => Provider.GetRequiredService<ConversationNavigationService>();

    /// <summary>Registers for and dispatches App Notification activations.</summary>
    public static NotificationActivationRouter NotificationActivation => Provider.GetRequiredService<NotificationActivationRouter>();
}
