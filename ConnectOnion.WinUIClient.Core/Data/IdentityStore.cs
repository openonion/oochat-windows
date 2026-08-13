using System.Security.Cryptography;
using System.Text;
using ConnectOnion.Protocol;
using Microsoft.Extensions.Logging;

namespace ConnectOnion.WinUIClient.Data;

/// <summary>
/// What a user needs to write down to restore this identity elsewhere.
/// </summary>
/// <param name="Address">The identity's <c>0x</c> address, for confirming a restore landed right.</param>
/// <param name="Mnemonic">
/// The BIP39 recovery phrase, or null for an identity minted before phrases existed — those seeds
/// came from a raw CSPRNG draw and no phrase encodes them. Null is the signal to offer
/// <paramref name="SeedHex"/> instead, not an error.
/// </param>
/// <param name="SeedHex">
/// The raw 32-byte Ed25519 seed as lowercase hex. Always available, always sufficient, and the only
/// backup a pre-phrase identity has. Equivalent in power to the phrase — treat it the same way.
/// </param>
public sealed record IdentityBackup(string Address, string? Mnemonic, string SeedHex)
{
    public bool HasMnemonic => !string.IsNullOrEmpty(Mnemonic);
}

/// <summary>
/// Loads, generates, backs up and restores the app's Ed25519 identity, persisting it to
/// <c>%AppData%\ConnectOnion\connectonion.db</c>. Native equivalent of the SDK's
/// <c>ensureKeys()</c> (browser path stored keys in localStorage). The 32-byte
/// seed is encrypted at rest with Windows DPAPI (<see cref="ProtectedData"/>,
/// <see cref="DataProtectionScope.CurrentUser"/>) and the resulting blob is stored
/// as base64 — SQLite alone is not an appropriate place for raw key material,
/// and DPAPI ties decryption to the logged-in Windows user/machine so the
/// on-disk .db file is not portable or readable outside that account.
///
/// <para><b>DPAPI is what makes the database safe, and also what makes it worthless as a backup.</b>
/// A copied <c>.db</c>, a restored profile, or a new machine cannot decrypt the seed, so from this
/// build on a new identity is minted from a BIP39 phrase (<see cref="AgentIdentity.GenerateWithMnemonic"/>)
/// and the phrase is stored beside it. The phrase is the portable backup — it restores the same
/// address here, in the Python CLI, or in oo-chat. Identities created before this build keep working
/// untouched and back up via their raw seed instead; regenerating them would silently change the
/// address every agent has already authorized.</para>
/// </summary>
public static class IdentityStore
{
    private static readonly Action<ILogger, string, Exception?> LogIdentityReset =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "IdentityReset"),
            "Local identity was reset ({Reason}); this device signs as a new address and every agent must re-authorize it");

    private static readonly Action<ILogger, Exception?> LogIdentityPersistFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(2, "IdentityPersistFailed"),
            "The local identity could not be saved; a new address will be minted on the next launch");

    /// <summary>
    /// Set once at startup. This is a static class reached from connection setup — there is no
    /// instance for the container to inject into — so it takes the same shape as
    /// <c>NotificationLog</c>: a facade that is a no-op until the host wires it up.
    /// </summary>
    private static ILogger? _logger;

    /// <summary>Called from <c>App</c> once the host is built.</summary>
    public static void Configure(ILoggerFactory loggerFactory)
        => _logger = loggerFactory.CreateLogger("ConnectOnion.Identity");

    // Extra DPAPI entropy so another process running as the same Windows user
    // cannot blindly call CryptUnprotectData on our blob without knowing this
    // value. Not a secret by itself (it's compiled into the binary) — it's
    // defense in depth on top of the per-user DPAPI master key, not instead of it.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ConnectOnion.IdentityStore.v1");

    // Process-lifetime cache of the identity. Memoization first — EnsureIdentity is called
    // synchronously from connection setup and would otherwise hit the database and DPAPI on every
    // connect — but no longer *only* memoization: an import replaces it (see ReplaceIdentity), so
    // treat it as the current value rather than a write-once field.
    private static AgentIdentity? _cached;

    // Guards the read-modify-write of _cached/_newMnemonic against two callers importing at once,
    // and against an import racing the lazy first EnsureIdentity from a connection.
    private static readonly object Gate = new();

    /// <summary>
    /// Raised when an identity row existed on disk but could not be recovered, so a brand-new
    /// identity was generated in its place. This is not a routine event: the app's address changes,
    /// and any authorization an agent had granted the old address is gone. The user has to be told
    /// — silently swapping identities is how someone loses access without ever knowing why.
    /// Fires once per process, before the replacement identity is returned.
    /// </summary>
    public static event Action<string>? IdentityReset;

    /// <summary>
    /// Raised after an import or <see cref="GenerateNewIdentity"/> deliberately swaps the identity,
    /// carrying the new address. Unlike <see cref="IdentityReset"/> this one is deliberate, but it
    /// has the same consequence — every live socket authenticated as the old address is now
    /// authenticated as nobody, so listeners must drop their connections.
    /// </summary>
    public static event Action<string>? IdentityReplaced;

    /// <summary>
    /// Set when <see cref="EnsureIdentity"/> had to discard an unreadable identity. Latched because
    /// the identity is usually first needed before any window exists to show a message — the shell
    /// checks this on load (see <c>MainWindow.Notifications.cs</c>) and reports it then.
    /// </summary>
    public static bool WasReset { get; private set; }

    /// <summary>Human-readable reason for <see cref="WasReset"/>, or null.</summary>
    public static string? ResetReason { get; private set; }

    /// <summary>
    /// The recovery phrase of an identity this process just minted, held until the UI has shown it
    /// to the user exactly once (then cleared via <see cref="AcknowledgeNewMnemonic"/>).
    ///
    /// <para>Latched for the same reason <see cref="WasReset"/> is: the identity is created the
    /// first time anything needs to connect, which is generally before a window exists to show a
    /// dialog. A phrase the user never sees is a backup that does not exist, so the shell picks
    /// this up on load and presents it.</para>
    /// </summary>
    public static string? NewlyCreatedMnemonic { get; private set; }

    /// <summary>Clears <see cref="NewlyCreatedMnemonic"/> once the phrase has been shown.</summary>
    public static void AcknowledgeNewMnemonic()
    {
        lock (Gate) NewlyCreatedMnemonic = null;
    }

    public static AgentIdentity EnsureIdentity()
    {
        lock (Gate)
        {
            if (_cached is not null) return _cached;

            var (identity, _, fault) = Load();
            if (identity is not null)
            {
                _cached = identity;
                return _cached;
            }

            // No row at all is the normal first-run path — generate quietly. A row we could not read
            // is a different story: report it, because the old identity is being thrown away.
            if (fault is not null) ReportReset(fault);

            _cached = Create();
            return _cached;
        }
    }

    /// <summary>
    /// Everything needed to restore this identity elsewhere, decrypted on demand.
    ///
    /// <para>Deliberately not cached: the phrase and the seed are the same secret in two shapes, and
    /// there is no reason to keep a plaintext copy of either alive for the process lifetime when
    /// the call happens at most a few times, on a settings page, at human speed.</para>
    /// </summary>
    public static IdentityBackup ExportBackup()
    {
        // Makes sure an identity exists at all before reporting there is nothing to back up.
        var identity = EnsureIdentity();

        var (_, mnemonic, _) = Load();
        return new IdentityBackup(
            identity.Address,
            mnemonic,
            Convert.ToHexString(identity.PrivateSeed).ToLowerInvariant());
    }

    /// <summary>
    /// Replaces the stored identity with the one a BIP39 phrase encodes.
    ///
    /// <para><b>Destructive and irreversible.</b> The previous seed is overwritten, so unless the
    /// caller backed it up first it is gone — and the app's address changes, meaning every agent
    /// that authorized the old address must authorize the new one. Confirm with the user before
    /// calling, and drop every live connection after (see <see cref="IdentityReplaced"/>).</para>
    /// </summary>
    /// <exception cref="ArgumentException">The phrase is not a valid BIP39 mnemonic.</exception>
    /// <exception cref="InvalidOperationException">The new identity could not be written to disk.</exception>
    public static AgentIdentity ImportMnemonic(string mnemonic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mnemonic);

        // Store the canonical form, not the user's keystrokes: the phrase is displayed back to them
        // later as their backup, and "Legal  Winner\n..." is not what they should be writing down.
        var canonical = Bip39.Normalize(mnemonic);
        var identity = AgentIdentity.FromMnemonic(canonical);
        return ReplaceIdentity(identity, canonical);
    }

    /// <summary>
    /// Creates and persists a fresh BIP39-backed identity, replacing the current one.
    /// OpenOnion creates the corresponding account when this identity first authenticates.
    ///
    /// <para><b>Destructive and irreversible.</b> The caller must confirm that the current backup
    /// is safe, refuse the operation while a run is active, disconnect all existing sockets, and
    /// show the returned phrase immediately.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The new identity could not be written to disk.</exception>
    public static (AgentIdentity Identity, string Mnemonic) GenerateNewIdentity()
    {
        var (identity, mnemonic) = AgentIdentity.GenerateWithMnemonic();
        return (ReplaceIdentity(identity, mnemonic), mnemonic);
    }

    /// <summary>
    /// Replaces the stored identity from a raw seed, for restoring a backup taken before recovery
    /// phrases existed (or one exported from another client). Accepts the 32-byte Ed25519 seed as
    /// 64 hex characters, or the 64-byte libsodium secret key as 128 hex characters (its first half
    /// <i>is</i> the seed) — that longer form is what oo-chat's legacy key export produces. A
    /// leading <c>0x</c> is tolerated.
    ///
    /// <para>Same destructive semantics as <see cref="ImportMnemonic"/>, and the restored identity
    /// still has no phrase — nothing can invent one for an existing seed.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The text is not a seed of a recognized length.</exception>
    /// <exception cref="InvalidOperationException">The new identity could not be written to disk.</exception>
    public static AgentIdentity ImportSeed(string seedHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedHex);

        var trimmed = seedHex.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];

        byte[] decoded;
        try
        {
            decoded = Convert.FromHexString(trimmed);
        }
        catch (FormatException)
        {
            throw new ArgumentException("That is not a valid key — expected hex characters.", nameof(seedHex));
        }

        if (decoded.Length is not (32 or 64))
        {
            throw new ArgumentException(
                "A private key must be 64 hex characters (a 32-byte seed) or 128 (a 64-byte secret key).",
                nameof(seedHex));
        }

        var seed = decoded.Length == 32 ? decoded : decoded[..32];
        try
        {
            return ReplaceIdentity(AgentIdentity.FromSeed(seed), mnemonic: null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
            if (!ReferenceEquals(seed, decoded)) CryptographicOperations.ZeroMemory(seed);
        }
    }

    private static AgentIdentity ReplaceIdentity(AgentIdentity identity, string? mnemonic)
    {
        lock (Gate)
        {
            // Throws rather than swallowing, unlike the first-run path: a user who was told the
            // import succeeded, and then finds the old address back after a restart, has lost the
            // phrase they thought they no longer needed.
            Persist(identity, mnemonic, throwOnFailure: true);
            _cached = identity;

            // An imported phrase is one the user already has written down — re-showing it as
            // "your new recovery phrase" would be noise, and worse, would suggest they need to
            // store something new. Clear any pending first-run phrase this replaces.
            NewlyCreatedMnemonic = null;
        }

        try { IdentityReplaced?.Invoke(identity.Address); }
        catch { /* a listener must never leave the store reporting a failed import */ }

        return identity;
    }

    /// <summary>
    /// Reads the stored identity. Returns <c>(null, null, null)</c> when there is simply nothing
    /// stored yet (first run), and <c>(null, null, reason)</c> when a row exists but is unusable —
    /// the caller must not treat those two the same, which is exactly the bug this split fixes.
    /// The middle element is the recovery phrase, null for a pre-phrase identity.
    /// </summary>
    private static (AgentIdentity? Identity, string? Mnemonic, string? Fault) Load()
    {
        string storedAddress;
        string storedSeed;
        string? storedMnemonic;

        try
        {
            // Blocking here is safe, but NOT for the reason this comment used to give.
            //
            // It said "AppDatabase.OpenAsync awaits with ConfigureAwait(false) the whole way
            // down". That is not true and cannot easily be made true: the explicit awaits do carry
            // it, but the `await using` declarations in that chain await DisposeAsync at scope
            // exit *without* it, and there is no readable syntax that fixes that while keeping the
            // connection usably typed. CA2007 flags 30 such sites in AppDatabase alone.
            //
            // What actually makes this safe is that no await in the chain ever yields.
            // Microsoft.Data.Sqlite's async methods are synchronous internally — SQLite has no
            // async I/O — and DbConnection.DisposeAsync's default implementation calls Dispose and
            // returns a completed ValueTask. Nothing is ever posted back to the captured context,
            // so the blocked thread is never the thread a continuation is waiting on.
            //
            // The distinction is not academic. Under the old claim, "are the ConfigureAwait calls
            // still there?" looked like a sufficient check, and it never was. The real invariant is
            // narrower and worth stating plainly: **this chain must not gain a genuinely
            // asynchronous await.** A real network or file operation in schema init would deadlock
            // first-run startup on the UI thread, and adding ConfigureAwait(false) to it would not
            // help — only making this method async would. See IdentityStoreBlockingContractTests.
#pragma warning disable VSTHRD002
            using var connection = AppDatabase.OpenAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT address, private_seed, mnemonic
                FROM identity_keys
                WHERE id = 1;
                """;
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return (null, null, null);          // first run — nothing stored

            storedAddress = reader.GetString(0);
            storedSeed = reader.GetString(1);
            storedMnemonic = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (string.IsNullOrEmpty(storedSeed))
                return (null, null, "the stored identity was empty");
        }
        catch (Exception ex)
        {
            // The database itself is unreadable. Don't claim the identity was lost — we don't know
            // that — but don't pretend this is a clean first run either.
            return (null, null, $"the identity could not be read from the database ({ex.Message})");
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(storedSeed);
            var seed = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);

            if (seed.Length != 32)
                return (null, null, "the stored identity was the wrong size");

            return (AgentIdentity.FromSeed(seed), TryUnprotectMnemonic(storedMnemonic), null);
        }
        catch (Exception ex)
        {
            // Builds before DPAPI stored exactly 32 seed bytes as 64 hexadecimal characters.
            // Recognise only that historical shape and verify its derived address before
            // re-protecting it. A copied DPAPI blob, corrupt base64, or arbitrary hand-edited text
            // must still take the visible reset path below rather than being treated as a key.
            if (TryMigratePlaintextSeed(storedAddress, storedSeed, out var migrated))
                return (migrated, null, null);

            // DPAPI is scoped to the Windows user + machine. The usual causes are a copied .db file,
            // or a restored user profile. Historical plaintext rows are migrated above.
            return (null, null, $"the stored identity could not be decrypted ({ex.Message})");
        }
    }

    private static bool TryMigratePlaintextSeed(
        string storedAddress,
        string storedSeed,
        out AgentIdentity? identity)
    {
        identity = null;
        if (storedSeed.Length != 64 || !storedSeed.All(Uri.IsHexDigit)) return false;

        byte[] seed;
        try { seed = Convert.FromHexString(storedSeed); }
        catch (FormatException) { return false; }

        try
        {
            var candidate = AgentIdentity.FromSeed(seed);
            if (!string.Equals(candidate.Address, storedAddress, StringComparison.OrdinalIgnoreCase))
                return false;

            // Keep using the recovered identity even if the best-effort rewrite fails. That leaves
            // the legacy row available for another migration attempt on the next launch instead of
            // replacing an address every saved agent has already authorized.
            Persist(candidate, mnemonic: null, throwOnFailure: false);
            identity = candidate;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>
    /// Decrypts the stored phrase, or returns null if there is none or it cannot be read.
    ///
    /// <para>An unreadable phrase is deliberately <i>not</i> an error: the seed decrypted fine (the
    /// caller already has a working identity), so the only thing lost is the convenient backup
    /// format, and the raw-seed export still covers the user. Failing the whole load here would
    /// throw away a perfectly good identity over its backup copy.</para>
    /// </summary>
    private static string? TryUnprotectMnemonic(string? storedMnemonic)
    {
        if (string.IsNullOrEmpty(storedMnemonic)) return null;

        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(storedMnemonic), Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var phrase = Encoding.UTF8.GetString(bytes);
                // A phrase that no longer validates cannot restore anything, and showing it as a
                // backup would be worse than showing none.
                return Bip39.Validate(phrase) ? phrase : null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch
        {
            return null;
        }
    }

    private static void ReportReset(string reason)
    {
        WasReset = true;
        ResetReason = reason;

        if (_logger is { } logger) LogIdentityReset(logger, reason, null);

        try { IdentityReset?.Invoke(reason); }
        catch { /* a listener must never block identity creation */ }
    }

    private static AgentIdentity Create()
    {
        // Phrase-derived from here on, so this identity has a backup that survives the machine.
        var (identity, mnemonic) = AgentIdentity.GenerateWithMnemonic();
        Persist(identity, mnemonic, throwOnFailure: false);

        // Latched for the shell to present. Only set when the phrase actually reached the disk:
        // telling someone to write down the phrase for an identity that will not survive a restart
        // sends them to back up an address they will never have again.
        if (ReadStoredAddress() == identity.Address) NewlyCreatedMnemonic = mnemonic;

        return identity;
    }

    private static void Persist(AgentIdentity identity, string? mnemonic, bool throwOnFailure)
    {
        try
        {
            var protectedSeed = ProtectedData.Protect(
                identity.PrivateSeed.ToArray(), Entropy, DataProtectionScope.CurrentUser);
            var storedSeed = Convert.ToBase64String(protectedSeed);

            string? storedMnemonic = null;
            if (!string.IsNullOrEmpty(mnemonic))
            {
                var mnemonicBytes = Encoding.UTF8.GetBytes(mnemonic);
                try
                {
                    storedMnemonic = Convert.ToBase64String(
                        ProtectedData.Protect(mnemonicBytes, Entropy, DataProtectionScope.CurrentUser));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(mnemonicBytes);
                }
            }

            // Safe for the same reason as Load's open — see the note there.
#pragma warning disable VSTHRD002
            using var connection = AppDatabase.OpenAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            using var command = connection.CreateCommand();
            // mnemonic is overwritten, not coalesced: after an import the row must describe the
            // identity that is actually stored. Keeping a previous phrase would leave a backup
            // pointing at an address the app no longer has.
            command.CommandText = """
                INSERT INTO identity_keys (id, address, private_seed, mnemonic)
                VALUES (1, $address, $private_seed, $mnemonic)
                ON CONFLICT(id) DO UPDATE SET
                    address = excluded.address,
                    private_seed = excluded.private_seed,
                    mnemonic = excluded.mnemonic;
                """;
            AppDatabase.Add(command, "$address", identity.Address);
            AppDatabase.Add(command, "$private_seed", storedSeed);
            AppDatabase.Add(command, "$mnemonic", storedMnemonic);
            command.ExecuteNonQuery();
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            // Persist is best-effort on the first-run path; a fresh identity per run still works —
            // but "works" here means the app functions, not that the user is unaffected. An identity
            // that never persists gives a new address on every launch, so every agent has to
            // re-authorize this client each time. Nothing surfaces this in the UI — unlike the
            // unreadable-identity path above, it raises no IdentityReset and sets no WasReset flag
            // — so the log line is the only trace, which is why it is a real one and not just a
            // Debug write that a Release build compiles away.
            if (_logger is { } logger) LogIdentityPersistFailed(logger, ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The identity could not be saved ({ex.Message}). The previous identity is unchanged.", ex);
        }
    }

    /// <summary>The address currently on disk, or null if the row is missing or unreadable.
    /// Used to confirm a write landed before promising the user their phrase is worth keeping.</summary>
    private static string? ReadStoredAddress()
    {
        try
        {
#pragma warning disable VSTHRD002
            using var connection = AppDatabase.OpenAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT address FROM identity_keys WHERE id = 1;";
            return command.ExecuteScalar() as string;
        }
        catch
        {
            return null;
        }
    }
}
