using System;
using System.Collections.Generic;
using System.Text;

namespace ConnectOnion.WinUIClient.Models;

public enum DeviceFormFactor
{
    Desktop,
    Laptop
}

public sealed record DeviceInfo(string DeviceName, string OsName,
    DeviceFormFactor FormFactor);
