using ConnectOnion.WinUIClient.Data;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Frozen;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Identifies a recoverable Agent-icon operation failure.
///
/// UI code should map these values to localized strings in Resources.resw
/// instead of displaying exception messages directly.
/// </summary>
public enum AgentIconError
{
    WindowUnavailable,
    PickerFailed,
    UnsupportedFileType,
    FileTooLarge,
    ImageTooLarge,
    InvalidImage,
    TemporaryFileMissing,
    InvalidTemporaryPath,
    SaveFailed,
    DeleteFailed,
}

/// <summary>
/// Represents a failure while selecting, processing, saving, or deleting
/// a custom Agent icon.
/// </summary>
public sealed class AgentIconException : Exception
{
    public AgentIconException(
        AgentIconError error,
        Exception? innerException = null)
        : base(error.ToString(), innerException)
    {
        Error = error;
    }

    public AgentIconError Error { get; }
}

/// <summary>
/// Selects, processes, commits, and deletes user-selected Agent icons.
/// </summary>
public interface IAgentIconService
{
    /// <summary>
    /// Opens the image picker and creates a processed temporary PNG.
    ///
    /// Returns the absolute temporary path, or <see langword="null"/> when
    /// the user closes the picker without choosing a file.
    /// </summary>
    Task<string?> PickTemporaryIconAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a processed temporary icon into permanent app-managed storage.
    ///
    /// Returns the relative path that should be assigned to
    /// <c>AgentConfig.IconPath</c> and persisted in SQLite.
    /// </summary>
    Task<string> CommitTemporaryIconAsync(
        string agentId,
        string temporaryPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a temporary icon created while an Add/Edit Agent form
    /// was open.
    /// </summary>
    Task DeleteTemporaryIconAsync(
        string? temporaryPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a committed icon using its SQLite relative path.
    /// </summary>
    Task DeleteIconAsync(
        string? relativePath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// WinUI implementation of Agent icon selection and local storage.
///
/// Selected images are decoded, EXIF-oriented, centre-cropped, resized to
/// 256 × 256, and encoded as PNG before being stored.
/// </summary>
public sealed class AgentIconService : IAgentIconService
{
    private const ulong MaxSourceFileBytes = 5UL * 1024UL * 1024UL;
    private const uint MaxSourceDimension = 8192;
    private const uint OutputDimension = 256;

    private static readonly FrozenSet<char> InvalidFileNameCharacters =
        Path.GetInvalidFileNameChars().ToFrozenSet();

    private static readonly FrozenSet<string> SupportedExtensions =
        new[]
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".webp",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<string?> PickTemporaryIconAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AppStorage.EnsureDirectories();

        var ownerWindow = App.MainWindow;

        if (ownerWindow is null)
        {
            throw new AgentIconException(
                AgentIconError.WindowUnavailable);
        }

        var picker = CreatePicker(ownerWindow);

        StorageFile? selectedFile;

        try
        {
            selectedFile = await picker.PickSingleFileAsync();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AgentIconException(
                AgentIconError.PickerFailed,
                exception);
        }

        // PickSingleFileAsync returns null when the user dismisses the picker.
        if (selectedFile is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        ValidateExtension(selectedFile.Name);

        BasicProperties properties;

        try
        {
            properties = await selectedFile.GetBasicPropertiesAsync();
        }
        catch (Exception exception)
        {
            throw new AgentIconException(
                AgentIconError.InvalidImage,
                exception);
        }

        if (properties.Size > MaxSourceFileBytes)
        {
            throw new AgentIconException(
                AgentIconError.FileTooLarge);
        }

        var temporaryFileName =
            $"agent-icon-{Guid.NewGuid():N}.png";

        var temporaryPath =
            AppStorage.GetTemporaryAgentIconAbsolutePath(
                temporaryFileName);

        try
        {
            await DecodeCropAndSaveAsync(
                    selectedFile,
                    temporaryFileName,
                    cancellationToken)
                .ConfigureAwait(false);

            return temporaryPath;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
        catch (AgentIconException)
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
        catch (Exception exception)
        {
            TryDeleteFile(temporaryPath);

            throw new AgentIconException(
                AgentIconError.SaveFailed,
                exception);
        }
    }

    /// <inheritdoc />
    public Task<string> CommitTemporaryIconAsync(
        string agentId,
        string temporaryPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);

        AppStorage.EnsureDirectories();

        string validatedTemporaryPath;

        try
        {
            validatedTemporaryPath =
                ValidatePathInsideDirectory(
                    temporaryPath,
                    AppStorage.TemporaryAgentIconsDir);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                  or InvalidOperationException
                  or NotSupportedException
                  or PathTooLongException)
        {
            throw new AgentIconException(
                AgentIconError.InvalidTemporaryPath,
                exception);
        }

        if (!File.Exists(validatedTemporaryPath))
        {
            throw new AgentIconException(
                AgentIconError.TemporaryFileMissing);
        }

        var safeAgentId = CreateSafeFileNamePart(agentId);

        var permanentFileName =
            $"agent-{safeAgentId}-{Guid.NewGuid():N}.png";

        var permanentPath =
            AppStorage.GetPermanentAgentIconAbsolutePath(
                permanentFileName);

        try
        {
            File.Move(
                validatedTemporaryPath,
                permanentPath,
                overwrite: false);
        }
        catch (Exception exception)
            when (exception is IOException
                  or UnauthorizedAccessException
                  or NotSupportedException)
        {
            throw new AgentIconException(
                AgentIconError.SaveFailed,
                exception);
        }

        var relativePath =
            AppStorage.CreateAgentIconRelativePath(
                permanentFileName);

        return Task.FromResult(relativePath);
    }

    /// <inheritdoc />
    public Task DeleteTemporaryIconAsync(
        string? temporaryPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(temporaryPath))
        {
            return Task.CompletedTask;
        }

        string validatedPath;

        try
        {
            validatedPath =
                ValidatePathInsideDirectory(
                    temporaryPath,
                    AppStorage.TemporaryAgentIconsDir);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                  or InvalidOperationException
                  or NotSupportedException
                  or PathTooLongException)
        {
            throw new AgentIconException(
                AgentIconError.InvalidTemporaryPath,
                exception);
        }

        try
        {
            if (File.Exists(validatedPath))
            {
                File.Delete(validatedPath);
            }
        }
        catch (Exception exception)
            when (exception is IOException
                  or UnauthorizedAccessException
                  or NotSupportedException)
        {
            throw new AgentIconException(
                AgentIconError.DeleteFailed,
                exception);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteIconAsync(
        string? relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return Task.CompletedTask;
        }

        string absolutePath;

        try
        {
            absolutePath =
                AppStorage.GetAgentIconAbsolutePath(
                    relativePath);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                  or InvalidOperationException
                  or NotSupportedException
                  or PathTooLongException)
        {
            throw new AgentIconException(
                AgentIconError.DeleteFailed,
                exception);
        }

        try
        {
            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
            }
        }
        catch (Exception exception)
            when (exception is IOException
                  or UnauthorizedAccessException
                  or NotSupportedException)
        {
            throw new AgentIconException(
                AgentIconError.DeleteFailed,
                exception);
        }

        return Task.CompletedTask;
    }

    private static FileOpenPicker CreatePicker(Window ownerWindow)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation =
                PickerLocationId.PicturesLibrary,

            ViewMode = PickerViewMode.Thumbnail,
        };

        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");

        var windowHandle =
            WindowNative.GetWindowHandle(ownerWindow);

        InitializeWithWindow.Initialize(
            picker,
            windowHandle);

        return picker;
    }

    private static void ValidateExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);

        if (string.IsNullOrWhiteSpace(extension)
            || !SupportedExtensions.Contains(extension))
        {
            throw new AgentIconException(
                AgentIconError.UnsupportedFileType);
        }
    }

    private static async Task DecodeCropAndSaveAsync(
        StorageFile selectedFile,
        string temporaryFileName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var inputStream =
            await selectedFile.OpenReadAsync();

        BitmapDecoder decoder;

        try
        {
            decoder = await BitmapDecoder.CreateAsync(
                inputStream);
        }
        catch (Exception exception)
        {
            throw new AgentIconException(
                AgentIconError.InvalidImage,
                exception);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var rawWidth = decoder.PixelWidth;
        var rawHeight = decoder.PixelHeight;
        var orientedWidth = decoder.OrientedPixelWidth;
        var orientedHeight = decoder.OrientedPixelHeight;

        if (rawWidth == 0
            || rawHeight == 0
            || orientedWidth == 0
            || orientedHeight == 0)
        {
            throw new AgentIconException(
                AgentIconError.InvalidImage);
        }

        if (rawWidth > MaxSourceDimension
            || rawHeight > MaxSourceDimension)
        {
            throw new AgentIconException(
                AgentIconError.ImageTooLarge);
        }

        var transform = CreateCentreCropTransform(
            rawWidth,
            rawHeight,
            orientedWidth,
            orientedHeight);

        PixelDataProvider pixelData;

        try
        {
            pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
        }
        catch (Exception exception)
        {
            throw new AgentIconException(
                AgentIconError.InvalidImage,
                exception);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var pixels = pixelData.DetachPixelData();

        StorageFolder temporaryFolder;

        try
        {
            temporaryFolder =
                await StorageFolder.GetFolderFromPathAsync(
                    AppStorage.TemporaryAgentIconsDir);
        }
        catch (Exception exception)
        {
            throw new AgentIconException(
                AgentIconError.SaveFailed,
                exception);
        }

        var outputFile =
            await temporaryFolder.CreateFileAsync(
                temporaryFileName,
                CreationCollisionOption.ReplaceExisting);

        using var outputStream =
            await outputFile.OpenAsync(
                FileAccessMode.ReadWrite);

        BitmapEncoder encoder;

        try
        {
            encoder = await BitmapEncoder.CreateAsync(
                BitmapEncoder.PngEncoderId,
                outputStream);
        }
        catch (Exception exception)
        {
            throw new AgentIconException(
                AgentIconError.SaveFailed,
                exception);
        }

        var dpiX = decoder.DpiX > 0
            ? decoder.DpiX
            : 96.0;

        var dpiY = decoder.DpiY > 0
            ? decoder.DpiY
            : 96.0;

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            OutputDimension,
            OutputDimension,
            dpiX,
            dpiY,
            pixels);

        try
        {
            await encoder.FlushAsync();
        }
        catch (Exception exception)
        {
            throw new AgentIconException(
                AgentIconError.SaveFailed,
                exception);
        }
    }

    /// <summary>
    /// Creates a transform which:
    /// 1. scales the source so its shorter oriented edge becomes 256 px;
    /// 2. applies EXIF orientation;
    /// 3. centre-crops the oriented result to 256 × 256.
    /// </summary>
    private static BitmapTransform CreateCentreCropTransform(
        uint rawWidth,
        uint rawHeight,
        uint orientedWidth,
        uint orientedHeight)
    {
        var scale =
            OutputDimension
            / (double)Math.Min(
                orientedWidth,
                orientedHeight);

        var scaledOrientedWidth =
            Math.Max(
                OutputDimension,
                checked((uint)Math.Ceiling(
                    orientedWidth * scale)));

        var scaledOrientedHeight =
            Math.Max(
                OutputDimension,
                checked((uint)Math.Ceiling(
                    orientedHeight * scale)));

        // ScaledWidth and ScaledHeight are expressed before EXIF rotation.
        // If the oriented dimensions swap width and height, swap the source
        // scaling dimensions as well.
        var orientationSwapsAxes =
            rawWidth == orientedHeight
            && rawHeight == orientedWidth
            && (rawWidth != rawHeight
                || orientedWidth != orientedHeight);

        var scaledRawWidth = orientationSwapsAxes
            ? scaledOrientedHeight
            : scaledOrientedWidth;

        var scaledRawHeight = orientationSwapsAxes
            ? scaledOrientedWidth
            : scaledOrientedHeight;

        return new BitmapTransform
        {
            ScaledWidth = scaledRawWidth,
            ScaledHeight = scaledRawHeight,
            InterpolationMode =
                BitmapInterpolationMode.Fant,

            Bounds = new BitmapBounds
            {
                X = (scaledOrientedWidth
                     - OutputDimension) / 2,

                Y = (scaledOrientedHeight
                     - OutputDimension) / 2,

                Width = OutputDimension,
                Height = OutputDimension,
            },
        };
    }

    /// <summary>
    /// Turns an agent id into something that can appear in a filename. The result is decoration
    /// only — the GUID that follows it is what makes the name unique — so collapsing awkward
    /// characters rather than rejecting them is the right trade.
    /// </summary>
    private static string CreateSafeFileNamePart(
        string value)
    {
        var characters = value
            .Trim()
            .Select(character =>
                InvalidFileNameCharacters.Contains(character)
                    || char.IsWhiteSpace(character)
                        ? '-'
                        : character)
            .ToArray();

        var result = new string(characters)
            .Trim('-', '.');

        if (string.IsNullOrWhiteSpace(result))
        {
            result = "agent";
        }

        const int maxLength = 64;

        return result.Length <= maxLength
            ? result
            : result[..maxLength];
    }

    /// <summary>
    /// Resolves a caller-supplied temporary path, refusing anything outside the managed
    /// directory. The containment rule itself lives in <see cref="AppStorage"/> so the one that
    /// guards writes here and the one that guards reads in <c>AgentAvatar</c> cannot drift apart.
    /// </summary>
    private static string ValidatePathInsideDirectory(
        string candidatePath,
        string expectedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            candidatePath);

        var candidateFullPath =
            Path.GetFullPath(candidatePath);

        if (!AppStorage.IsPathInsideDirectory(
                candidateFullPath,
                expectedDirectory))
        {
            throw new InvalidOperationException(
                "The path is outside the managed directory.");
        }

        return candidateFullPath;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup after another failure is best-effort.
        }
    }
}
