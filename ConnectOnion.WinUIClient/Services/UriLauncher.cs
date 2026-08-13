using System;
using System.Threading.Tasks;
using Windows.System;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Opens a URI in whatever the OS considers the default handler.
///
/// This exists purely so callers do not bind directly to <see cref="Launcher"/>, which is a static
/// WinRT type and therefore impossible to substitute in a test — meaning any code path that opens a
/// link (today: Help → ConnectOnion Docs) could not be verified without actually launching a
/// browser. Tests swap <see cref="AppServices.UriLauncher"/> for a fake and assert the URI.
/// </summary>
public interface IUriLauncher
{
    /// <summary>Returns false when the shell declined to open the URI (no handler registered,
    /// blocked by policy). Implementations do not throw for that case — a failed launch is a
    /// normal outcome the caller is expected to surface.</summary>
    Task<bool> LaunchAsync(Uri uri);
}

/// <summary>The real one: hands the URI to the Windows shell.</summary>
public sealed class SystemUriLauncher : IUriLauncher
{
    public async Task<bool> LaunchAsync(Uri uri)
    {
        try
        {
            return await Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // Treat a throwing shell the same as a refusing one; the caller shows the same message.
            return false;
        }
    }
}
