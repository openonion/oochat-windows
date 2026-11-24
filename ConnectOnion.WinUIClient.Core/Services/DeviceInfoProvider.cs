using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// 
/// </summary>
public sealed class DeviceInfoProvider
{
    private const int Windows11minBuild = 22000;

    /// <summary>
    /// Reads facts about the current machine for Identity information. 
    /// </summary>
    public DeviceInfo GetCurrent()
    {
        string deviceName = Environment.MachineName;
        int osBuild = Environment.OSVersion.Version.Build;
        bool hasBattery = ReadHasBatter();

        string osName = GetOsName(osBuild);
        DeviceFormFactor formFactor = GetFormFactor(hasBattery);

        return new DeviceInfo(deviceName, osName, formFactor);
    }

    internal static string GetOsName(int osBuild)
    {
        if (osBuild >= Windows11minBuild)
        {
            return "Windows 11";

        }
        else
        {
            return "Windows 10";
        }
    }

    internal static DeviceFormFactor GetFormFactor(bool hasBattery)
    {
        if (hasBattery)
        {
            return DeviceFormFactor.Laptop;
        }
        else
        {
            return DeviceFormFactor.Desktop;
        }
    }

    private static bool ReadHasBatter()
    {
        SystemPowerStatus status;
        bool success = GetSystemPowerStatus(out status);

        if (!success)
        {
            return false;
        }
        // BatteryFlag: 128 = No system battery, 255 = Unknown status
        if (status.BatteryFlag == 128 || status.BatteryFlag == 255)
        {
            return false;
        }
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

}
