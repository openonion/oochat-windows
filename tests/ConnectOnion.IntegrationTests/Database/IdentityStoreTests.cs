using System.Reflection;
using System.Security.Cryptography;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Data;

namespace ConnectOnion.IntegrationTests.Database;

[Collection(DatabaseCollection.Name)]
public sealed class IdentityStoreTests
{
    [Fact]
    public async Task EnsureIdentity_FirstRun_EncryptsSeedAtRestAndRoundTrips()
    {
        await DeleteStoredIdentityAsync();
        ResetStaticState();

        var generated = IdentityStore.EnsureIdentity();
        var storedSeed = await ReadStoredSeedAsync();

        Assert.NotNull(storedSeed);
        Assert.NotEqual(Convert.ToBase64String(generated.PrivateSeed.ToArray()), storedSeed);
        Assert.False(IdentityStore.WasReset);

        ResetStaticState();
        var reloaded = IdentityStore.EnsureIdentity();
        Assert.Equal(generated.Address, reloaded.Address);
        Assert.Equal(generated.PrivateSeed.ToArray(), reloaded.PrivateSeed.ToArray());
    }

    [Fact]
    public async Task EnsureIdentity_CorruptStoredSeed_ReportsResetAndPersistsReplacement()
    {
        await WriteStoredIdentityAsync("old-address", "not-valid-base64");
        ResetStaticState();
        string? reportedReason = null;
        void OnReset(string reason) => reportedReason = reason;
        IdentityStore.IdentityReset += OnReset;

        try
        {
            var replacement = IdentityStore.EnsureIdentity();

            Assert.True(IdentityStore.WasReset);
            Assert.NotNull(IdentityStore.ResetReason);
            Assert.Contains("could not be decrypted", IdentityStore.ResetReason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(IdentityStore.ResetReason, reportedReason);
            Assert.NotEqual("old-address", replacement.Address);
            Assert.NotEqual("not-valid-base64", await ReadStoredSeedAsync());
        }
        finally
        {
            IdentityStore.IdentityReset -= OnReset;
        }
    }

    [Fact]
    public async Task EnsureIdentity_PreDpapiPlaintextSeed_IsProtectedWithoutChangingAddress()
    {
        var seed = Convert.FromHexString(
            "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");
        var legacy = AgentIdentity.FromSeed(seed);
        var plaintext = Convert.ToHexString(seed).ToLowerInvariant();
        await WriteStoredIdentityAsync(legacy.Address, plaintext);
        ResetStaticState();

        var migrated = IdentityStore.EnsureIdentity();
        var stored = await ReadStoredSeedAsync();

        Assert.False(IdentityStore.WasReset, IdentityStore.ResetReason);
        Assert.Equal(legacy.Address, migrated.Address);
        Assert.Equal(seed, migrated.PrivateSeed.ToArray());
        Assert.NotNull(stored);
        Assert.NotEqual(plaintext, stored);
        Assert.Equal(seed, UnprotectSeed(stored!));

        // The rewrite survives a real store restart and remains a seed-only identity: a recovery
        // phrase cannot be reverse-derived without changing the address.
        ResetStaticState();
        var reloaded = IdentityStore.EnsureIdentity();
        Assert.Equal(legacy.Address, reloaded.Address);
        Assert.False(IdentityStore.ExportBackup().HasMnemonic);
    }

    [Fact]
    public async Task EnsureIdentity_FirstRun_MintsAPhraseThatRestoresTheSameAddress()
    {
        await DeleteStoredIdentityAsync();
        ResetStaticState();

        var generated = IdentityStore.EnsureIdentity();
        var phrase = IdentityStore.NewlyCreatedMnemonic;

        // The phrase is the whole point of the first-run path: without it the address dies with
        // this machine's DPAPI keys.
        Assert.NotNull(phrase);
        Assert.True(Bip39.Validate(phrase));
        Assert.Equal(generated.Address, AgentIdentity.FromMnemonic(phrase!).Address);

        // Stored encrypted, never in the clear beside the seed it derives.
        var storedMnemonic = await ReadStoredMnemonicAsync();
        Assert.NotNull(storedMnemonic);
        Assert.NotEqual(phrase, storedMnemonic);

        IdentityStore.AcknowledgeNewMnemonic();
        Assert.Null(IdentityStore.NewlyCreatedMnemonic);
    }

    [Fact]
    public async Task ExportBackup_AfterRestart_StillReturnsThePhrase()
    {
        await DeleteStoredIdentityAsync();
        ResetStaticState();
        var generated = IdentityStore.EnsureIdentity();
        var phrase = IdentityStore.NewlyCreatedMnemonic;

        // A backup is only useful if it survives the process that created it.
        ResetStaticState();
        var backup = IdentityStore.ExportBackup();

        Assert.Equal(generated.Address, backup.Address);
        Assert.True(backup.HasMnemonic);
        Assert.Equal(phrase, backup.Mnemonic);
        Assert.Equal(Convert.ToHexString(generated.PrivateSeed).ToLowerInvariant(), backup.SeedHex);
    }

    [Fact]
    public async Task ExportBackup_LegacySeedOnlyIdentity_OffersTheSeedInsteadOfFailing()
    {
        // What every install created before recovery phrases existed looks like: a real seed, no
        // phrase. It must keep working and still offer a backup, just a less convenient one.
        var seed = Convert.FromHexString("11223344556677889900aabbccddeeff11223344556677889900aabbccddeeff");
        var legacy = AgentIdentity.FromSeed(seed);
        await WriteStoredIdentityAsync(legacy.Address, ProtectSeed(seed));
        ResetStaticState();

        var backup = IdentityStore.ExportBackup();

        Assert.Equal(legacy.Address, backup.Address);
        Assert.False(backup.HasMnemonic);
        Assert.Null(backup.Mnemonic);
        Assert.Equal("11223344556677889900aabbccddeeff11223344556677889900aabbccddeeff", backup.SeedHex);
    }

    [Fact]
    public async Task ImportMnemonic_ReplacesTheIdentityAndSurvivesARestart()
    {
        await DeleteStoredIdentityAsync();
        ResetStaticState();
        var original = IdentityStore.EnsureIdentity();

        const string phrase =
            "legal winner thank year wave sausage worth useful legal winner thank yellow";
        string? replacedAddress = null;
        void OnReplaced(string address) => replacedAddress = address;
        IdentityStore.IdentityReplaced += OnReplaced;

        try
        {
            var imported = IdentityStore.ImportMnemonic(phrase);

            Assert.Equal("0xc6f2ac5598970c79633714d3eb5c34d7bfc3e92da58c7354b37996d9a4af3ab2", imported.Address);
            Assert.NotEqual(original.Address, imported.Address);
            // Listeners have to hear about it — every live socket is now authenticated as nobody.
            Assert.Equal(imported.Address, replacedAddress);
            // An imported phrase is one the user already has; it must not be re-presented as new.
            Assert.Null(IdentityStore.NewlyCreatedMnemonic);
            Assert.Same(imported, IdentityStore.EnsureIdentity());

            ResetStaticState();
            Assert.Equal(imported.Address, IdentityStore.EnsureIdentity().Address);
            Assert.Equal(phrase, IdentityStore.ExportBackup().Mnemonic);
        }
        finally
        {
            IdentityStore.IdentityReplaced -= OnReplaced;
        }
    }

    [Fact]
    public async Task ImportMnemonic_NormalizesBeforeStoring()
    {
        await DeleteStoredIdentityAsync();
        ResetStaticState();

        var imported = IdentityStore.ImportMnemonic(
            "  Legal   winner\tthank year\nwave sausage worth useful legal winner thank YELLOW ");

        Assert.Equal("0xc6f2ac5598970c79633714d3eb5c34d7bfc3e92da58c7354b37996d9a4af3ab2", imported.Address);
        // What is handed back as the backup must be what the user should write down.
        Assert.Equal(
            "legal winner thank year wave sausage worth useful legal winner thank yellow",
            IdentityStore.ExportBackup().Mnemonic);
    }

    [Fact]
    public async Task GenerateNewIdentity_ReplacesPersistsAndReportsTheNewIdentity()
    {
        await DeleteStoredIdentityAsync();
        ResetStaticState();
        var original = IdentityStore.EnsureIdentity();
        string? replacedAddress = null;
        void OnReplaced(string address) => replacedAddress = address;
        IdentityStore.IdentityReplaced += OnReplaced;

        try
        {
            var (replacement, phrase) = IdentityStore.GenerateNewIdentity();

            Assert.NotEqual(original.Address, replacement.Address);
            Assert.Equal(replacement.Address, replacedAddress);
            Assert.True(Bip39.Validate(phrase));
            Assert.Equal(replacement.Address, AgentIdentity.FromMnemonic(phrase).Address);
            Assert.Equal(phrase, IdentityStore.ExportBackup().Mnemonic);

            ResetStaticState();
            Assert.Equal(replacement.Address, IdentityStore.EnsureIdentity().Address);
            Assert.Equal(phrase, IdentityStore.ExportBackup().Mnemonic);
        }
        finally
        {
            IdentityStore.IdentityReplaced -= OnReplaced;
        }
    }

    [Theory]
    [InlineData("legal winner thank year wave sausage worth useful legal winner thank zebra")]
    [InlineData("not even close to a phrase")]
    public async Task ImportMnemonic_InvalidPhrase_LeavesTheStoredIdentityAlone(string phrase)
    {
        await DeleteStoredIdentityAsync();
        ResetStaticState();
        var original = IdentityStore.EnsureIdentity();

        Assert.Throws<ArgumentException>(() => IdentityStore.ImportMnemonic(phrase));

        // The failure must be total: a half-applied import would strand the user between identities.
        Assert.Equal(original.Address, IdentityStore.EnsureIdentity().Address);
        ResetStaticState();
        Assert.Equal(original.Address, IdentityStore.EnsureIdentity().Address);
    }

    [Fact]
    public async Task ImportSeed_AcceptsBothTheSeedAndTheLibsodiumSecretKeyForm()
    {
        await DeleteStoredIdentityAsync();
        ResetStaticState();

        const string seedHex = "11223344556677889900aabbccddeeff11223344556677889900aabbccddeeff";
        var expected = AgentIdentity.FromSeed(Convert.FromHexString(seedHex));

        Assert.Equal(expected.Address, IdentityStore.ImportSeed("0x" + seedHex).Address);
        // oo-chat's legacy export is the 64-byte secret key; its first half is the seed.
        var secretKeyHex = seedHex + Convert.ToHexString(expected.PublicKey).ToLowerInvariant();
        ResetStaticState();
        Assert.Equal(expected.Address, IdentityStore.ImportSeed(secretKeyHex).Address);

        // Nothing can invent a phrase for a pre-existing seed — the restore is real, the backup
        // format stays the seed.
        var backup = IdentityStore.ExportBackup();
        Assert.False(backup.HasMnemonic);
        Assert.Equal(seedHex, backup.SeedHex);
    }

    [Theory]
    [InlineData("nothex")]
    [InlineData("aabbcc")]
    public async Task ImportSeed_MalformedKey_LeavesTheStoredIdentityAlone(string input)
    {
        await DeleteStoredIdentityAsync();
        ResetStaticState();
        var original = IdentityStore.EnsureIdentity();

        Assert.Throws<ArgumentException>(() => IdentityStore.ImportSeed(input));
        Assert.Equal(original.Address, IdentityStore.EnsureIdentity().Address);
    }

    private static string ProtectSeed(byte[] seed)
    {
        var entropy = System.Text.Encoding.UTF8.GetBytes("ConnectOnion.IdentityStore.v1");
        return Convert.ToBase64String(
            ProtectedData.Protect(seed, entropy, DataProtectionScope.CurrentUser));
    }

    private static byte[] UnprotectSeed(string stored)
    {
        var entropy = System.Text.Encoding.UTF8.GetBytes("ConnectOnion.IdentityStore.v1");
        return ProtectedData.Unprotect(
            Convert.FromBase64String(stored), entropy, DataProtectionScope.CurrentUser);
    }

    private static void ResetStaticState()
    {
        var type = typeof(IdentityStore);
        type.GetField("_cached", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        type.GetField("<WasReset>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
        type.GetField("<ResetReason>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        type.GetField("<NewlyCreatedMnemonic>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, null);
    }

    private static async Task DeleteStoredIdentityAsync()
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM identity_keys;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WriteStoredIdentityAsync(string address, string seed)
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        // mnemonic is explicitly nulled: this helper writes a *pre-phrase* identity row, and these
        // tests share one database, so leaving the column alone would inherit a phrase from
        // whichever test ran before and quietly stop exercising the legacy path.
        command.CommandText = """
            INSERT INTO identity_keys (id, address, private_seed, mnemonic)
            VALUES (1, $address, $seed, NULL)
            ON CONFLICT(id) DO UPDATE SET
                address = excluded.address,
                private_seed = excluded.private_seed,
                mnemonic = NULL;
            """;
        command.Parameters.AddWithValue("$address", address);
        command.Parameters.AddWithValue("$seed", seed);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadStoredSeedAsync()
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT private_seed FROM identity_keys WHERE id = 1;";
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<string?> ReadStoredMnemonicAsync()
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT mnemonic FROM identity_keys WHERE id = 1;";
        return await command.ExecuteScalarAsync() as string;
    }
}
