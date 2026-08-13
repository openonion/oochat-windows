using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace ConnectOnion.WinUIClient.Presentation;

/// <summary>
/// Binds an absolute local file path (attachment thumbnail source, either a
/// picked outgoing file or a cached incoming image) to a <see cref="BitmapImage"/>.
/// Returns null for an empty/invalid path so the bound <c>Image</c> control simply
/// renders nothing rather than throwing during XAML data binding.
///
/// The chat list virtualizes, so scrolling recycles containers and re-runs this converter for
/// the same paths repeatedly. Two things keep that cheap:
/// <list type="bullet">
/// <item>Results are held in a small bounded cache keyed by path, so a recycled container reuses
/// an already-decoded bitmap instead of hitting the disk again. Cached incoming images are
/// content-hash named, so a path always maps to the same bytes; a picked outgoing file edited in
/// place between renders would keep its previous thumbnail, which is fine for a draft preview.</item>
/// <item>Images wider than the 280px the templates render at are decoded down to that width
/// instead of at full resolution — a phone screenshot otherwise costs tens of MB of decoded
/// pixels to show a thumbnail. The size is read from the image header first so that images
/// <i>smaller</i> than the cap are left alone rather than being upscaled into a blurry mess.</item>
/// </list>
/// </summary>
public sealed class LocalPathToImageSourceConverter : IValueConverter
{
    /// <summary>Matches the 280px cap the attachment templates put on the rendered image.</summary>
    private const int MaxDecodeWidth = 280;
    private const int MaxCachedImages = 32;

    // Converters run on the UI thread, so a plain dictionary + queue is enough. The queue gives
    // FIFO eviction; a strict LRU isn't worth the bookkeeping for a cache this small.
    private static readonly Dictionary<string, WeakReference<BitmapImage>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> CacheOrder = new();

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path)) return null;
        if (Cache.TryGetValue(path, out var cached) && cached.TryGetTarget(out var cachedImage))
            return cachedImage;

        // Handed back empty and filled in asynchronously — the same deal as BitmapImage's own
        // UriSource loading, so the Image control already copes with the source arriving late.
        var image = new BitmapImage();
        _ = LoadAsync(image, path);

        var isNewPath = !Cache.ContainsKey(path);
        Cache[path] = new WeakReference<BitmapImage>(image);
        if (isNewPath) CacheOrder.Enqueue(path);
        while (CacheOrder.Count > MaxCachedImages && CacheOrder.TryDequeue(out var evicted))
        {
            Cache.Remove(evicted);
        }

        return image;
    }

    private static async Task LoadAsync(BitmapImage image, string path)
    {
        try
        {
            // Convert runs on the UI thread and does not await this, so everything before the
            // first suspension point runs synchronously inside the binding — and File.OpenRead is
            // a blocking syscall. The chat list virtualizes, so that was one synchronous file open
            // per recycled container, during measure/arrange, on every scroll frame past an image.
            //
            // Task.Run rather than a bare `await Task.Yield()`: this method starts on the UI
            // thread, so a yield resumes right back on it (the dispatcher is the captured
            // synchronization context) and would move nothing off. Only the open is offloaded —
            // ConfigureAwait(true) then returns to the UI thread, which the BitmapImage work below
            // requires, since a BitmapImage has thread affinity and both DecodePixelWidth and
            // SetSourceAsync must be touched from the thread that created it.
            //
            // A raw FileStream (rather than StorageFile) keeps this working for both the cached
            // images under AppData and files the user picked from anywhere on disk. useAsync so
            // the decoder's reads below do not block a pool thread either.
            await using var file = await Task.Run(() => new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true)).ConfigureAwait(true);
            using var stream = file.AsRandomAccessStream();

            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.PixelWidth > MaxDecodeWidth)
            {
                // Logical (not Physical) so the decode target scales with the display's DPI and
                // the thumbnail stays crisp on a 150%/200% monitor.
                image.DecodePixelType = DecodePixelType.Logical;
                image.DecodePixelWidth = MaxDecodeWidth;
            }

            stream.Seek(0);
            await image.SetSourceAsync(stream);
        }
        catch
        {
            // Leave the BitmapImage empty; the Image control renders nothing, matching the
            // previous behaviour for an unreadable or malformed file.
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
