using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectOnion.WinUIClient.Common;

namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// A saved agent connection. Mirrors the TypeScript <c>AgentConfig</c> type.
/// Observable so the editor form and the agent list stay in sync via two-way
/// binding; System.Text.Json serializes the public properties and ignores the
/// change notifications. Uses <c>[ObservableProperty]</c> on partial properties
/// (the WinUI-recommended form); non-null string defaults are set in the ctor
/// because a partial-property declaration can't carry an initializer.
/// </summary>
public sealed partial class AgentConfig : Common.ObservableObject
{
    public const int MaxNameLength = 64;

    public AgentConfig()
    {
        Id = "";
        Name = "";
        Address = "";
    }

    [ObservableProperty]
    [JsonPropertyName("id")]
    public partial string Id { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [JsonPropertyName("name")]
    public partial string Name { get; set; }

    /// <summary>User-facing form of <see cref="Name"/>. The stored and transmitted value remains
    /// unchanged; every UI surface uses this same projection as the sidebar.</summary>
    [JsonIgnore]
    public string DisplayName => FriendlyAgentName.From(Name);

    /// <summary>Applies a user-chosen local display name without touching the connection identity.
    /// The endpoint and stable agent id remain the values used to reach the remote agent.</summary>
    public bool TryRename(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > MaxNameLength
            || string.Equals(trimmed, Name, StringComparison.Ordinal))
        {
            return false;
        }

        Name = trimmed;
        return true;
    }

    public static bool IsValidName(string? name)
    {
        var trimmed = name?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length <= MaxNameLength;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRelayOnly))]
    [JsonPropertyName("address")]
    public partial string Address { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRelayOnly))]
    [JsonPropertyName("directUrl")]
    public partial string? DirectUrl { get; set; }

    /// <summary>
    /// True when this saved entry resolves through the relay address rather than a user-supplied
    /// direct endpoint. Only these agents have a stable public web-chat URL worth sharing.
    /// </summary>
    [JsonIgnore]
    public bool IsRelayOnly
        => !string.IsNullOrWhiteSpace(Address)
            && string.IsNullOrWhiteSpace(DirectUrl);

    /// <summary>Path to the user's chosen icon, relative to the data root (<c>avatars/….png</c>).
    /// Null means no custom icon was ever set and the name-initial avatar is used; the path is
    /// stored rather than the bytes so the image never travels through SQLite.</summary>
    [ObservableProperty]
    [JsonPropertyName("iconPath")]
    public partial string? IconPath { get; set; }

    /// <summary>Cached /info response JSON, persisted across restarts.</summary>
    [JsonPropertyName("infoJson")]
    public string? InfoJson { get; set; }

    /// <summary>Timestamp of the last successful /info fetch.</summary>
    [JsonPropertyName("infoUpdatedAt")]
    public string? InfoUpdatedAt { get; set; }
}
