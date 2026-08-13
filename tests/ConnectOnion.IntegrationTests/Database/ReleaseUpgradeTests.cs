using System.Reflection;
using System.Security.Cryptography;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.IntegrationTests.Database;

/// <summary>
/// What an MSIX upgrade must not break.
///
/// <para>An in-place upgrade keeps the package's data folder, so the new build opens the previous
/// build's database and its previous identity. Two things make that worth testing rather than
/// assuming. The database steps forward through <see cref="SchemaMigrator"/> on open, and the
/// identity is DPAPI-protected — decryptable only by the same Windows user, and regenerated
/// rather than recovered if it ever fails to decrypt. A regeneration is not a crash: the app
/// starts, works, and has a <b>different address</b>, so every agent the user had authorized now
/// rejects them and the written-down recovery phrase no longer matches. That is the failure this
/// file exists to catch, and it would otherwise be caught by a user.</para>
///
/// <para>These run headlessly against a real SQLite file and real DPAPI. They cover the data half
/// of the upgrade criterion; the install/activate/uninstall half needs a real packaged install
/// and lives in <c>scripts/Test-ReleaseUpgrade.ps1</c>.</para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ReleaseUpgradeTests
{
    /// <summary>
    /// The headline criterion: upgrading preserves the address, the seed <i>and</i> the recovery
    /// phrase. Keeping the address while losing the phrase is not a successful upgrade — the
    /// phrase is the only thing that can move the identity to another machine, and nothing can
    /// re-derive one for an existing seed afterwards.
    /// </summary>
    [Fact]
    public async Task Upgrade_PreservesAddressSeedAndRecoveryPhrase()
    {
        // --- the previous release runs and mints an identity ---
        await DeleteStoredIdentityAsync();
        ResetStaticState();

        var before = IdentityStore.EnsureIdentity();
        var phraseBefore = IdentityStore.ExportBackup().Mnemonic;
        var protectedSeedBefore = await ReadColumnAsync("private_seed");
        var protectedPhraseBefore = await ReadColumnAsync("mnemonic");

        Assert.False(string.IsNullOrWhiteSpace(phraseBefore));

        // --- the new release starts against the same data folder ---
        ResetStaticState();
        await using (var connection = await AppDatabase.OpenAsync())
        {
            // Opening is what runs the migrations. At the latest version this is a no-op, which
            // is exactly the case a same-schema upgrade has to survive.
            await SchemaMigrator.ApplyAsync(connection);
        }

        var after = IdentityStore.EnsureIdentity();
        var backupAfter = IdentityStore.ExportBackup();

        Assert.False(IdentityStore.WasReset, $"the identity was regenerated: {IdentityStore.ResetReason}");
        Assert.Equal(before.Address, after.Address);
        Assert.Equal(before.PrivateSeed.ToArray(), after.PrivateSeed.ToArray());
        Assert.Equal(phraseBefore, backupAfter.Mnemonic);
        Assert.True(backupAfter.HasMnemonic);

        // The protected blobs are the stored form; an upgrade that re-encrypted them would still
        // decrypt correctly here, so comparing the ciphertext is what proves nothing rewrote them.
        Assert.Equal(protectedSeedBefore, await ReadColumnAsync("private_seed"));
        Assert.Equal(protectedPhraseBefore, await ReadColumnAsync("mnemonic"));

        // And the phrase still means what it says.
        Assert.Equal(after.Address, AgentIdentity.FromMnemonic(backupAfter.Mnemonic!).Address);
    }

    /// <summary>
    /// Upgrading from a release that predates schema v5. Those identities have no phrase and can
    /// never be given one — regenerating a seed to attach a phrase would change the address every
    /// agent already authorized — so the correct outcome is: identity intact, phrase still absent,
    /// and the backup falls back to the raw seed rather than failing.
    /// </summary>
    [Fact]
    public async Task Upgrade_FromPreMnemonicRelease_KeepsTheIdentityAndOffersTheSeedInstead()
    {
        var legacy = AgentIdentity.Generate();
        await WriteLegacyIdentityAsync(legacy);
        ResetStaticState();

        var after = IdentityStore.EnsureIdentity();
        var backup = IdentityStore.ExportBackup();

        Assert.False(IdentityStore.WasReset, $"a pre-v5 identity was regenerated: {IdentityStore.ResetReason}");
        Assert.Equal(legacy.Address, after.Address);
        Assert.Equal(legacy.PrivateSeed.ToArray(), after.PrivateSeed.ToArray());

        Assert.False(backup.HasMnemonic);
        Assert.Equal(legacy.Address, backup.Address);
        Assert.Equal(Convert.ToHexString(legacy.PrivateSeed.ToArray()), backup.SeedHex, ignoreCase: true);

        // Still null afterwards: reading a legacy identity must not invent a phrase for it.
        Assert.Null(await ReadColumnAsync("mnemonic"));
    }

    /// <summary>
    /// The v4 → v5 migration against a <i>real</i> DPAPI blob rather than a placeholder string.
    /// The column is TEXT holding base64, so a migration that re-encoded or trimmed it would look
    /// fine against the literal <c>'protected-seed-blob'</c> the schema test uses and destroy an
    /// actual key.
    /// </summary>
    [Fact]
    public async Task SchemaUpgrade_FromVersionFour_LeavesARealProtectedSeedByteIdentical()
    {
        var identity = AgentIdentity.Generate();
        var protectedSeed = Protect(identity.PrivateSeed.ToArray());

        await using var initialized = await AppDatabase.OpenAsync();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await AppDatabase.CreateSchemaAsync(connection);

        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                PRAGMA user_version = 4;
                INSERT INTO identity_keys (id, address, private_seed)
                VALUES (1, $address, $seed);
                """;
            seed.Parameters.AddWithValue("$address", identity.Address);
            seed.Parameters.AddWithValue("$seed", protectedSeed);
            await seed.ExecuteNonQueryAsync();
        }

        await SchemaMigrator.ApplyAsync(connection);

        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT private_seed FROM identity_keys WHERE id = 1;";
        var stored = (string?)await read.ExecuteScalarAsync();

        Assert.Equal(protectedSeed, stored);

        // The real assertion: it still decrypts, and to the same key.
        var unprotected = ProtectedData.Unprotect(
            Convert.FromBase64String(stored!), Entropy, DataProtectionScope.CurrentUser);
        Assert.Equal(identity.PrivateSeed.ToArray(), unprotected);
    }

    /// <summary>
    /// Conversations are the other half of "application data preserved". Cheap to assert and the
    /// thing a user would notice first.
    /// </summary>
    [Fact]
    public async Task Upgrade_PreservesConversationsAndMessages()
    {
        const string conversationId = "upgrade-conversation";
        await CreateSessionAsync(conversationId);

        var repository = new ConversationRepository();
        await repository.UpsertMessagesAsync(conversationId, new[]
        {
            new ChatMessage { Id = 1, Role = ChatRole.User, Content = "before the upgrade" },
            new ChatMessage { Id = 2, Role = ChatRole.Agent, Content = "acknowledged" },
        });

        await using (var connection = await AppDatabase.OpenAsync())
        {
            await SchemaMigrator.ApplyAsync(connection);
        }

        var messages = await repository.LoadMessagesAsync(conversationId);

        Assert.Equal(2, messages.Count);
        Assert.Equal("before the upgrade", messages[0].Content);
        Assert.Equal("acknowledged", messages[1].Content);
    }

    // --- helpers -------------------------------------------------------------------------------

    // Must match IdentityStore's own entropy, or Unprotect fails and the test proves nothing.
    private static readonly byte[] Entropy =
        System.Text.Encoding.UTF8.GetBytes("ConnectOnion.IdentityStore.v1");

    private static string Protect(byte[] value)
        => Convert.ToBase64String(
            ProtectedData.Protect(value, Entropy, DataProtectionScope.CurrentUser));

    /// <summary>Writes the row shape a pre-schema-v5 release left behind: a protected seed and a
    /// null phrase.</summary>
    private static async Task WriteLegacyIdentityAsync(AgentIdentity identity)
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO identity_keys (id, address, private_seed, mnemonic)
            VALUES (1, $address, $seed, NULL)
            ON CONFLICT(id) DO UPDATE SET
                address = excluded.address,
                private_seed = excluded.private_seed,
                mnemonic = NULL;
            """;
        command.Parameters.AddWithValue("$address", identity.Address);
        command.Parameters.AddWithValue("$seed", Protect(identity.PrivateSeed.ToArray()));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DeleteStoredIdentityAsync()
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM identity_keys;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadColumnAsync(string column)
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        // Interpolated because SQLite cannot parameterize an identifier; the only two call sites
        // pass literals from this file.
        command.CommandText = $"SELECT {column} FROM identity_keys WHERE id = 1;";
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task CreateSessionAsync(string sessionId)
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO agents (id, name, address) VALUES ('upgrade-agent', 'Agent', '0xup');
            INSERT OR IGNORE INTO sessions (id, agent_id, title, created_at, updated_at)
            VALUES ($id, 'upgrade-agent', 'Upgrade', '2026-01-01', '2026-01-01');
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Models a process restart: the store caches the identity in a static for the
    /// lifetime of the process, so without this an "upgrade" would just re-read that cache.</summary>
    private static void ResetStaticState()
    {
        var type = typeof(IdentityStore);
        type.GetField("_cached", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        type.GetField("<WasReset>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
        type.GetField("<ResetReason>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        type.GetField("<NewlyCreatedMnemonic>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, null);
    }
}
