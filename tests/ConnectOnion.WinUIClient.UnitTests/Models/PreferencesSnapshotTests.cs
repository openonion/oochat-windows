using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

public sealed class PreferencesSnapshotTests
{
    [Fact]
    public void VoiceCloudTranscriptionConsent_DefaultsToFalseAndRoundTripsInSharedMap()
    {
        var preferences = new PreferencesSnapshot
        {
            ShortcutOverrides = new Dictionary<string, string> { ["other"] = "keep" },
        };

        Assert.False(preferences.VoiceCloudTranscriptionConsent);

        preferences.VoiceCloudTranscriptionConsent = true;

        Assert.True(preferences.VoiceCloudTranscriptionConsent);
        Assert.Equal("True", preferences.ShortcutOverrides["voice.cloudTranscriptionConsent"]);
        Assert.Equal("keep", preferences.ShortcutOverrides["other"]);

        preferences.VoiceCloudTranscriptionConsent = false;

        Assert.False(preferences.VoiceCloudTranscriptionConsent);
        Assert.False(preferences.ShortcutOverrides.ContainsKey("voice.cloudTranscriptionConsent"));
        Assert.Equal("keep", preferences.ShortcutOverrides["other"]);
    }

    [Fact]
    public void InterfaceTextSize_DefaultsToMediumWithoutStoredOverride()
    {
        var preferences = new PreferencesSnapshot();

        Assert.Equal(InterfaceTextSize.Medium, preferences.InterfaceTextSize);
        Assert.False(preferences.ShortcutOverrides.ContainsKey("ui.textSize"));
    }

    [Theory]
    [InlineData(InterfaceTextSize.Small, "Small")]
    [InlineData(InterfaceTextSize.Medium, null)]
    [InlineData(InterfaceTextSize.Large, "Large")]
    public void InterfaceTextSize_RoundTripsThroughSharedPreferenceMap(
        InterfaceTextSize size,
        string? storedValue)
    {
        var preferences = new PreferencesSnapshot();

        preferences.InterfaceTextSize = size;

        Assert.Equal(size, preferences.InterfaceTextSize);
        if (storedValue is null)
            Assert.False(preferences.ShortcutOverrides.ContainsKey("ui.textSize"));
        else
            Assert.Equal(storedValue, preferences.ShortcutOverrides["ui.textSize"]);
    }

    [Fact]
    public void InterfaceTextSize_UnknownStoredValue_FallsBackToMedium()
    {
        var preferences = new PreferencesSnapshot
        {
            ShortcutOverrides = new Dictionary<string, string>
            {
                ["ui.textSize"] = "future-value",
            },
        };

        Assert.Equal(InterfaceTextSize.Medium, preferences.InterfaceTextSize);
    }

    [Fact]
    public void EnterToSend_Disabled_SetsNewlineOverride()
    {
        var preferences = new PreferencesSnapshot();

        preferences.EnterToSend = false;

        Assert.False(preferences.EnterToSend);
        Assert.Equal("newline", preferences.ShortcutOverrides["composer.enterKey"]);
    }

    [Fact]
    public void EnterToSend_Reenabled_RemovesOnlyComposerOverride()
    {
        var preferences = new PreferencesSnapshot
        {
            ShortcutOverrides = new Dictionary<string, string>
            {
                ["composer.enterKey"] = "newline",
                ["other"] = "keep",
            },
        };

        preferences.EnterToSend = true;

        Assert.True(preferences.EnterToSend);
        Assert.False(preferences.ShortcutOverrides.ContainsKey("composer.enterKey"));
        Assert.Equal("keep", preferences.ShortcutOverrides["other"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    public void ZoomFactor_MissingOrInvalidValueDefaultsToActualSize(string? storedValue)
    {
        var preferences = new PreferencesSnapshot();
        if (storedValue is not null)
            preferences.ShortcutOverrides["window.zoomFactor"] = storedValue;

        Assert.Equal(1.0, preferences.ZoomFactor);
    }

    [Theory]
    [InlineData(0.67, "0.67")]
    [InlineData(1.4, "1.4")]
    [InlineData(2.0, "2")]
    public void ZoomFactor_RoundTripsUsingInvariantStorage(double zoom, string storedValue)
    {
        var preferences = new PreferencesSnapshot();

        preferences.ZoomFactor = zoom;

        Assert.Equal(storedValue, preferences.ShortcutOverrides["window.zoomFactor"]);
        Assert.Equal(zoom, preferences.ZoomFactor, precision: 2);
    }

    [Fact]
    public void ZoomFactor_ActualSizeRemovesOnlyZoomOverride()
    {
        var preferences = new PreferencesSnapshot
        {
            ShortcutOverrides = new Dictionary<string, string>
            {
                ["window.zoomFactor"] = "1.4",
                ["other"] = "keep",
            },
        };

        preferences.ZoomFactor = 1.0005;

        Assert.False(preferences.ShortcutOverrides.ContainsKey("window.zoomFactor"));
        Assert.Equal("keep", preferences.ShortcutOverrides["other"]);
    }

    [Theory]
    [InlineData(WindowCloseBehavior.Ask, null)]
    [InlineData(WindowCloseBehavior.HideToTray, "tray")]
    [InlineData(WindowCloseBehavior.Exit, "exit")]
    public void CloseBehavior_RoundTripsThroughSharedPreferenceMap(
        WindowCloseBehavior behavior,
        string? storedValue)
    {
        var preferences = new PreferencesSnapshot
        {
            ShortcutOverrides = new Dictionary<string, string>
            {
                ["other"] = "keep",
            },
        };

        preferences.CloseBehavior = behavior;

        Assert.Equal(behavior, preferences.CloseBehavior);
        Assert.Equal("keep", preferences.ShortcutOverrides["other"]);
        if (storedValue is null)
            Assert.False(preferences.ShortcutOverrides.ContainsKey("window.closeBehavior"));
        else
            Assert.Equal(storedValue, preferences.ShortcutOverrides["window.closeBehavior"]);
    }

    [Fact]
    public void CloseBehavior_UnknownStoredValue_FallsBackToAsk()
    {
        var preferences = new PreferencesSnapshot
        {
            ShortcutOverrides = new Dictionary<string, string>
            {
                ["window.closeBehavior"] = "future-value",
            },
        };

        Assert.Equal(WindowCloseBehavior.Ask, preferences.CloseBehavior);
    }
}
