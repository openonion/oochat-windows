using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.Services.Lifecycle;
using ConnectOnion.WinUIClient.Services.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ConnectOnion.WinUIClient
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        internal const string UiNotificationAgentEnvironmentVariable =
            "CONNECTONION_UI_NOTIFICATION_AGENT_ID";
        internal const string UiNotificationConversationEnvironmentVariable =
            "CONNECTONION_UI_NOTIFICATION_CONVERSATION_ID";
        internal const string UiStartupFailureEnvironmentVariable =
            "CONNECTONION_UI_STARTUP_FAILURE";

        private Window? _window;

        private readonly IHost? _host;
        private readonly Exception? _bootstrapFailure;
        private int _shutdownStarted;
        private static readonly DeferredActivationGate ActivationGate = new();

        /// <summary>
        /// The application's DI container, built by the Generic Host. This is the composition root
        /// the whole app resolves from — view models are constructed by it (see
        /// <see cref="ServiceRegistration"/>), and framework-created code-behind reaches shared
        /// singletons through <see cref="AppServices"/>, which is a typed accessor over this.
        /// </summary>
        public static IServiceProvider Services { get; private set; } = null!;

        /// <summary>Resolves a service from the container. Framework-created views (pages, dialogs,
        /// user controls) call this to obtain their view model, because WinUI constructs the views
        /// themselves through parameterless constructors rather than through the container.</summary>
        public static T GetService<T>() where T : notnull => Services.GetRequiredService<T>();

        /// <summary>
        /// The single top-level window. Created exactly once in <see cref="OnLaunched"/>;
        /// every subsequent activation (a redirected second launch, a taskbar/shell
        /// re-activation) reuses this instance via <see cref="ActivateMainWindow"/>.
        /// Used by code that needs an HWND (e.g. <c>FileOpenPicker</c>) but isn't itself
        /// a <see cref="Window"/> — most notably controls like <c>ChatComposer</c>,
        /// which is hosted inside a <see cref="Microsoft.UI.Xaml.Controls.Page"/> and
        /// has no HWND of its own.
        /// </summary>
        public static Window? MainWindow { get; private set; }

        /// <summary>
        /// Wakes the existing main window — restore from minimized/tray and bring to the
        /// foreground — without ever creating one. Safe to call from any thread (a
        /// redirected second launch arrives on a non-UI thread); the actual window work
        /// is marshalled to the UI thread. If the window does not exist yet (a redirect
        /// during cold start), the request is buffered and replayed by
        /// <see cref="FlushPendingActivation"/> once the window is created.
        /// </summary>
        public static void ActivateMainWindow() => ActivationGate.Request();

        /// <summary>Replays a buffered cold-start activation, if any. Called from
        /// <see cref="OnLaunched"/> once the single main window exists.</summary>
        internal static void FlushPendingActivation()
        {
            if (MainWindow is MainWindow main)
            {
                ActivationGate.Attach(
                    () => _ = main.DispatcherQueue.TryEnqueue(main.BringToForeground));
            }
        }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            Diagnostics.StartupTelemetry.Mark(Diagnostics.StartupPhases.ManagedEntry);
            try
            {
                InitializeComponent();
                AppStorage.EnsureDirectories();

                // Anything in the temporary avatar directory belongs to an Add Agent form that
                // never finished, so nothing in the database points at it. Sweep it off the
                // startup path rather than delaying the first frame.
                _ = Task.Run(AppStorage.PurgeTemporaryAgentIcons);

                // The framework categories below otherwise bury the application's own events.
                // The async sink keeps file I/O and its shared-file mutex off the UI thread.
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
                    .MinimumLevel.Override("Polly", LogEventLevel.Warning)
                    .Enrich.FromLogContext()
                    .WriteTo.Async(
                        sink => sink.File(
                            System.IO.Path.Combine(AppStorage.LogsDir, "connectonion-.log"),
                            formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                            rollingInterval: RollingInterval.Day,
                            rollOnFileSizeLimit: true,
                            fileSizeLimitBytes: 10 * 1024 * 1024,
                            retainedFileCountLimit: 14,
                            shared: true),
                        bufferSize: 20000)
                    .CreateLogger();

                // Build the composition root before any window/page is created.
                var builder = Host.CreateApplicationBuilder();
                builder.Services.AddAppServices();
                builder.Services.AddSerilog(Log.Logger, dispose: false);
                _host = builder.Build();
                Services = _host.Services;

                var loggerFactory = _host.Services.GetRequiredService<ILoggerFactory>();
                NotificationLog.Configure(loggerFactory);
                IdentityStore.Configure(loggerFactory);
                CoreStrings.Configure(LocalizedStrings.Get);
            }
            catch (Exception exception)
            {
                // Keep the Application object alive long enough for OnLaunched to show a native
                // recovery surface. This includes an unavailable data root, broken log path, or
                // composition-root failure — all of which previously exited with no visible clue.
                _bootstrapFailure = exception;
            }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            if (_bootstrapFailure is not null)
            {
                StartupFailurePresenter.Show(_bootstrapFailure, TryGetLogDirectory());
                await Log.CloseAndFlushAsync();
                Exit();
                return;
            }

            try
            {
                if (string.Equals(
                        Environment.GetEnvironmentVariable(UiStartupFailureEnvironmentVariable),
                        "1",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("UI automation requested a startup failure.");
                }

                // Deliberately sequential. Starting the host concurrently with window
                // construction was tried and measured: it changes nothing, because every hosted
                // service's StartAsync completes synchronously on this thread (the SQLite reads
                // return inline), so the returned task is already complete and there is nothing
                // to overlap with. It only moved `MainWindow`/`Closed` assignment after an await
                // and let the window exist before its services had loaded — risk with no payoff.
                // Making it real would mean pushing host start onto a background thread, which
                // OS notification registration is not obviously safe for.
                await _host!.StartAsync();
                Diagnostics.StartupTelemetry.Mark(Diagnostics.StartupPhases.HostStarted);
                _window = new MainWindow();
                Diagnostics.StartupTelemetry.Mark(Diagnostics.StartupPhases.WindowCreated);
                MainWindow = _window;
                _window.Closed += async (_, _) =>
                {
                    if (_window is MainWindow main) await main.SaveWindowPlacementAsync();
                    await ShutdownAsync();
                };
                _window.Activate();
                Diagnostics.StartupTelemetry.Mark(Diagnostics.StartupPhases.WindowActivated);
                // Arm only after Activate(): the first composition pass we want is this window's.
                Diagnostics.StartupTelemetry.TrackFirstFrame();
                HandleColdStartActivation();
                HandleUiAutomationNotificationActivation();
                FlushPendingActivation();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application startup failed");
                StartupFailurePresenter.Show(ex, TryGetLogDirectory());
                await ShutdownAsync();
                Exit();
            }
        }

        /// <summary>Stops active runs, hosted services, logging, and the service provider once.</summary>
        internal async Task ShutdownAsync()
        {
            if (System.Threading.Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;

            ActivationGate.Detach();

            if (_host is null)
            {
                await Log.CloseAndFlushAsync().ConfigureAwait(false);
                return;
            }

            var total = System.Diagnostics.Stopwatch.StartNew();
            var phase = System.Diagnostics.Stopwatch.StartNew();
            Log.Information("Application shutdown started");

            try
            {
                await AppServices.RunManager.ShutdownAsync()
                    .WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Run manager did not shut down cleanly");
            }

            Log.Information("Run manager shutdown completed in {ElapsedMs} ms", phase.ElapsedMilliseconds);
            phase.Restart();

            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Generic Host did not stop cleanly");
            }
            finally
            {
                Log.Information("Generic Host shutdown completed in {ElapsedMs} ms", phase.ElapsedMilliseconds);
                _host.Dispose();
                Log.Information("Application shutdown completed in {ElapsedMs} ms", total.ElapsedMilliseconds);
                await Log.CloseAndFlushAsync().ConfigureAwait(false);
            }
        }

        private static string? TryGetLogDirectory()
        {
            try { return AppStorage.LogsDir; }
            catch { return null; }
        }

        /// <summary>
        /// If the process was cold-started by clicking a toast, hand its arguments to
        /// the activation router. The router buffers them until the main window signals
        /// it's ready (see <c>MainWindow</c>'s notification wiring), so navigation never
        /// runs before the UI exists.
        /// </summary>
        private static void HandleColdStartActivation()
            => RouteNotificationActivation(Microsoft.Windows.AppLifecycle.AppInstance
                .GetCurrent().GetActivatedEventArgs());

        /// <summary>
        /// Drives the same buffered notification-navigation path from the isolated FlaUI process.
        /// Windows does not offer a deterministic way for an unpackaged CI process to synthesize
        /// a notification click, so the real-window suite supplies the target through two
        /// process-local environment variables. Ordinary launches never set them.
        /// </summary>
        private static void HandleUiAutomationNotificationActivation()
        {
            var agentId = Environment.GetEnvironmentVariable(UiNotificationAgentEnvironmentVariable);
            var conversationId = Environment.GetEnvironmentVariable(
                UiNotificationConversationEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(conversationId))
                return;

            AppServices.NotificationActivation.HandleColdStart(
                new System.Collections.Generic.Dictionary<string, string>
                {
                    ["action"] = "openConversation",
                    ["agentId"] = agentId,
                    ["conversationId"] = conversationId,
                });
        }

        /// <summary>
        /// A second launch — most commonly clicking a toast while the app is already
        /// running — is redirected here by <see cref="Program"/> (the process that
        /// received the activation exits before constructing a window). The running
        /// instance never re-runs <see cref="OnLaunched"/>, so this is the only place a
        /// warm-start toast click is honored: wake the window and, when the launch
        /// carried notification arguments, navigate to its target conversation.
        /// Fires on a non-UI thread; both calls below marshal their own UI work.
        /// </summary>
        internal static void OnActivationRedirected(Microsoft.Windows.AppLifecycle.AppActivationArguments args)
        {
            NotificationLog.Info($"OnActivationRedirected: kind={args?.Kind.ToString() ?? "null"}");
            ActivateMainWindow();
            RouteNotificationActivation(args);
        }

        /// <summary>Routes a notification activation (cold or warm start) to the activation
        /// router. Non-notification activations are ignored. Never throws.</summary>
        private static void RouteNotificationActivation(Microsoft.Windows.AppLifecycle.AppActivationArguments? activation)
        {
            try
            {
                if (activation?.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification
                    && activation.Data is Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs invoked)
                {
                    NotificationLog.Info("RouteNotificationActivation: AppNotification kind — handing to router");
                    AppServices.NotificationActivation.HandleColdStart(invoked.Arguments);
                }
                else
                {
                    NotificationLog.Info($"RouteNotificationActivation: not a notification (kind={activation?.Kind.ToString() ?? "null"}) — no navigation");
                }
            }
            catch (System.Exception ex)
            {
                NotificationLog.Warn("notification activation routing failed", ex);
            }
        }
    }
}
