using System.Security.Cryptography;
using System.Text;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>
/// Builds the process-wide ownership name for a data root. Windows AppLifecycle instance keys
/// are scoped to a particular packaged identity or unpackaged executable registration, so two
/// portable builds extracted to different folders can both consider themselves primary. The
/// data-root key closes that gap without preventing isolated automation profiles from running
/// side by side.
/// </summary>
public static class DataRootInstanceIdentity
{
    private const string Prefix = @"Local\ConnectOnion.Desktop.DataRoot.";

    public static string ForPath(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot))
            .ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Prefix + Convert.ToHexString(digest);
    }
}
