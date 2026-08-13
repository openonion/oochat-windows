using System;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace ConnectOnion.WinUIClient.Common;

/// <summary>
/// Resolves runtime-generated user-visible text from the app's PRI resources.
/// XAML surfaces should continue to use <c>x:Uid</c> with literal English fallback text.
/// </summary>
public static class LocalizedStrings
{
    private static readonly ConcurrentDictionary<string, byte> ReportedFallbacks = new();

    private static readonly Lazy<ResourceMap?> Resources = new(() =>
    {
        try
        {
            return new ResourceManager().MainResourceMap.TryGetSubtree("Resources");
        }
        catch (Exception ex)
        {
            // An unpackaged host with no PRI loaded still has to render its UI in English.
            Serilog.Log.Warning(ex, "Localization resources could not be loaded; using English");
            return null;
        }
    });

    /// <summary>Returns a localized value, or the supplied diagnosable English fallback.</summary>
    public static string Get(string key, string fallback)
    {
        try
        {
            var candidate = Resources.Value?.TryGetValue(key)?.ValueAsString;
            if (!string.IsNullOrEmpty(candidate))
                return candidate;
        }
        catch (Exception ex)
        {
            ReportFallbackOnce(key, ex);
            return fallback;
        }

        ReportFallbackOnce(key);
        return fallback;
    }

    /// <summary>Formats a localized resource using the active UI culture.</summary>
    public static string Format(string key, string fallbackFormat, params object?[] arguments)
    {
        var format = Get(key, fallbackFormat);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, arguments);
        }
        catch (FormatException ex)
        {
            // A malformed translation must not make the affected dialog or notification unusable.
            Serilog.Log.Warning(ex, "Localized format {ResourceKey} is invalid; using English", key);
            return string.Format(CultureInfo.CurrentCulture, fallbackFormat, arguments);
        }
    }

    private static void ReportFallbackOnce(string key, Exception? exception = null)
    {
        if (!ReportedFallbacks.TryAdd(key, 0))
            return;

        if (exception is null)
            Serilog.Log.Warning("Localization resource {ResourceKey} is missing; using English", key);
        else
            Serilog.Log.Warning(
                exception,
                "Localization resource {ResourceKey} could not be read; using English",
                key);
    }
}
