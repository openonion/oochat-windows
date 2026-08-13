using System;
using System.Globalization;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Data;
using Microsoft.Windows.Globalization;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Persists the UI language independently of Windows package state.
/// English is the explicit first-run default.
/// </summary>
public sealed class LanguagePreferenceStore
{
    private const string MetaKey = "application_language";

    public const string English = "en-US";
    public const string SimplifiedChinese = "zh-CN";

    public string Current { get; private set; } = English;

    public void ApplyLoaded(string? language)
    {
        Current = Normalize(language);
        try
        {
            ApplicationLanguages.PrimaryLanguageOverride = Current;
            ApplyDotNetCulture(Current);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Application language override could not be applied");
        }
    }

    public async Task LoadAndApplyAsync()
    {
        try
        {
            await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
            var loaded = await AppDatabase.GetMetaAsync(connection, MetaKey).ConfigureAwait(false);
            ApplyLoaded(loaded);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Application language could not be loaded; using English");
            ApplyLoaded(English);
        }

    }

    public async Task SaveAsync(string language)
    {
        var normalized = Normalize(language);
        await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
        await AppDatabase.SetMetaAsync(connection, null, MetaKey, normalized).ConfigureAwait(false);

        // Current represents the saved selection so reopening Settings shows what the user chose.
        // Do not apply either the WinUI override or the .NET culture in this process: existing
        // XAML resources have already been materialized, while runtime lookups would switch
        // immediately, producing a confusing half-old/half-new interface. LoadAndApplyAsync applies
        // the saved choice once, before the next main window is created.
        Current = normalized;
    }

    public static string Normalize(string? language)
        => string.Equals(language, SimplifiedChinese, StringComparison.OrdinalIgnoreCase)
            ? SimplifiedChinese
            : English;

    private static void ApplyDotNetCulture(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
