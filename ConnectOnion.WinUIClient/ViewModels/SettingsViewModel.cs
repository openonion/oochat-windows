using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;
using Windows.Devices.Enumeration;
using Windows.Foundation;

namespace ConnectOnion.WinUIClient.ViewModels;

/// <summary>
/// Backs the settings page. Preferences are held in one <see cref="PreferencesSnapshot"/> and
/// exposed as get-only projections over it — every write goes through an explicit
/// <c>Set…Async</c> so persisting is never an accidental side effect of a binding.
///
/// Each setter is guarded by <c>_loaded</c> and an equality check, which together make settings
/// writes idempotent: populating the page cannot save, and re-picking the current value is a
/// no-op rather than a redundant database round-trip.
/// </summary>
public sealed partial class SettingsViewModel : Common.ObservableObject
{
    private readonly PreferencesRepository _repo;
    private readonly TextScaleService _textScale;
    private PreferencesSnapshot _prefs = new();
    /// <summary>False until <see cref="LoadAsync"/> finishes. Gates every save, so the load's
    /// own assignments cannot write the freshly-read values straight back.</summary>
    private bool _loaded;

    public ThemeMode Theme => _prefs.Theme;
    public InterfaceTextSize InterfaceTextSize => _prefs.InterfaceTextSize;
    public MessageFontSize MessageFontSize => _prefs.MessageFontSize;
    public bool EnterToSend => _prefs.EnterToSend;
    public string MicrophoneDeviceId => _prefs.MicrophoneDeviceId;
    public WindowCloseBehavior CloseBehavior => _prefs.CloseBehavior;

    public IReadOnlyList<MicrophoneItem> Microphones { get; private set; } = [];

    // -1 default seeded in the ctor (partial-property declarations can't carry an initializer);
    // it fires the hook while _loaded is still false, so it no-ops.
    public SettingsViewModel(PreferencesRepository preferences, TextScaleService textScale)
    {
        _repo = preferences;
        _textScale = textScale;
        SelectedMicIndex = -1;
    }

    [ObservableProperty]
    public partial int SelectedMicIndex { get; set; }

    /// <summary>Updates the selected row and persists it as one observable operation so the page
    /// can report a failed save instead of losing an exception from a fire-and-forget hook.</summary>
    public async Task SetSelectedMicrophoneAsync(int value)
    {
        SelectedMicIndex = value;
        if (_loaded && value >= 0 && value < Microphones.Count)
            await SetMicrophoneAsync(Microphones[value].DeviceId);
    }

    public async Task LoadAsync()
    {
        _prefs = await _repo.LoadAsync();

        Microphones = await EnumerateMicrophonesAsync();

        // Restore the saved microphone, falling back to the "System default" row when that
        // device is gone — unplugging a headset must not leave the picker on a dead entry.
        // FirstOrDefault on a tuple yields (null, 0) when nothing matches, so the index is
        // checked rather than trusted; index 0 is the synthetic default row added below.
        var idx = Microphones
            .Select((m, i) => (m, i))
            .FirstOrDefault(x => x.m.DeviceId == _prefs.MicrophoneDeviceId).i;
        if (idx < 0)
            idx = Microphones.Select((m, i) => (m, i))
                .FirstOrDefault(x => x.m.IsDefault).i;
        SelectedMicIndex = idx >= 0 ? idx : 0;

        // Manual notifications: these are computed from _prefs, which was replaced wholesale
        // above, so nothing raised a change for them.
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(InterfaceTextSize));
        OnPropertyChanged(nameof(MessageFontSize));
        OnPropertyChanged(nameof(EnterToSend));
        OnPropertyChanged(nameof(Microphones));

        // Flipped last: only now do user-driven SelectedMicIndex changes persist (see the hook).
        _loaded = true;
    }

    public async Task SetThemeAsync(ThemeMode theme)
    {
        if (!_loaded || _prefs.Theme == theme) return;
        _prefs.Theme = theme;
        // Applied before the await so the UI flips immediately; the save is allowed to trail.
        ThemeService.Apply(theme);
        OnPropertyChanged(nameof(Theme));
        await _repo.SaveAsync(_prefs);
    }

    public async Task SetMessageFontSizeAsync(MessageFontSize size)
    {
        if (!_loaded || _prefs.MessageFontSize == size) return;
        _prefs.MessageFontSize = size;
        // Broadcast before the await so open chat views resize immediately; the save trails,
        // matching SetThemeAsync. Without this an already-open ChatPage keeps its old size until
        // it reloads, so the setting looks broken.
        MessageFontService.Apply(size);
        OnPropertyChanged(nameof(MessageFontSize));
        await _repo.SaveAsync(_prefs);
    }

    public async Task SetInterfaceTextSizeAsync(InterfaceTextSize size)
    {
        if (!_loaded || _prefs.InterfaceTextSize == size) return;
        _prefs.InterfaceTextSize = size;
        _textScale.ApplyInterfaceTextSize(size);
        OnPropertyChanged(nameof(InterfaceTextSize));
        await _repo.SaveAsync(_prefs);
    }

    public async Task SetEnterToSendAsync(bool enterToSend)
    {
        if (!_loaded || _prefs.EnterToSend == enterToSend) return;
        _prefs.EnterToSend = enterToSend;
        OnPropertyChanged(nameof(EnterToSend));
        await _repo.SaveAsync(_prefs);
    }

    /// <summary>
    /// Changes what the title-bar close button does. <see cref="MainWindow"/> caches this value
    /// after its first read, so it is told to re-read rather than left holding the old one until
    /// the next launch.
    /// </summary>
    public async Task SetCloseBehaviorAsync(WindowCloseBehavior behavior)
    {
        if (!_loaded || _prefs.CloseBehavior == behavior) return;
        _prefs.CloseBehavior = behavior;
        OnPropertyChanged(nameof(CloseBehavior));
        await _repo.SaveAsync(_prefs);
        MainWindow.InvalidateCloseBehaviorCache();
    }

    private async Task SetMicrophoneAsync(string deviceId)
    {
        if (!_loaded || _prefs.MicrophoneDeviceId == deviceId) return;
        _prefs.MicrophoneDeviceId = deviceId;
        await _repo.SaveAsync(_prefs);
    }

    /// <summary>Enumerates capture devices for the microphone picker. Always returns at least
    /// the synthetic "System default" row (empty device id), which is what the app stores when
    /// the user has expressed no preference.</summary>
    private static async Task<IReadOnlyList<MicrophoneItem>> EnumerateMicrophonesAsync()
    {
        // Wrapped in Task.Run and hand-adapted through a TCS: DeviceInformation.FindAllAsync is
        // a WinRT IAsyncOperation, and device enumeration can block for a noticeable moment on
        // machines with many audio endpoints. Pushing it off the UI thread keeps the settings
        // page from hitching when it opens.
        var devices = await Task.Run(() =>
        {
            var op = DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
            var tcs = new TaskCompletionSource<DeviceInformationCollection>();
            op.Completed = (_, status) =>
            {
                if (status == AsyncStatus.Completed)
                    tcs.SetResult(op.GetResults());
                else if (status == AsyncStatus.Error)
                    tcs.SetException(op.ErrorCode);
                else
                    tcs.SetCanceled();
            };
            return tcs.Task;
        });
        var list = new List<MicrophoneItem>(devices.Count + 1)
        {
            new("System default", "", IsDefault: true),
        };
        // Disabled endpoints are filtered out: Windows keeps reporting devices that are present
        // but switched off, and offering one would silently record no audio.
        foreach (var d in devices)
        {
            if (d.IsEnabled)
                list.Add(new(d.Name, d.Id));
        }
        return list;
    }
}

public sealed record MicrophoneItem(string Name, string DeviceId, bool IsDefault = false);
