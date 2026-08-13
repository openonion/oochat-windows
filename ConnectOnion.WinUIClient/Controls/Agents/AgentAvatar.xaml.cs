using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// The agent's face, shared by the home cards, the agent detail page, the chat header and the
/// sidebar. Shows the user's chosen image when <see cref="ImagePath"/> resolves to one, and the
/// name initial on a theme-neutral background when it does not — missing, unreadable and never-set
/// all land on the initial, because an agent row with a blank avatar is worse than a lettered one.
/// </summary>
public sealed partial class AgentAvatar : UserControl
{
    /// <summary>Committed icons are 256px squares but the largest slot on screen is 56px, so the
    /// decode is capped: decoding at full size costs ~256 KB of pixels to draw a 28px circle, and
    /// the sidebar builds one of these per agent.</summary>
    private const int MaxDecodePixels = 96;

    private const int MaxCachedImages = 16;

    // The sidebar rebuilds its rows on every refresh, so the same handful of files are decoded
    // again and again.
    //
    // Strong references, deliberately — this used to hold WeakReference<BitmapImage>. The only
    // strong reference to a cached bitmap was then the Source of a realized Image, so a rebuild
    // that returned rows to the recycle pool plus any GC in between collected it. The next
    // realization missed the cache and fell into LoadImageAsync, and a freshly realized avatar
    // starts on the initial (CustomAvatarImage is Collapsed in the XAML) — so the agent's face
    // visibly flashed letter -> picture on every conversation switch, and only for agents that
    // had an icon at all. A cache that evaporates exactly when it is about to be used is not a
    // cache.
    //
    // Correct to hold because a path is never reused for different content: AgentIconService mints
    // a fresh GUID for both the temporary pick (agent-icon-{guid}.png) and the committed file
    // (agent-{agentId}-{guid}.png), so choosing a new picture always produces a new key. There is
    // therefore no invalidation to write — but if that naming ever becomes stable, this cache
    // starts serving a stale face and would need one.
    //
    // Safe to hold because both dimensions are bounded and small: at most MaxCachedImages entries,
    // each decoded down to MaxDecodePixels, so the whole cache is ~16 x 96 x 96 x 4B ≈ 0.6 MB of
    // native surface however many agents exist. That is the opposite situation to
    // LocalPathToImageSourceConverter, which is weak on purpose: it caches chat attachment
    // thumbnails at 280px with no bound on how many distinct images a transcript contains.
    //
    // All access is from the UI thread, so a plain dictionary + FIFO queue is enough.
    private static readonly Dictionary<string, BitmapImage> ImageCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Queue<string> ImageCacheOrder = new();

    public static readonly DependencyProperty InitialProperty =
        DependencyProperty.Register(
            nameof(Initial),
            typeof(string),
            typeof(AgentAvatar),
            new PropertyMetadata("?"));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(
            nameof(Size),
            typeof(double),
            typeof(AgentAvatar),
            new PropertyMetadata(32.0));

    public static readonly DependencyProperty ImagePathProperty =
        DependencyProperty.Register(
            nameof(ImagePath),
            typeof(string),
            typeof(AgentAvatar),
            new PropertyMetadata(null, OnImagePathChanged));

    /// <summary>
    /// Bumped on every <see cref="ImagePath"/> change so a decode that finishes after the control
    /// was rebound is discarded. Recycled rows change path faster than a disk read completes, and
    /// without this the previous agent's face lands on the current agent's row.
    /// </summary>
    private int _imageLoadVersion;

    public AgentAvatar()
    {
        InitializeComponent();
    }

    /// <summary>Letter shown when no custom image is available.</summary>
    public string Initial
    {
        get => (string)GetValue(InitialProperty);
        set => SetValue(InitialProperty, value);
    }

    /// <summary>Width and height of the avatar.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>
    /// The image to show. Normally the stored relative path (<c>avatars/agent-….png</c>), but an
    /// absolute path is accepted too so the Add Agent form can preview a pick that has not been
    /// committed yet. Either way it must land inside the managed avatar directories —
    /// <c>icon_path</c> is a database column, and a hand-edited one must not turn this control
    /// into a viewer for arbitrary files on disk.
    /// </summary>
    public string? ImagePath
    {
        get => (string?)GetValue(ImagePathProperty);
        set => SetValue(ImagePathProperty, value);
    }

    private static void OnImagePathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((AgentAvatar)sender).BeginLoadCustomImage();

    private void BeginLoadCustomImage()
    {
        var loadVersion = Interlocked.Increment(ref _imageLoadVersion);
        var configuredPath = ImagePath;

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            ShowInitial();
            return;
        }

        var absolutePath = ResolveManagedImagePath(configuredPath);
        if (absolutePath is null)
        {
            ShowInitial();
            return;
        }

        // Synchronous, in the same frame as the rebind. This is the path that has to be hit on a
        // recycled row: anything asynchronous here is a visible flash.
        if (ImageCache.TryGetValue(absolutePath, out var cachedImage))
        {
            ShowImage(cachedImage);
            return;
        }

        // Deliberately not awaited: the load swallows every failure and falls back to the initial,
        // so there is nothing for a caller to observe.
        _ = LoadImageAsync(absolutePath, loadVersion);
    }

    private async Task LoadImageAsync(string absolutePath, int loadVersion)
    {
        try
        {
            await using var file = File.OpenRead(absolutePath);
            using var stream = file.AsRandomAccessStream();

            var image = new BitmapImage();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.PixelWidth > MaxDecodePixels)
            {
                // Logical rather than Physical so the decode target follows the display scale and
                // the avatar stays sharp on a 150%/200% monitor.
                image.DecodePixelType = DecodePixelType.Logical;
                image.DecodePixelWidth = MaxDecodePixels;
            }

            stream.Seek(0);
            await image.SetSourceAsync(stream);

            // The path can change while the file is being read. Applying a stale result would put
            // one agent's picture on another's row.
            if (loadVersion != Volatile.Read(ref _imageLoadVersion)) return;

            Cache(absolutePath, image);
            ShowImage(image);
        }
        catch (Exception)
        {
            // Missing, corrupt, locked or unsupported: the initial is the safe fallback, and an
            // agent list must not break because one file went bad.
            if (loadVersion == Volatile.Read(ref _imageLoadVersion)) ShowInitial();
        }
    }

    /// <summary>
    /// Turns a stored relative path (or an uncommitted absolute preview path) into a file this
    /// control is allowed to open, or null if it points outside the managed directories.
    /// </summary>
    private static string? ResolveManagedImagePath(string configuredPath)
    {
        try
        {
            var trimmedPath = configuredPath.Trim();
            if (!Path.IsPathFullyQualified(trimmedPath))
            {
                return AppStorage.TryGetAgentIconAbsolutePath(trimmedPath, out var resolved)
                    ? resolved
                    : null;
            }

            var absolutePath = Path.GetFullPath(trimmedPath);
            return AppStorage.IsPathInsideDirectory(absolutePath, AppStorage.AgentIconsDir)
                   || AppStorage.IsPathInsideDirectory(absolutePath, AppStorage.TemporaryAgentIconsDir)
                ? absolutePath
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException
                                             or NotSupportedException
                                             or PathTooLongException)
        {
            return null;
        }
    }

    private static void Cache(string absolutePath, BitmapImage image)
    {
        var isNewPath = !ImageCache.ContainsKey(absolutePath);
        ImageCache[absolutePath] = image;
        if (isNewPath) ImageCacheOrder.Enqueue(absolutePath);

        while (ImageCacheOrder.Count > MaxCachedImages && ImageCacheOrder.TryDequeue(out var evicted))
        {
            ImageCache.Remove(evicted);
        }
    }

    private void ShowImage(BitmapImage image)
    {
        CustomAvatarImage.Source = image;
        CustomAvatarImage.Visibility = Visibility.Visible;
        InitialText.Visibility = Visibility.Collapsed;

    }

    private void ShowInitial()
    {
        CustomAvatarImage.Source = null;
        CustomAvatarImage.Visibility = Visibility.Collapsed;
        InitialText.Visibility = Visibility.Visible;

    }

    /// <summary>
    /// A decode can also fail after the source is attached, which never reaches the load's own
    /// catch. Invalidate first so an in-flight load cannot put the broken image straight back.
    /// </summary>
    private void CustomImage_ImageFailed(object sender, ExceptionRoutedEventArgs args)
    {
        Interlocked.Increment(ref _imageLoadVersion);
        ShowInitial();
    }
}
