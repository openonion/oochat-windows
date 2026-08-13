using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

public sealed class WindowPlacementPolicyTests
{
    [Theory]
    [InlineData("120,340", 120, 340, false)]
    [InlineData("-1920,-40,0", -1920, -40, false)]
    [InlineData("80,40,1", 80, 40, true)]
    public void TryParse_ValidInvariantCoordinates_RoundTrips(string value, int x, int y, bool maximized)
    {
        var position = WindowPlacementPolicy.TryParse(value);

        Assert.Equal(new WindowPlacement(new WindowPosition(x, y), maximized), position);
        Assert.Equal($"{x},{y},{(maximized ? 1 : 0)}", WindowPlacementPolicy.Serialize(position!.Value));
    }

    [Fact]
    public void TryParse_FiveFields_RoundTripsThePersistedSize()
    {
        var placement = WindowPlacementPolicy.TryParse("100,200,0,1280,860");

        Assert.Equal(
            new WindowPlacement(new WindowPosition(100, 200), false, new PixelSize(1280, 860)),
            placement);
        Assert.Equal("100,200,0,1280,860", WindowPlacementPolicy.Serialize(placement!.Value));
    }

    /// <summary>Rows written before the size was persisted stay readable, and reopen at the
    /// platform default rather than at some size this build invented for them.</summary>
    [Fact]
    public void TryParse_ThreeFieldRowFromAnOlderBuild_HasNoSize()
    {
        var placement = WindowPlacementPolicy.TryParse("80,40,1");

        Assert.NotNull(placement);
        Assert.Null(placement!.Value.Size);
    }

    /// <summary>A stored size below the shell's floor — an older build, or a hand-edited
    /// preferences row — is raised on read, so the window can never reopen unusable.</summary>
    [Fact]
    public void TryParse_SizeBelowTheMinimum_IsRaisedToIt()
    {
        var placement = WindowPlacementPolicy.TryParse("0,0,0,200,150");

        Assert.Equal(
            new PixelSize(WindowPlacementPolicy.MinimumWidth, WindowPlacementPolicy.MinimumHeight),
            placement!.Value.Size);
    }

    /// <summary>A corrupt size must not cost the user their window position too.</summary>
    [Theory]
    [InlineData("10,20,0,zero,860")]
    [InlineData("10,20,0,0,860")]
    [InlineData("10,20,0,-1280,860")]
    public void TryParse_MalformedSize_KeepsThePositionAndDropsTheSize(string value)
    {
        var placement = WindowPlacementPolicy.TryParse(value);

        Assert.Equal(new WindowPosition(10, 20), placement!.Value.Position);
        Assert.Null(placement.Value.Size);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("x,20")]
    [InlineData("10,20,30")]
    [InlineData("10,20,0,1280")]
    [InlineData("10,20,0,1280,860,7")]
    public void TryParse_InvalidValue_ReturnsNull(string? value)
        => Assert.Null(WindowPlacementPolicy.TryParse(value));

    [Fact]
    public void ClampToWorkArea_PositionAlreadyOnNegativeCoordinateMonitor_IsPreserved()
    {
        var result = WindowPlacementPolicy.ClampToWorkArea(
            new WindowPosition(-1700, 120),
            new PixelSize(1200, 800),
            new PixelRect(-1920, 0, 1920, 1080));

        Assert.Equal(new WindowPosition(-1700, 120), result);
    }

    [Fact]
    public void ClampToWorkArea_DisconnectedMonitor_MovesWindowFullyIntoNearestDisplay()
    {
        var result = WindowPlacementPolicy.ClampToWorkArea(
            new WindowPosition(2600, 1400),
            new PixelSize(1200, 800),
            new PixelRect(0, 0, 1920, 1040));

        Assert.Equal(new WindowPosition(720, 240), result);
    }

    [Fact]
    public void ClampToWorkArea_WindowLargerThanDisplay_KeepsCaptionAtWorkAreaOrigin()
    {
        var result = WindowPlacementPolicy.ClampToWorkArea(
            new WindowPosition(500, 400),
            new PixelSize(2200, 1400),
            new PixelRect(0, 40, 1920, 1000));

        Assert.Equal(new WindowPosition(0, 40), result);
    }
}
