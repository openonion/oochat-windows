using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class DeviceInfoProviderTests
{
    [Theory]
    [InlineData(19045, "Windows 10")]
    [InlineData(21999, "Windows 10")]
    [InlineData(22000, "Windows 11")]
    [InlineData(26100, "Windows 11")]
    public void GetOsName_UsesBuildNumberBoundary(int build, string expected)
    {
        Assert.Equal(expected, DeviceInfoProvider.GetOsName(build));
    }

    [Fact]
    public void GetFormFactor_WithBattery_ReturnsLaptop()
    {
        Assert.Equal(DeviceFormFactor.Laptop, DeviceInfoProvider.GetFormFactor(true));
    }

    [Fact]
    public void GetFormFactor_WithoutBattery_ReturnsDesktop()
    {
        Assert.Equal(DeviceFormFactor.Desktop, DeviceInfoProvider.GetFormFactor(false));
    }

    //test against the real machine. 
    [Fact]
    public void GetCurrent_OnRealMachine_ReturnsSaneValues()
    {
        DeviceInfoProvider provider = new DeviceInfoProvider();

        DeviceInfo info = provider.GetCurrent();

        Assert.False(string.IsNullOrWhiteSpace(info.DeviceName));
        Assert.Equal(Environment.MachineName, info.DeviceName);
        Assert.Contains(info.OsName, new[] { "Windows 10", "Windows 11" });
        Assert.True(Enum.IsDefined(info.FormFactor));
    }
}
