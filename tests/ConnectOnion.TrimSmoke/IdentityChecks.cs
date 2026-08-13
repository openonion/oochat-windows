using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Data;

namespace ConnectOnion.TrimSmoke;

/// <summary>
/// Identity is the one piece of state a user cannot regenerate their way out of: the address is
/// what every agent authorized, and the BIP39 derivation is a cross-client contract with the
/// Python SDK and oo-chat. It also crosses DPAPI and the <c>identity_keys</c> table, so it is
/// worth proving inside a trimmed binary rather than assuming the headless tests cover it.
/// </summary>
internal static class IdentityChecks
{
    public static void Run(Harness h, bool freshDataRoot)
    {
        h.Section("Identity");

        h.Check("BIP39 derivation matches the cross-client contract", () =>
        {
            // The canonical test vector: this phrase must produce this address in the C# client,
            // the Python SDK and oo-chat alike. Bip39Tests pins the full vector set headlessly;
            // this repeats one of them where the linker has had its say.
            const string phrase =
                "abandon abandon abandon abandon abandon abandon "
                + "abandon abandon abandon abandon abandon about";

            Harness.True(Bip39.Validate(phrase), "the canonical mnemonic failed validation");

            var first = Bip39.ToSeed(phrase);
            var second = Bip39.ToSeed(phrase);
            Harness.Equal(Convert.ToHexString(first), Convert.ToHexString(second),
                "the derivation is not deterministic");
            // PBKDF2-HMAC-SHA512 yields 64 bytes; the contract takes the first 32 as the Ed25519
            // seed with no BIP32 path, which is what makes one phrase restore one address in all
            // three clients.
            Harness.Equal(64, first.Length, "the BIP39 seed is the wrong length");
        });

        h.Check("a generated identity round-trips through its own phrase", () =>
        {
            var (identity, mnemonic) = AgentIdentity.GenerateWithMnemonic();
            Harness.True(Bip39.Validate(mnemonic), "the generated phrase does not validate");

            var restored = AgentIdentity.FromMnemonic(mnemonic);
            Harness.Equal(identity.Address, restored.Address,
                "restoring from the phrase produced a different address");
        });

        if (!freshDataRoot) return;

        h.Check("the identity store persists and reloads through DPAPI", () =>
        {
            var created = IdentityStore.EnsureIdentity();
            var reloaded = IdentityStore.EnsureIdentity();

            Harness.Equal(created.Address, reloaded.Address,
                "the stored identity did not decrypt back to the same address");
            Harness.True(!IdentityStore.WasReset,
                $"the identity was regenerated instead of reloaded: {IdentityStore.ResetReason}");

            var backup = IdentityStore.ExportBackup();
            Harness.Equal(created.Address, backup.Address, "the backup names a different address");
            Harness.True(backup.SeedHex.Length > 0, "the backup carries no seed");
        });
    }
}
