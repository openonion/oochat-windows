using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>Why a rebind was refused, or that it took.</summary>
public enum RebindOutcome
{
    Applied,
    /// <summary>The chord is already bound to another action, named by <see cref="RebindResult.ConflictingActionName"/>.</summary>
    Conflict,
    /// <summary>The chord names no dispatchable key, or the id is not a customizable action.</summary>
    Invalid,
}

/// <param name="ConflictingActionName">The action already holding the chord; empty unless <see cref="RebindOutcome.Conflict"/>.</param>
public readonly record struct RebindResult(RebindOutcome Outcome, string ConflictingActionName)
{
    public bool Succeeded => Outcome == RebindOutcome.Applied;

    public static readonly RebindResult Applied = new(RebindOutcome.Applied, "");
    public static readonly RebindResult Invalid = new(RebindOutcome.Invalid, "");
    public static RebindResult Conflict(string actionName) => new(RebindOutcome.Conflict, actionName);
}

/// <summary>
/// Resolves every customizable shortcut to the chord that is actually live — the user's override
/// if there is a usable one, the catalog default otherwise — and answers the key handlers'
/// "did this keystroke mean anything?" question.
///
/// <para>Overrides share <c>preferences.shortcut_overrides_json</c> with the unrelated
/// <c>composer.enterKey</c> entry. This only ever adds and removes its own catalog ids, so that
/// neighbour survives a rebind untouched.</para>
///
/// <para>An override that no longer parses (a hand-edited row, a key dropped from
/// <see cref="KeyNames"/>) silently falls back to the default rather than leaving an action with
/// no way to fire.</para>
/// </summary>
public sealed class KeyboardShortcutService
{
    private readonly PreferencesRepository _preferences;
    private readonly IReadOnlyDictionary<string, KeyboardShortcutItem> _customizable;
    private readonly object _gate = new();

    private Dictionary<string, KeyChord> _chords = new(StringComparer.Ordinal);
    private Dictionary<KeyChord, string> _byChord = new();

    /// <summary>Raised after any rebind or reset, so menus and open pages re-read their bindings.</summary>
    public event Action? BindingsChanged;

    public KeyboardShortcutService(PreferencesRepository preferences)
    {
        _preferences = preferences;
        _customizable = KeyboardShortcutCatalog.GetCustomizable().ToDictionary(i => i.Id, StringComparer.Ordinal);
        Rebuild(new Dictionary<string, string>());
    }

    /// <summary>Reads the stored overrides. Until this runs the service answers with catalog
    /// defaults, so a keystroke during startup still does the right thing.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _preferences.LoadAsync(cancellationToken).ConfigureAwait(false);
        ApplySnapshot(snapshot);
    }

    public void ApplySnapshot(PreferencesSnapshot snapshot)
    {
        Rebuild(snapshot.ShortcutOverrides);
        BindingsChanged?.Invoke();
    }

    /// <summary>The live chord for an action, or <see cref="KeyChord.None"/> if it is not a
    /// customizable action.</summary>
    public KeyChord GetChord(string id)
    {
        lock (_gate) return _chords.TryGetValue(id, out var chord) ? chord : KeyChord.None;
    }

    /// <summary>
    /// The action this keystroke is bound to, or null. Handlers pass <c>(int)e.Key</c> and the
    /// modifier states they already read; numpad +/- fold onto their OEM twins here, matching what
    /// the hard-coded handlers accepted before.
    /// </summary>
    public string? Match(int keyCode, bool ctrl, bool shift, bool alt)
    {
        var chord = KeyChord.FromKeyEvent(keyCode, ctrl, shift, alt);
        if (chord.IsEmpty) return null;

        lock (_gate) return _byChord.TryGetValue(chord, out var id) ? id : null;
    }

    /// <summary>True when this keystroke is bound to <paramref name="id"/> — the form the key
    /// handlers read most naturally.</summary>
    public bool IsShortcut(string id, int keyCode, bool ctrl, bool shift, bool alt)
        => Match(keyCode, ctrl, shift, alt) == id;

    /// <summary>The catalog with each customizable entry showing its live chord. Non-customizable
    /// entries pass through untouched.</summary>
    public IReadOnlyList<KeyboardShortcutGroup> GetLiveGroups()
    {
        var groups = new List<KeyboardShortcutGroup>();
        foreach (var group in KeyboardShortcutCatalog.GetGroups())
        {
            var items = new List<KeyboardShortcutItem>(group.Shortcuts.Count);
            foreach (var item in group.Shortcuts)
            {
                if (!item.IsCustomizable)
                {
                    items.Add(item);
                    continue;
                }

                var chord = GetChord(item.Id);
                item.KeyBindings = new[] { chord.ToBinding() };
                items.Add(item);
            }
            groups.Add(new KeyboardShortcutGroup(group.Title, items));
        }
        return groups;
    }

    /// <summary>True when this action is currently on something other than its factory chord.</summary>
    public bool IsRebound(string id)
        => _customizable.TryGetValue(id, out var item) && GetChord(id) != item.DefaultChord;

    /// <summary>
    /// Points an action at a new chord. Refuses a chord already held by a different action —
    /// silently stealing it would leave that one dead with no way to notice.
    /// </summary>
    public async Task<RebindResult> RebindAsync(string id, KeyChord chord, CancellationToken cancellationToken = default)
    {
        if (chord.IsEmpty || !_customizable.ContainsKey(id)) return RebindResult.Invalid;

        var holder = Match(chord.KeyCode, chord.Ctrl, chord.Shift, chord.Alt);
        if (holder is not null && holder != id)
            return RebindResult.Conflict(_customizable[holder].Name);

        await MutateAsync(
            overrides =>
            {
                // Back on the default? Drop the row instead of pinning it, so a future change of
                // default reaches users who never rebound this action.
                if (chord == _customizable[id].DefaultChord) overrides.Remove(id);
                else overrides[id] = chord.Canonical;
            },
            cancellationToken).ConfigureAwait(false);

        return RebindResult.Applied;
    }

    /// <summary>Drops any override, restoring the catalog default.</summary>
    public async Task ResetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!_customizable.ContainsKey(id)) return;
        await MutateAsync(overrides => overrides.Remove(id), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops every shortcut override at once. Leaves non-shortcut entries
    /// (<c>composer.enterKey</c>) alone.</summary>
    public async Task ResetAllAsync(CancellationToken cancellationToken = default)
        => await MutateAsync(
            overrides =>
            {
                foreach (var id in _customizable.Keys) overrides.Remove(id);
            },
            cancellationToken).ConfigureAwait(false);

    /// <summary>Load-modify-save against the shared preferences row, then republish. The whole
    /// snapshot round-trips because that is the repository's only write shape.</summary>
    private async Task MutateAsync(Action<Dictionary<string, string>> mutate, CancellationToken cancellationToken)
    {
        var snapshot = await _preferences.LoadAsync(cancellationToken).ConfigureAwait(false);
        mutate(snapshot.ShortcutOverrides);
        await _preferences.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);

        Rebuild(snapshot.ShortcutOverrides);
        BindingsChanged?.Invoke();
    }

    /// <summary>
    /// Recomputes both lookup tables from the catalog plus overrides. Defaults are seeded first so
    /// an unparseable or conflicting override just loses; the last writer of a chord would
    /// otherwise decide the winner by dictionary order.
    /// </summary>
    private void Rebuild(IReadOnlyDictionary<string, string> overrides)
    {
        var chords = new Dictionary<string, KeyChord>(StringComparer.Ordinal);
        foreach (var (id, item) in _customizable)
            chords[id] = item.DefaultChord;

        foreach (var (id, raw) in overrides)
        {
            if (!_customizable.ContainsKey(id)) continue;      // e.g. composer.enterKey
            if (KeyChord.TryParse(raw, out var chord)) chords[id] = chord;
        }

        // A stored pair that now collides (two overrides onto one chord, or an override landing on
        // another action's default) can only be resolved one way, so the first id wins by catalog
        // order and the loser reverts to its default — deterministic, and never silently dead.
        var byChord = new Dictionary<KeyChord, string>();
        foreach (var item in KeyboardShortcutCatalog.GetCustomizable())
        {
            var chord = chords[item.Id];
            if (byChord.ContainsKey(chord)) chord = chords[item.Id] = item.DefaultChord;
            if (!byChord.ContainsKey(chord)) byChord[chord] = item.Id;
        }

        lock (_gate)
        {
            _chords = chords;
            _byChord = byChord;
        }
    }
}
