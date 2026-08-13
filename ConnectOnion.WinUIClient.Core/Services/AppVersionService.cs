using System.Reflection;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Resolves the user-facing app version and copyright line. The version is never hard-coded:
/// packaged builds report the MSIX identity version (the one users actually see in Settings →
/// Apps), unpackaged builds fall back to the assembly's informational version. Both values are
/// resolved once and cached, since neither can change while the process runs.
/// </summary>
public static class AppVersionService
{
    private static readonly Lazy<string> LazyDisplayVersion = new(ResolveDisplayVersion);

    /// <summary>Marketing-style version, e.g. "1.0.0". Never empty — falls back to "1.0.0".</summary>
    public static string DisplayVersion => LazyDisplayVersion.Value;

    /// <summary>"Version 1.0.0", ready to render.</summary>
    public static string VersionText => $"Version {DisplayVersion}";

    /// <summary>"© 2026 ConnectOnion" — the year is taken from the clock, not baked in.</summary>
    public static string CopyrightText => $"© {DateTime.Now.Year} ConnectOnion";

    private static string ResolveDisplayVersion()
    {
        // Packaged (MSIX): the identity version is the one shown to users, so it wins.
        // Package.Current throws when running unpackaged (dotnet run -p:RunUnpackaged=true).
        try
        {
            var packageVersion = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{packageVersion.Major}.{packageVersion.Minor}.{packageVersion.Build}";
        }
        catch
        {
            // Not packaged; fall through to the assembly metadata.
        }

        var assembly = typeof(AppVersionService).Assembly;

        // InformationalVersion carries any SourceLink suffix ("1.0.0+<sha>") — trim it.
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            var trimmed = plus >= 0 ? informational[..plus] : informational;
            if (!string.IsNullOrWhiteSpace(trimmed)) return trimmed;
        }

        var version = assembly.GetName().Version;
        return version is null
            ? "1.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
