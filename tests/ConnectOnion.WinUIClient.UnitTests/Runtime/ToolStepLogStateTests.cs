using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Runtime;

/// <summary>The per-step expanded-log toolbar state: enlarge and the copy "Copied" flash. These
/// are transient UI state on the step, driven by the toolbar's command and the view's copy handler
/// — pinned here so the toggles and their derived labels/height stay wired correctly.</summary>
public sealed class ToolStepLogStateTests
{
    [Fact]
    public void NewStep_DefaultsToDefaultHeight()
    {
        var step = new ToolStepViewModel();

        Assert.False(step.IsLogEnlarged);
        Assert.Equal(280, step.LogMaxHeight);
        Assert.Equal("Copy", step.CopyLabel);
        Assert.Equal("Expand", step.EnlargeLabel);
        // A short log that fits shows no Expand button.
        Assert.False(step.CanEnlargeLog);
        Assert.False(step.ShowEnlargeButton);
    }

    [Fact]
    public void ShowEnlargeButton_OnlyWhenOverflowingOrEnlarged()
    {
        var step = new ToolStepViewModel();
        Assert.False(step.ShowEnlargeButton);

        // Log overflows the default cap -> Expand becomes useful.
        step.CanEnlargeLog = true;
        Assert.True(step.ShowEnlargeButton);

        // Even if a later measure says it fits, an already-enlarged log keeps the button so it can
        // be collapsed back.
        step.CanEnlargeLog = false;
        step.IsLogEnlarged = true;
        Assert.True(step.ShowEnlargeButton);
    }

    [Fact]
    public void ToggleLogSize_FlipsHeightAndLabel()
    {
        var step = new ToolStepViewModel();

        step.ToggleLogSizeCommand.Execute(null);
        Assert.True(step.IsLogEnlarged);
        Assert.Equal(560, step.LogMaxHeight);
        Assert.Equal("Collapse", step.EnlargeLabel);

        step.ToggleLogSizeCommand.Execute(null);
        Assert.False(step.IsLogEnlarged);
        Assert.Equal(280, step.LogMaxHeight);
        Assert.Equal("Expand", step.EnlargeLabel);
    }

    [Fact]
    public void JustCopied_SwapsCopyLabel()
    {
        var step = new ToolStepViewModel();

        Assert.Equal("Copy", step.CopyLabel);

        step.JustCopied = true;
        Assert.Equal("Copied", step.CopyLabel);

        step.JustCopied = false;
        Assert.Equal("Copy", step.CopyLabel);
    }

    /// <summary>The per-step duration is deliberately quiet: a genuine zero and a missing
    /// measurement both render blank (no column of "0 ms" to scan past), and a sub-millisecond call
    /// reads as "&lt;1 ms" rather than rounding down into that same blank.</summary>
    [Theory]
    [InlineData(null, "")]
    [InlineData(0d, "")]
    [InlineData(0.4, "<1 ms")]
    [InlineData(13d, "13 ms")]
    [InlineData(999d, "999 ms")]
    [InlineData(1500d, "1.5 s")]
    public void DurationLabel_HidesZero_AndFloorsSubMillisecond(double? durationMs, string expected)
    {
        var step = new ToolStepViewModel { DurationMs = durationMs };

        Assert.Equal(expected, step.DurationLabel);
    }

    [Fact]
    public void EnlargeLabel_NotifiesWhenSizeToggles()
    {
        var step = new ToolStepViewModel();
        var changed = new List<string?>();
        step.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        step.IsLogEnlarged = true;

        Assert.Contains(nameof(step.LogMaxHeight), changed);
        Assert.Contains(nameof(step.EnlargeLabel), changed);
    }
}
