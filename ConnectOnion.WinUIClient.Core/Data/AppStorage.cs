namespace ConnectOnion.WinUIClient.Data;

/// <summary>
/// Shared filesystem plumbing for the local repositories.
///
/// Unpackaged data lives under <c>%AppData%\ConnectOnion</c>; an installed MSIX uses its
/// package <c>LocalState\ConnectOnion</c> folder. SQLite is the active store:
/// <code>
///   connectonion.db
/// </code>
///
/// <para><b>Deliberately holds no JSON helper.</b> It used to carry a generic
/// <c>ReadAsync&lt;T&gt;</c>/<c>WriteAsync&lt;T&gt;</c> file round-trip left over from the
/// pre-SQLite store, with no caller anywhere in the app. Those overloads are reflection-based,
/// so they contributed <c>IL2026</c> to the trim inventory and would have thrown
/// <see cref="System.NotSupportedException"/> the first time anyone did call them from a trimmed
/// build. A JSON blob that belongs to us goes through a source-generated context
/// (<see cref="AppJsonContext"/>, <see cref="ConversationJsonContext"/>); anything shaped by the
/// host goes through <see cref="ConnectOnion.Protocol.WireJson"/>. There is no third way, and
/// that is the point — see <c>docs/TRIMMING.md</c>.</para>
/// </summary>
public static class AppStorage
{
    /// <summary>
    /// Optional data-root override for isolated automation and portable development runs.
    /// Production leaves this unset and selects the root from the deployment model.
    /// </summary>
    public const string DataRootEnvironmentVariable = "CONNECTONION_DATA_ROOT";

    /// <summary>Directory segment persisted inside an agent's <c>icon_path</c>.</summary>
    private const string AgentIconsFolderName = "avatars";

    private static readonly StorageLocation Location = ResolveStorageLocation();

    public static readonly string RootDir = Location.RootDir;

    public static readonly string ConversationsDir = Path.Combine(RootDir, "conversations");

    /// <summary>
    /// Content store for user-selected and agent-produced conversation images.
    /// Files are named by content hash so storing the same image is idempotent
    /// and remote filenames are never trusted as local paths.
    /// </summary>
    public static readonly string ImageCacheDir = Path.Combine(RootDir, "cache", "images");

    /// <summary>
    /// Permanent store for user-selected agent icons. <c>agents.icon_path</c> holds a path
    /// relative to <see cref="RootDir"/> (<c>avatars/agent-….png</c>) rather than an absolute
    /// one, so a portable install that moves between folders keeps resolving its own icons.
    /// </summary>
    public static readonly string AgentIconsDir = Path.Combine(RootDir, AgentIconsFolderName);

    /// <summary>
    /// Scratch space holding a processed icon between "user picked an image" and "the agent it
    /// belongs to was saved". Nothing here is referenced by the database, so
    /// <see cref="PurgeTemporaryAgentIcons"/> may empty it at startup.
    /// </summary>
    public static readonly string TemporaryAgentIconsDir =
        Path.Combine(RootDir, "temp", AgentIconsFolderName);

    /// <summary>Rolling structured application logs.</summary>
    public static readonly string LogsDir = Path.Combine(RootDir, "logs");

    public static void EnsureDirectories()
    {
        MigrateLegacyPackagedData();
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(ConversationsDir);
        Directory.CreateDirectory(ImageCacheDir);
        Directory.CreateDirectory(AgentIconsDir);
        Directory.CreateDirectory(TemporaryAgentIconsDir);
        Directory.CreateDirectory(LogsDir);
    }

    public static string PathFor(string fileName) => Path.Combine(RootDir, fileName);

    /// <summary>
    /// Builds the value stored in <c>agents.icon_path</c>. Always forward-slashed and always
    /// relative, which is what <see cref="GetAgentIconAbsolutePath"/> expects to resolve.
    /// </summary>
    public static string CreateAgentIconRelativePath(string fileName)
        => $"{AgentIconsFolderName}/{ValidateLeafFileName(fileName)}";

    /// <summary>
    /// Resolves a stored icon path against the current data root.
    ///
    /// The containment check is the point: <c>icon_path</c> is a database column, and a database
    /// that was hand-edited or restored from elsewhere must not be able to make the app open —
    /// or, through the delete paths, remove — a file outside the managed avatars directory.
    /// </summary>
    /// <exception cref="InvalidOperationException">The path escapes <see cref="AgentIconsDir"/>.</exception>
    public static string GetAgentIconAbsolutePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        var absolutePath = Path.GetFullPath(Path.Combine(RootDir, normalized));

        if (!IsPathInsideDirectory(absolutePath, AgentIconsDir))
        {
            throw new InvalidOperationException(
                "The agent icon path must remain inside the managed avatars directory.");
        }

        return absolutePath;
    }

    /// <summary>
    /// Non-throwing <see cref="GetAgentIconAbsolutePath"/> for display paths, where a stored value
    /// that no longer resolves means "fall back to the initial", not "fail".
    /// </summary>
    public static bool TryGetAgentIconAbsolutePath(string? relativePath, out string? absolutePath)
    {
        absolutePath = null;
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        try
        {
            absolutePath = GetAgentIconAbsolutePath(relativePath);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or InvalidOperationException
                                              or NotSupportedException
                                              or PathTooLongException)
        {
            return false;
        }
    }

    public static string GetTemporaryAgentIconAbsolutePath(string fileName)
        => Path.Combine(TemporaryAgentIconsDir, ValidateLeafFileName(fileName));

    public static string GetPermanentAgentIconAbsolutePath(string fileName)
        => Path.Combine(AgentIconsDir, ValidateLeafFileName(fileName));

    /// <summary>
    /// True when <paramref name="candidatePath"/> resolves to something inside
    /// <paramref name="expectedDirectory"/>. The trailing separator matters: without it
    /// <c>…\avatars-backup</c> would pass as being inside <c>…\avatars</c>.
    /// </summary>
    public static bool IsPathInsideDirectory(string candidatePath, string expectedDirectory)
    {
        var candidateFullPath = Path.GetFullPath(candidatePath);
        var directoryFullPath = Path
            .GetFullPath(expectedDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return candidateFullPath.StartsWith(directoryFullPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Empties the temporary icon directory. An icon lives there only while an Add Agent form is
    /// open, so anything found at startup was left by a process that died mid-pick and is
    /// unreachable — nothing in the database ever points here.
    ///
    /// Startup only, and never from <see cref="EnsureDirectories"/>: the picker calls that while a
    /// form is open, and purging then would delete the icon the user is about to save.
    /// </summary>
    public static void PurgeTemporaryAgentIcons()
    {
        try
        {
            if (!Directory.Exists(TemporaryAgentIconsDir)) return;

            foreach (var file in Directory.EnumerateFiles(TemporaryAgentIconsDir))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception exception) when (exception is IOException
                                                      or UnauthorizedAccessException)
                {
                    // One undeletable leftover must not stop the sweep or delay startup.
                }
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or DirectoryNotFoundException)
        {
            // Best-effort housekeeping.
        }
    }

    /// <summary>
    /// Accepts only a bare filename. Generated names are safe by construction; this exists so a
    /// future caller cannot smuggle a relative segment into a path that is later trusted.
    /// </summary>
    private static string ValidateLeafFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var trimmed = fileName.Trim();
        var leafFileName = Path.GetFileName(trimmed);

        if (!string.Equals(trimmed, leafFileName, StringComparison.Ordinal)
            || leafFileName is "." or ".."
            || leafFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "The value must be a filename with no directory components.",
                nameof(fileName));
        }

        return leafFileName;
    }

    private static StorageLocation ResolveStorageLocation()
    {
        var configured = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new StorageLocation(
                SelectRootDir(configured, packagedLocalFolder: null, roamingAppDataFolder: null),
                null);
        }

        try
        {
            // ApplicationData requires package identity. Keeping it behind this guarded branch
            // preserves the unpackaged CLI/portable path while making an installed MSIX use the
            // stable LocalState location that Windows backs up and retains across upgrades.
            var applicationData = Windows.Storage.ApplicationData.Current;
            var root = SelectRootDir(
                configured: null,
                packagedLocalFolder: applicationData.LocalFolder.Path,
                roamingAppDataFolder: null);

            // Older development builds wrote through Environment.SpecialFolder.ApplicationData.
            // Windows virtualized those writes into LocalCache\Roaming. Migrate that one legacy
            // location before the first LocalState directory is created so existing developer
            // conversations and the DPAPI-protected identity are not silently abandoned.
            var legacyRoot = Path.Combine(
                applicationData.LocalCacheFolder.Path,
                "Roaming",
                "ConnectOnion");
            return new StorageLocation(root, legacyRoot);
        }
        catch (InvalidOperationException)
        {
            // Unpackaged applications have no ApplicationData.Current.
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Some unpackaged Windows App SDK hosts report the missing identity as a COM error.
        }

        return new StorageLocation(
            SelectRootDir(
                configured: null,
                packagedLocalFolder: null,
                roamingAppDataFolder: Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData)),
            null);
    }

    internal static string SelectRootDir(
        string? configured,
        string? packagedLocalFolder,
        string? roamingAppDataFolder)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        if (!string.IsNullOrWhiteSpace(packagedLocalFolder))
        {
            return Path.Combine(packagedLocalFolder, "ConnectOnion");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(roamingAppDataFolder);
        return Path.Combine(roamingAppDataFolder, "ConnectOnion");
    }

    private static void MigrateLegacyPackagedData()
    {
        var legacyRoot = Location.LegacyRootDir;
        if (legacyRoot is null ||
            Directory.Exists(RootDir) ||
            !Directory.Exists(legacyRoot))
        {
            return;
        }

        var parent = Path.GetDirectoryName(RootDir);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        Directory.Move(legacyRoot, RootDir);
    }

    private sealed record StorageLocation(string RootDir, string? LegacyRootDir);
}
