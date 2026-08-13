using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.ViewModels;

/// <summary>
/// Backs the Usage panel in Settings: per-model token totals for a chosen time window, read
/// straight out of the usage ledger with SQL aggregation.
///
/// Shows tokens, never money. The wire protocol carries no pricing, an agent can be backed by any
/// model, and rates change — a hard-coded price table would be a confidently-stated lie. If cost
/// ever ships it has to be a rate table the user maintains, labelled as their own estimate.
/// </summary>
public sealed partial class UsageViewModel : Common.ObservableObject
{
    private readonly UsageRepository _usage;
    private bool _invariantsLoaded;
    private DateTimeOffset? _cachedFirstRecorded;
    private IReadOnlyList<DailyUsageTotal> _cachedDaily = [];

    public UsageViewModel(UsageRepository usage)
    {
        _usage = usage;
        Range = UsageRange.Last7Days;
        FirstRecordedText = "";
    }

    /// <summary>One row per model, biggest spender first.</summary>
    public ObservableCollection<UsageRowViewModel> Rows { get; } = new();

    public IReadOnlyList<UsageRange> Ranges { get; } = new[]
    {
        UsageRange.Today, UsageRange.Last7Days, UsageRange.Last30Days, UsageRange.AllTime,
    };

    [ObservableProperty]
    public partial UsageRange Range { get; set; }

    /// <summary>Changes the visible window and completes only after the replacement rows load, so
    /// the view can surface repository failures instead of losing them from a fire-and-forget hook.</summary>
    public async Task SetRangeAsync(UsageRange value)
    {
        if (Range == value) return;
        Range = value;
        await LoadAsync();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    public partial bool IsLoading { get; private set; }

    /// <summary>"Recording since 3 Jul 2026", or empty when nothing has been recorded. Makes it
    /// explicit that history begins when the feature shipped rather than at first launch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFirstRecorded))]
    public partial string FirstRecordedText { get; private set; }

    /// <summary>
    /// The twelve-month activity grid. Deliberately independent of <see cref="Range"/>: the
    /// heatmap's whole job is the long view, so re-cutting it to "Today" whenever the table above
    /// it is filtered would leave a one-square map and destroy the only thing it shows. It is
    /// therefore loaded once per <see cref="LoadAsync"/> from the full ledger, not from the
    /// window the table uses.
    /// </summary>
    [ObservableProperty]
    public partial UsageHeatmap? Heatmap { get; private set; }

    public bool HasFirstRecorded => !string.IsNullOrEmpty(FirstRecordedText);

    public bool ShowEmptyState => !IsLoading && Rows.Count == 0;

    public bool ShowContent => !IsLoading && Rows.Count > 0;

    // ---- Totals across every model in the window ----
    //
    // Summed in memory rather than with a second aggregate query: Rows is already the complete
    // set for the window (one row per model, so tens at most), and deriving the totals from the
    // same data the table shows makes it impossible for the header and the rows to disagree.
    // These are computed properties with no change notification of their own — RaiseTotals is
    // what tells the UI they moved.

    public long TotalTokens => Rows.Sum(r => r.TotalTokens);
    public long TotalInputTokens => Rows.Sum(r => r.InputTokens);
    public long TotalOutputTokens => Rows.Sum(r => r.OutputTokens);
    public long TotalCalls => Rows.Sum(r => r.Calls);

    public string TotalTokensText => UsageFormat.Tokens(TotalTokens);
    public string TotalInputTokensText => UsageFormat.Tokens(TotalInputTokens);
    public string TotalOutputTokensText => UsageFormat.Tokens(TotalOutputTokens);
    public string TotalCallsText => TotalCalls.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            // ConfigureAwait(true) throughout: everything after these awaits touches the
            // ObservableCollection and the bound properties, which must happen on the UI thread.
            // Only the last 13 months are asked for, not the whole ledger: everything older is
            // discarded by the projection anyway, and the ledger grows without bound. The extra
            // month covers the partial week the grid pads backwards into.
            var heatmapSince = DateTimeOffset.Now.AddMonths(-13).ToUnixTimeMilliseconds();
            var summariesTask = _usage.GetByModelAsync(Range.SinceUnixMs());
            var firstTask = _invariantsLoaded
                ? Task.FromResult(_cachedFirstRecorded)
                : _usage.GetFirstRecordedAsync();
            var dailyTask = _invariantsLoaded
                ? Task.FromResult(_cachedDaily)
                : _usage.GetDailyTotalsAsync(heatmapSince);
            await Task.WhenAll(summariesTask, firstTask, dailyTask).ConfigureAwait(true);

            var summaries = await summariesTask.ConfigureAwait(true);
            var first = await firstTask.ConfigureAwait(true);
            var daily = await dailyTask.ConfigureAwait(true);
            _cachedFirstRecorded = first;
            _cachedDaily = daily;
            _invariantsLoaded = true;
            Heatmap = UsageHeatmap.Build(daily, DateOnly.FromDateTime(DateTime.Now));

            Rows.Clear();

            // Each row's share bar is relative to the biggest model in the window, not to the sum,
            // so a single dominant model doesn't squash every other bar into invisibility.
            var max = summaries.Count == 0 ? 0 : summaries.Max(s => s.TotalTokens);
            foreach (var s in summaries) Rows.Add(new UsageRowViewModel(s, max));

            FirstRecordedText = first is null
                ? ""
                : LocalizedStrings.Format(
                    "UsageRecordingSince",
                    "Recording since {0:d MMM yyyy}",
                    first.Value.ToLocalTime());
        }
        finally
        {
            // In a finally so a failed read still clears the spinner and recomputes the header
            // — otherwise a transient database error would leave the panel loading forever.
            IsLoading = false;
            RaiseTotals();
        }
    }

    /// <summary>Erases the ledger. Only ever called from an explicit, confirmed user action — no
    /// other code path deletes usage, and deleting a conversation never does.</summary>
    public async Task ClearAllAsync()
    {
        await _usage.ClearAsync().ConfigureAwait(true);
        _invariantsLoaded = false;
        _cachedFirstRecorded = null;
        _cachedDaily = [];
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>Notifies every property derived from <see cref="Rows"/>. Needed because mutating
    /// an <c>ObservableCollection</c> raises collection-changed, not property-changed for things
    /// computed from it — without this the header would keep last window's numbers.
    /// Adding a computed total above means adding it here too.</summary>
    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(TotalTokens));
        OnPropertyChanged(nameof(TotalInputTokens));
        OnPropertyChanged(nameof(TotalOutputTokens));
        OnPropertyChanged(nameof(TotalCalls));
        OnPropertyChanged(nameof(TotalTokensText));
        OnPropertyChanged(nameof(TotalInputTokensText));
        OnPropertyChanged(nameof(TotalOutputTokensText));
        OnPropertyChanged(nameof(TotalCallsText));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowContent));
    }
}

/// <summary>One model's row in the Usage table. Plain get-only properties: the type is bound from
/// an <c>x:Bind</c> DataTemplate, which needs settable/plain members rather than <c>required</c>
/// or <c>init</c> accessors (see the XAML gotchas in CLAUDE.md).</summary>
public sealed class UsageRowViewModel
{
    public UsageRowViewModel(ModelUsageSummary summary, long maxTotalTokens)
    {
        Model = summary.Model;
        Calls = summary.Calls;
        InputTokens = summary.InputTokens;
        OutputTokens = summary.OutputTokens;
        CachedTokens = summary.CachedTokens;
        TotalTokens = summary.TotalTokens;
        CacheHitRatio = summary.CacheHitRatio;
        // Guarded against 0: an empty or all-zero window would otherwise divide by zero and
        // render every bar as NaN width.
        SharePercent = maxTotalTokens <= 0 ? 0 : 100.0 * summary.TotalTokens / maxTotalTokens;
    }

    public string Model { get; }
    public long Calls { get; }
    public long InputTokens { get; }
    public long OutputTokens { get; }
    public long CachedTokens { get; }
    public long TotalTokens { get; }
    public double CacheHitRatio { get; }

    /// <summary>Width of the share bar, 0–100, relative to the window's biggest model.</summary>
    public double SharePercent { get; }

    public string CallsText => Calls.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
    public string InputTokensText => UsageFormat.Tokens(InputTokens);
    public string OutputTokensText => UsageFormat.Tokens(OutputTokens);
    public string TotalTokensText => UsageFormat.Tokens(TotalTokens);

    /// <summary>Cache hits are only worth showing when there are any — a permanent "0%" column just
    /// adds noise for agents whose model does not do prompt caching.</summary>
    public bool HasCacheHits => CachedTokens > 0;

    public string CacheText => $"{CacheHitRatio:P0} cached";

    public string AccessibilityName =>
        $"{Model}: {TotalTokensText} tokens across {CallsText} calls, " +
        $"{InputTokensText} in, {OutputTokensText} out.";
}

internal static class UsageFormat
{
    /// <summary>Compact token counts (1.2K, 3.4M). Matches how the per-turn summary bubble already
    /// formats them, so the same number never appears in two different shapes.</summary>
    public static string Tokens(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 1_000 => $"{tokens / 1000.0:0.#}K",
        _ => tokens.ToString(System.Globalization.CultureInfo.CurrentCulture),
    };
}
