using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ConnectOnion.WinUIClient.Models;

/// <summary>Whether an attachment is multimodal image input/output or a generic file.</summary>
public enum AttachmentKind
{
    Image,
    File,
}

/// <summary>
/// Lifecycle of an attachment. <see cref="PendingAttachment"/> (composer draft) uses
/// the full range; a <see cref="ChatAttachment"/> already attached to a sent/received
/// message is normally either <see cref="Sent"/>/<see cref="Ready"/> or <see cref="Failed"/>.
/// </summary>
public enum AttachmentStatus
{
    Pending,
    Encoding,
    Ready,
    Sending,
    Sent,
    Failed,
}

/// <summary>
/// An attachment already attached to a rendered <see cref="ChatMessage"/> bubble —
/// either something the user sent, or an image the agent produced via the
/// <c>agent_image</c> wire event. Never holds a full data URL: outgoing attachments
/// keep generic files pointing at the user's original file; user and agent images are copied
/// once to <c>AppStorage</c>'s content-addressed image cache and only the
/// cache path is kept (see <c>AttachmentImageCacheService</c>) — this is what keeps
/// large base64 payloads out of the in-memory message list and out of SQLite.
/// </summary>
public sealed partial class ChatAttachment : Common.ObservableObject
{
    public ChatAttachment() => Status = AttachmentStatus.Ready;

    // Plain `set` (not `init`) throughout — see the identical note on
    // PendingAttachment: the XAML compiler's generated x:Bind reflection
    // metadata needs settable properties for any type reachable from a
    // DataTemplate, which this class is (ChatPage.xaml's AttachmentChipTemplate).
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("kind")]
    public AttachmentKind Kind { get; set; }

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    /// <summary>
    /// Absolute local filesystem path: an app-owned content-cache file for images, or the
    /// user's original path for a generic file they sent. Used both for "open/save" and, for
    /// images, as the bitmap source.
    /// Settable after construction (not just at creation) because an incoming
    /// <c>agent_image</c> attachment starts with no local copy and gets one once
    /// <c>AttachmentImageCacheService</c> finishes decoding/downloading it — the
    /// bound <c>Image</c> control picks up the change via <c>Mode=OneWay</c>.
    /// </summary>
    [ObservableProperty]
    [JsonPropertyName("localCachePath")]
    public partial string? LocalCachePath { get; set; }

    /// <summary>Original remote reference (an http(s) URL), when the source was a URL rather than a data URL. Never a data: URL — those are decoded to <see cref="LocalCachePath"/> immediately and discarded.</summary>
    [JsonPropertyName("remoteUri")]
    public string? RemoteUri { get; set; }

    [JsonIgnore]
    public string? PreviewUri => Kind == AttachmentKind.Image && !string.IsNullOrEmpty(LocalCachePath)
        ? new Uri(LocalCachePath).AbsoluteUri
        : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [JsonPropertyName("status")]
    public partial AttachmentStatus Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [JsonPropertyName("error")]
    public partial string? Error { get; set; }

    [JsonIgnore]
    public bool IsImage => Kind == AttachmentKind.Image;

    [JsonIgnore]
    public bool IsFailed => Status == AttachmentStatus.Failed;

    [JsonIgnore]
    public bool HasError => !string.IsNullOrEmpty(Error);

    [JsonIgnore]
    public string SizeLabel => FormatSize(SizeBytes);

    /// <summary>
    /// A cached local path and a failed status are contradictory: the cache pipeline only marks
    /// an attachment failed when it produced no path. Normalize either assignment order so stale
    /// persisted data cannot make WinUI render a thumbnail and a failure label together.
    /// </summary>
    partial void OnLocalCachePathChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && Status == AttachmentStatus.Failed)
        {
            Status = AttachmentStatus.Sent;
            Error = null;
        }
    }

    partial void OnStatusChanged(AttachmentStatus value)
    {
        if (value == AttachmentStatus.Failed && !string.IsNullOrWhiteSpace(LocalCachePath))
        {
            Status = AttachmentStatus.Sent;
            Error = null;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };
}
