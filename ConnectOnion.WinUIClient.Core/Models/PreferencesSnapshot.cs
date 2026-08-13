using System.Text.Json.Serialization;

namespace ConnectOnion.WinUIClient.Models;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public enum MessageFontSize
{
    Sm,
    Md,
    Lg,
}

public enum InterfaceTextSize
{
    Small,
    Medium,
    Large,
}

public enum WindowCloseBehavior
{
    Ask,
    HideToTray,
    Exit,
}

/// <summary>
/// Desktop preferences (theme, sidebar, message font size, shortcut overrides).
/// Mirrors the TypeScript <c>PreferencesSnapshot</c> type.
/// </summary>
public sealed class PreferencesSnapshot
{
    private const string ComposerEnterKey = "composer.enterKey";
    private const string EnterNewLineValue = "newline";
    private const string WindowCloseBehaviorKey = "window.closeBehavior";
    private const string HideToTrayValue = "tray";
    private const string ExitValue = "exit";
    private const string WindowZoomKey = "window.zoomFactor";
    private const string InterfaceTextSizeKey = "ui.textSize";
    private const string VoiceCloudConsentKey = "voice.cloudTranscriptionConsent";

    [JsonPropertyName("theme")]
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    [JsonPropertyName("sidebarVisible")]
    public bool SidebarVisible { get; set; } = true;

    [JsonPropertyName("messageFontSize")]
    public MessageFontSize MessageFontSize { get; set; } = MessageFontSize.Md;

    [JsonPropertyName("shortcutOverrides")]
    public Dictionary<string, string> ShortcutOverrides { get; set; } = new();

    /// <summary>Selected capture device id, or empty for system default.</summary>
    [JsonPropertyName("microphoneDeviceId")]
    public string MicrophoneDeviceId { get; set; } = "";

    /// <summary>
    /// Whether the user has acknowledged that voice recordings are sent to OpenOnion for cloud
    /// transcription. Absence deliberately means false, so first use always asks before upload.
    /// </summary>
    [JsonIgnore]
    public bool VoiceCloudTranscriptionConsent
    {
        get => ShortcutOverrides.TryGetValue(VoiceCloudConsentKey, out var value)
               && string.Equals(value, bool.TrueString, System.StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
                ShortcutOverrides[VoiceCloudConsentKey] = bool.TrueString;
            else
                ShortcutOverrides.Remove(VoiceCloudConsentKey);
        }
    }

    [JsonIgnore]
    public bool EnterToSend
    {
        get => !ShortcutOverrides.TryGetValue(ComposerEnterKey, out var value)
               || !string.Equals(value, EnterNewLineValue, System.StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
            {
                ShortcutOverrides.Remove(ComposerEnterKey);
                return;
            }

            ShortcutOverrides[ComposerEnterKey] = EnterNewLineValue;
        }
    }

    /// <summary>
    /// The window's Ctrl+/- zoom, 1.0 for actual size. Shares the <see cref="ShortcutOverrides"/>
    /// bag with the other non-shortcut window preferences — <see cref="Data.SchemaMigrator"/>
    /// needs no new column and <c>KeyboardShortcutService.Rebuild</c> skips ids it does not know.
    ///
    /// Round-tripped through <see cref="CultureInfo.InvariantCulture"/> deliberately: the current
    /// culture would write "1,4" on a German or French install and then fail to parse it back,
    /// silently resetting the user's zoom on every launch.
    /// </summary>
    [JsonIgnore]
    public double ZoomFactor
    {
        get => ShortcutOverrides.TryGetValue(WindowZoomKey, out var value)
               && double.TryParse(
                   value,
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var parsed)
            ? parsed
            : 1.0;
        set
        {
            // Actual size is the default, so it is stored as the absence of a row rather than as
            // "1" — the same shape EnterToSend and CloseBehavior use for their defaults.
            if (System.Math.Abs(value - 1.0) < 0.001)
            {
                ShortcutOverrides.Remove(WindowZoomKey);
                return;
            }

            ShortcutOverrides[WindowZoomKey] =
                value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The app-wide text-size preset. It is kept in the extensible preference map so existing
    /// databases gain the setting without a schema migration; Medium is represented by no entry.
    /// </summary>
    [JsonIgnore]
    public InterfaceTextSize InterfaceTextSize
    {
        get => ShortcutOverrides.TryGetValue(InterfaceTextSizeKey, out var value)
               && Enum.TryParse<InterfaceTextSize>(value, ignoreCase: true, out var parsed)
            ? parsed
            : InterfaceTextSize.Medium;
        set
        {
            if (value == InterfaceTextSize.Medium)
            {
                ShortcutOverrides.Remove(InterfaceTextSizeKey);
                return;
            }

            ShortcutOverrides[InterfaceTextSizeKey] = value.ToString();
        }
    }

    [JsonIgnore]
    public WindowCloseBehavior CloseBehavior
    {
        get
        {
            if (!ShortcutOverrides.TryGetValue(WindowCloseBehaviorKey, out var value))
                return WindowCloseBehavior.Ask;
            if (string.Equals(value, HideToTrayValue, System.StringComparison.OrdinalIgnoreCase))
                return WindowCloseBehavior.HideToTray;
            if (string.Equals(value, ExitValue, System.StringComparison.OrdinalIgnoreCase))
                return WindowCloseBehavior.Exit;
            return WindowCloseBehavior.Ask;
        }
        set
        {
            switch (value)
            {
                case WindowCloseBehavior.HideToTray:
                    ShortcutOverrides[WindowCloseBehaviorKey] = HideToTrayValue;
                    break;
                case WindowCloseBehavior.Exit:
                    ShortcutOverrides[WindowCloseBehaviorKey] = ExitValue;
                    break;
                default:
                    ShortcutOverrides.Remove(WindowCloseBehaviorKey);
                    break;
            }
        }
    }
}
