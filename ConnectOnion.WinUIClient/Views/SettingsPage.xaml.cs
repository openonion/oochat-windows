using System;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.Services.Notifications;
using ConnectOnion.WinUIClient.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient.Views;

/// <summary>
/// Settings, as stacked <c>SettingsCard</c> sections filtered by the category rail and the
/// search box.
///
/// The controls here are driven imperatively (assign <c>IsOn</c>/<c>SelectedIndex</c> on
/// load, save on the change event) rather than two-way bound. That is deliberate for the
/// enum pickers: theme and font size are <c>ComboBox</c> indices, so a binding would need a
/// converter per enum in both directions, and the index→enum mapping would end up split
/// between XAML and code anyway. The cost is <see cref="_initializing"/> — see below.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly SettingsViewModel _vm = App.GetService<SettingsViewModel>();

    /// <summary>Suppresses the save handlers while <see cref="OnLoaded"/> populates the
    /// controls. Without it, every assignment below raises Toggled/SelectionChanged, and
    /// opening the page would write the settings straight back — harmless for equal values,
    /// but it also fires the side effects (theme re-apply, a settings write per control).</summary>
    private bool _initializing;

    /// <summary>Remembered so <see cref="Filter"/> can restore the chosen category when the
    /// search box is cleared, rather than defaulting back to General.</summary>
    private string _currentCategory = "General";

    public SettingsPage()
    {
        InitializeComponent();
        ThemeChoice.ItemsSource = new string[]
        {
            LocalizedStrings.Get("SettingsThemeSystem", "System"),
            LocalizedStrings.Get("SettingsThemeLight", "Light"),
            LocalizedStrings.Get("SettingsThemeDark", "Dark"),
        };
        InterfaceTextSizeChoice.ItemsSource = new string[]
        {
            LocalizedStrings.Get("SettingsFontSmall", "Small"),
            LocalizedStrings.Get("SettingsFontMedium", "Medium"),
            LocalizedStrings.Get("SettingsFontLarge", "Large"),
        };
        FontChoice.ItemsSource = new string[]
        {
            LocalizedStrings.Get("SettingsFontSmall", "Small"),
            LocalizedStrings.Get("SettingsFontMedium", "Medium"),
            LocalizedStrings.Get("SettingsFontLarge", "Large"),
        };
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _initializing = true;
        try
        {
            await _vm.LoadAsync();

            // Everything between here and the reset must stay assignment-only — any call that
            // could await would let a real user toggle land while the guard is up and be dropped.
            ThemeChoice.SelectedIndex = _vm.Theme switch
            {
                ThemeMode.Light => 1,
                ThemeMode.Dark => 2,
                _ => 0,
            };
            InterfaceTextSizeChoice.SelectedIndex = _vm.InterfaceTextSize switch
            {
                InterfaceTextSize.Small => 0,
                InterfaceTextSize.Large => 2,
                _ => 1,
            };
            LanguageChoice.SelectedIndex =
                AppServices.LanguagePreference.Current == LanguagePreferenceStore.SimplifiedChinese
                    ? 1
                    : 0;
            FontChoice.SelectedIndex = _vm.MessageFontSize switch
            {
                MessageFontSize.Sm => 0,
                MessageFontSize.Lg => 2,
                _ => 1,
            };
            EnterToSendSwitch.IsOn = _vm.EnterToSend;
            CloseBehaviorChoice.SelectedIndex = _vm.CloseBehavior switch
            {
                WindowCloseBehavior.HideToTray => 1,
                WindowCloseBehavior.Exit => 2,
                _ => 0,
            };

            MicChoice.ItemsSource = _vm.Microphones;
            MicChoice.SelectedIndex = _vm.SelectedMicIndex;

            LoadNotificationSettings();

        }
        catch (Exception ex)
        {
            ShowSettingsError(
                ex,
                "Settings page could not be initialized",
                LocalizedStrings.Get(
                    "SettingsLoadErrorMessage",
                    "Some settings couldn't be loaded. Close Settings and try again."));
        }
        finally
        {
            _initializing = false;
        }
    }

    private void LoadNotificationSettings()
    {
        var s = AppServices.NotificationSettings.Current;
        SystemNotificationsUnavailableInfoBar.IsOpen = !AppNotificationCapability.IsAvailable;
        EnableNotificationsSwitch.IsOn = s.EnableNotifications;
        NotifyAgentRepliesSwitch.IsOn = s.NotifyAgentReplies;
        NotifyTaskCompletionSwitch.IsOn = s.NotifyTaskCompletion;
        NotifyApprovalRequestsSwitch.IsOn = s.NotifyApprovalRequests;
        NotifyConnectionProblemsSwitch.IsOn = s.NotifyConnectionProblems;
        PlayNotificationSoundSwitch.IsOn = s.PlayNotificationSound;
        ShowMessagePreviewSwitch.IsOn = s.ShowMessagePreview;
        NotificationDetailPanel.IsEnabled = s.EnableNotifications;
    }

    private async void EnableNotificationsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        // The enable/disable of the detail panel happens *before* the _initializing check, so
        // the master switch still greys out its dependents while the page is populating.
        // Only the persistence below is suppressed.
        NotificationDetailPanel.IsEnabled = EnableNotificationsSwitch.IsOn;
        if (_initializing) return;
        await SaveSettingAsync(SaveNotificationSettingsAsync, "Notification settings could not be saved");
    }

    private async void NotificationDetail_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        await SaveSettingAsync(SaveNotificationSettingsAsync, "Notification settings could not be saved");
    }

    /// <summary>Rebuilds the whole settings object from the switches and saves it. Writing
    /// all seven every time (rather than patching one field) keeps the saved state a
    /// straight snapshot of what is on screen, so a half-applied save is not possible.
    /// Note the detail switches are read even when the master switch is off — their values
    /// are preserved, so turning notifications back on restores the user's prior choices.</summary>
    private System.Threading.Tasks.Task SaveNotificationSettingsAsync()
    {
        var settings = new Models.Notifications.NotificationSettings
        {
            EnableNotifications = EnableNotificationsSwitch.IsOn,
            NotifyAgentReplies = NotifyAgentRepliesSwitch.IsOn,
            NotifyTaskCompletion = NotifyTaskCompletionSwitch.IsOn,
            NotifyApprovalRequests = NotifyApprovalRequestsSwitch.IsOn,
            NotifyConnectionProblems = NotifyConnectionProblemsSwitch.IsOn,
            PlayNotificationSound = PlayNotificationSoundSwitch.IsOn,
            ShowMessagePreview = ShowMessagePreviewSwitch.IsOn,
        };
        return AppServices.NotificationSettings.SaveAsync(settings);
    }

    private async void ThemeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var theme = ThemeChoice.SelectedIndex switch
        {
            1 => ThemeMode.Light,
            2 => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        await SaveSettingAsync(() => _vm.SetThemeAsync(theme), "Theme setting could not be saved");
    }

    private async void LanguageChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageChoice.SelectedIndex < 0) return;
        SettingsErrorBar.IsOpen = false;

        var language = LanguageChoice.SelectedIndex switch
        {
            1 => LanguagePreferenceStore.SimplifiedChinese,
            _ => LanguagePreferenceStore.English,
        };
        if (string.Equals(
                AppServices.LanguagePreference.Current,
                language,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await AppServices.LanguagePreference.SaveAsync(language);
            LanguageRestartInfo.IsOpen = true;
        }
        catch (Exception ex)
        {
            ShowSettingsError(ex, "Application language could not be saved");
        }
    }

    private async void FontChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var size = FontChoice.SelectedIndex switch
        {
            0 => MessageFontSize.Sm,
            2 => MessageFontSize.Lg,
            _ => MessageFontSize.Md,
        };
        await SaveSettingAsync(() => _vm.SetMessageFontSizeAsync(size), "Message font setting could not be saved");
    }

    private async void InterfaceTextSizeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var size = InterfaceTextSizeChoice.SelectedIndex switch
        {
            0 => InterfaceTextSize.Small,
            2 => InterfaceTextSize.Large,
            _ => InterfaceTextSize.Medium,
        };
        await SaveSettingAsync(
            () => _vm.SetInterfaceTextSizeAsync(size),
            "Interface text-size setting could not be saved");
    }

    private async void EnterToSendSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        await SaveSettingAsync(
            () => _vm.SetEnterToSendAsync(EnterToSendSwitch.IsOn),
            "Enter-to-send setting could not be saved");
    }

    /// <summary>
    /// Restarts the app so the new language takes effect.
    ///
    /// <c>AppInstance.Restart</c> is the Windows App SDK call that survives the single-instance
    /// redirection in <c>Program</c> — a plain <c>Process.Start</c> of our own exe would be
    /// redirected straight back into the instance that is trying to exit.
    /// </summary>
    private async void LanguageRestartNow_Click(object sender, RoutedEventArgs e)
    {
        SettingsErrorBar.IsOpen = false;
        try
        {
            // Drain runs and flush the database first; Restart terminates this process outright,
            // so anything not shut down here is simply lost.
            if (Application.Current is App app)
                await app.ShutdownAsync();
            Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
        }
        catch (Exception ex)
        {
            // The setting is already saved either way — the user can restart by hand.
            ShowSettingsError(
                ex,
                "Could not restart to apply the language change",
                LocalizedStrings.Get(
                    "SettingsRestartErrorMessage",
                    "ConnectOnion couldn't restart automatically. Restart it manually to apply the language."));
        }
    }

    private async void CloseBehaviorChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || CloseBehaviorChoice.SelectedIndex < 0) return;

        var behavior = CloseBehaviorChoice.SelectedIndex switch
        {
            1 => WindowCloseBehavior.HideToTray,
            2 => WindowCloseBehavior.Exit,
            _ => WindowCloseBehavior.Ask,
        };
        await SaveSettingAsync(
            () => _vm.SetCloseBehaviorAsync(behavior),
            "Window close behavior could not be saved");
    }

    private async void MicChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        await SaveSettingAsync(
            () => _vm.SetSelectedMicrophoneAsync(MicChoice.SelectedIndex),
            "Microphone setting could not be saved");
    }

    private async Task SaveSettingAsync(Func<Task> save, string logMessage)
    {
        SettingsErrorBar.IsOpen = false;
        try
        {
            await save();
        }
        catch (Exception ex)
        {
            ShowSettingsError(ex, logMessage);
        }
    }

    private void ShowSettingsError(Exception exception, string logMessage, string? fallbackMessage = null)
    {
        Serilog.Log.Error(exception, logMessage);
        SettingsErrorBar.Message = fallbackMessage ?? LocalizedStrings.Get(
            "SettingsSaveErrorMessage",
            "ConnectOnion couldn't save this change. Try again.");
        SettingsErrorBar.IsOpen = true;
    }

    /// <summary>Called by the settings shell when the user picks a category in the left rail.
    /// Sections are shown/hidden rather than navigated, so scroll and control state survive
    /// switching categories.</summary>
    public void SelectCategory(string category)
    {
        _currentCategory = category;
        SearchEmptyState.Visibility = Visibility.Collapsed;

        // General groups the app-wide preferences: appearance, window behaviour, chat and audio.
        var isGeneral = category == "General";
        GeneralSection.Visibility = isGeneral ? Visibility.Visible : Visibility.Collapsed;
        WindowSection.Visibility = isGeneral ? Visibility.Visible : Visibility.Collapsed;
        ChatSection.Visibility = isGeneral ? Visibility.Visible : Visibility.Collapsed;
        AudioSection.Visibility = isGeneral ? Visibility.Visible : Visibility.Collapsed;

        NotificationsSection.Visibility = category == "Notifications" ? Visibility.Visible : Visibility.Collapsed;
        ContentScrollViewer.ChangeView(null, 0, null, true);
    }

    /// <summary>
    /// Settings search. Matches against a hand-written keyword string per section rather
    /// than the rendered text, so a user can find "dark" or "newline" even though neither
    /// word is a card header — but it also means <b>adding a setting requires adding its
    /// words to the relevant line below</b>, or it becomes unfindable.
    ///
    /// Search deliberately overrides the category rail: while a query is active, any section
    /// can show regardless of the selected category, and clearing the box restores it.
    /// </summary>
    public void Filter(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            SelectCategory(_currentCategory);
            return;
        }

        var value = query.Trim();
        var showGeneral = Contains(value, "general appearance theme light dark system language english chinese interface text size small medium large 中文 语言 界面 文字 字号 大小");
        var showWindow = Contains(value, "general window close closing exit quit tray minimize background 关闭 退出 托盘");
        var showChat = Contains(value, "general chat message text size font enter send newline 聊天 消息 字体 发送 换行");
        var showNotifications = Contains(value, "notifications agent replies task completion approval connection sound preview 通知 回复 完成 审批 连接 声音 预览");
        var showAudio = Contains(value, "audio microphone voice input 音频 麦克风 语音 输入");

        GeneralSection.Visibility = showGeneral ? Visibility.Visible : Visibility.Collapsed;
        WindowSection.Visibility = showWindow ? Visibility.Visible : Visibility.Collapsed;
        ChatSection.Visibility = showChat ? Visibility.Visible : Visibility.Collapsed;
        NotificationsSection.Visibility = showNotifications ? Visibility.Visible : Visibility.Collapsed;
        AudioSection.Visibility = showAudio ? Visibility.Visible : Visibility.Collapsed;
        SearchEmptyState.Visibility = showGeneral || showWindow || showChat || showNotifications || showAudio
            ? Visibility.Collapsed
            : Visibility.Visible;
        ContentScrollViewer.ChangeView(null, 0, null, true);
    }

    // Note the direction: the *keyword list* is searched for the user's query, not the other
    // way round. So "the" matches (it is a substring of "theme") — a prefix-ish match that
    // happens to behave well for incremental typing, which is what this box gets.
    private static bool Contains(string query, string terms)
        => terms.Contains(query, StringComparison.OrdinalIgnoreCase);
}
