using CommunityToolkit.Mvvm.ComponentModel;

namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// One dynamic named field in an ask_user form (e.g. "username"), bound to an
/// inline TextBox in the chat bubble. Plain `set` (not `init`) on every property:
/// the XAML compiler's generated x:Bind reflection metadata needs settable
/// properties for any type reachable from a DataTemplate (see the identical note
/// on PendingAttachment/ChatAttachment).
/// </summary>
public sealed partial class AskUserFieldEntry : Common.ObservableObject
{
    private static readonly string[] SecretNameFragments =
    [
        "password", "passwd", "passphrase", "secret", "token", "apikey", "api_key",
        "private_key", "privatekey", "credential", "otp", "2fa", "mfa",
    ];

    public AskUserFieldEntry() => Value = "";

    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Placeholder { get; set; }
    public bool Required { get; set; }
    public string? Type { get; set; }
    public bool IsSecret
    {
        get
        {
            if (string.Equals(Type, "password", StringComparison.OrdinalIgnoreCase)) return true;

            var haystack = $"{Name} {Label}".ToLowerInvariant()
                .Replace("-", "", StringComparison.Ordinal)
                .Replace(" ", "", StringComparison.Ordinal);
            return SecretNameFragments.Any(fragment =>
                haystack.Contains(fragment.Replace("_", "", StringComparison.Ordinal),
                    StringComparison.Ordinal));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    public partial string? ValidationError { get; set; }

    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationError);
    public string RequirementLabel => Required
        ? Common.CoreStrings.Get("AskUserFieldRequired", "Required")
        : Common.CoreStrings.Get("AskUserFieldOptional", "Optional");

    [ObservableProperty]
    public partial string Value { get; set; }

    partial void OnValueChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) ValidationError = null;
        Owner?.NotifyAskUserInputChanged();
    }

    public ChatMessage? Owner { get; set; }
}

/// <summary>
/// One selectable option in an ask_user turn. Native CheckBox/RadioButton controls expose the
/// correct UI Automation patterns; this model still owns cross-item exclusivity because repeater
/// item templates do not share one radio-button namescope.
/// </summary>
public sealed partial class AskUserOptionEntry : Common.ObservableObject
{
    public string Text { get; set; } = "";

    /// <summary>Rewrites the host's three stock approval options into clearer wording.
    /// <para>Only the *right* side is localized. The match arms are the literal strings the
    /// agent sends on the wire, so they stay English no matter what the client's language is;
    /// <see cref="Text"/> is also what gets sent back as the answer. An option the host words
    /// differently falls through unchanged and is shown verbatim — the agent's own phrasing is
    /// better than a wrong guess.</para></summary>
    public string DisplayText => Text switch
    {
        "Yes, apply this change"
            => Common.CoreStrings.Get("AskUserOptionApplyOnce", "Apply once"),
        "Yes to all (auto-approve)"
            => Common.CoreStrings.Get("AskUserOptionApplyAll", "Apply all similar changes"),
        "No, reject and give feedback"
            => Common.CoreStrings.Get("AskUserOptionReject", "Reject and provide feedback"),
        _ => Text,
    };

    /// <summary>
    /// The card this option belongs to, so a click can reach its siblings to deselect them.
    /// Plain `set` and untyped-by-interface on purpose: every type reachable from a DataTemplate
    /// needs settable properties for the XAML compiler's generated x:Bind metadata (see the note
    /// on <see cref="AskUserFieldEntry"/>).
    /// </summary>
    public ChatMessage? Owner { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibilityName))]
    public partial bool IsChecked { get; set; }

    public string AccessibilityName => $"{DisplayText}, {(IsChecked ? "selected" : "not selected")}";

    /// <summary>Handles a click on the option's card. Single-select turns clear the siblings and
    /// keep the clicked one on (clicking the selected option again is a no-op, matching a radio
    /// group); multi-select turns just flip it.</summary>
    public void Toggle()
        => SetSelectedFromControl(Owner is { AskUserMultiSelect: false } || !IsChecked);

    /// <summary>Applies the state reported by the native selection control. A single-select option
    /// cannot be cleared by clicking it again; a multi-select option follows the checkbox state.</summary>
    public void SetSelectedFromControl(bool selected)
    {
        if (Owner is { AskUserMultiSelect: false })
        {
            if (!selected)
            {
                IsChecked = true;
                return;
            }
            foreach (var sibling in Owner.AskUserOptionEntries)
            {
                if (!ReferenceEquals(sibling, this)) sibling.IsChecked = false;
            }
            IsChecked = true;
            Owner.NotifyAskUserInputChanged();
            return;
        }
        IsChecked = selected;
        Owner?.NotifyAskUserInputChanged();
    }
}
