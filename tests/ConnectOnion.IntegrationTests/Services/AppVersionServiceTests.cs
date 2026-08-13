using System.Reflection;
using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.IntegrationTests.Services;

public sealed class AppVersionServiceTests
{
    [Fact]
    public void DisplayVersion_UnpackagedBuild_UsesInformationalVersionWithoutSourceSuffix()
    {
        var informational = typeof(AppVersionService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var expected = informational.Split('+')[0];

        Assert.Equal(expected, AppVersionService.DisplayVersion);
        Assert.DoesNotContain("+", AppVersionService.DisplayVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionText_DisplayVersion_AddsUserFacingPrefix()
    {
        Assert.Equal($"Version {AppVersionService.DisplayVersion}", AppVersionService.VersionText);
    }

    [Fact]
    public void CopyrightText_CurrentClock_UsesCurrentYear()
    {
        Assert.Equal($"© {DateTime.Now.Year} ConnectOnion", AppVersionService.CopyrightText);
    }
}
