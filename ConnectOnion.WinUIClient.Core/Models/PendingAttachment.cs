using CommunityToolkit.Mvvm.ComponentModel;

namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// A composer-side attachment draft: picked from disk and validated before send.
/// Encoding is deferred until the turn is ready to write its INPUT frame. Lives only in
/// <c>ChatComposer</c>'s pending list — never persisted, never put in
/// <see cref="ChatMessage"/> directly. On successful send it is converted into a
/// <see cref="ChatAttachment"/> on the new user message and discarded.
/// </summary>
public sealed partial class PendingAttachment : Common.ObservableObject
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    // Plain `set` rather than `required`/`init`: the XAML compiler's generated
    // type-info metadata (XamlTypeInfo.g.cs, used for x:Bind reflection over
    // every type reachable from a DataTemplate) generates a property setter for
    // each of these regardless of whether any binding is actually TwoWay, and
    // neither `required` nor `init` accessors satisfy that generated code.
    // AttachmentPickerService always supplies all five via an object
    // initializer immediately after construction regardless.
    public AttachmentKind Kind { get; set; }

    public string FileName { get; set; } = "";

    /// <summary>Absolute path to the picked <c>StorageFile</c> on disk.</summary>
    public string LocalPath { get; set; } = "";

    public string MimeType { get; set; } = "application/octet-stream";

    /// <summary>Raw file size in bytes, read once from file metadata (no content read) so oversized files are rejected before any I/O.</summary>
    public long SizeBytes { get; set; }

    // Default status is Pending (the enum's zero value), so no ctor initializer is needed.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial AttachmentStatus Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool IsImage => Kind == AttachmentKind.Image;
    public bool IsFailed => Status == AttachmentStatus.Failed;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool IsBusy => Status is AttachmentStatus.Encoding or AttachmentStatus.Sending;
    public bool IsReady => Status == AttachmentStatus.Ready;
    public string SizeLabel => ChatAttachment.FormatSize(SizeBytes);

    /// <summary>Compact, locale-neutral metadata for the composer's file card.</summary>
    public string MetadataLabel
    {
        get
        {
            var extension = Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant();
            var size = SizeLabel;
            if (string.IsNullOrEmpty(extension)) return size;
            return string.IsNullOrEmpty(size) ? extension : $"{extension} · {size}";
        }
    }
}
