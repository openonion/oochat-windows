namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Shape check for a ConnectOnion agent address: <c>0x</c> followed by 64 hex digits, i.e.
/// a hex-encoded 32-byte Ed25519 public key. This is form validation only — it says the
/// string could be an address, never that the agent exists or is reachable. The same 66/0x
/// test gates relay lookup in <c>AgentConnectionService.ResolveConnectionAsync</c>, so a
/// string that fails here would not be resolvable anyway.
/// </summary>
public static class AgentAddressValidator
{
    /// <summary>Shown verbatim under the address field, so it states the rule rather than
    /// just reporting failure.</summary>
    public const string ValidationMessage =
        "Enter a valid agent address (0x + 64 hex characters)";

    public static bool IsValid(string address)
    {
        if (address.Length != 66 || !address.StartsWith("0x", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in address.AsSpan(2))
        {
            if (!IsHexCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    // Hand-rolled rather than a regex or Convert.FromHexString: this runs on every keystroke
    // in the add-agent field, and it must answer "not yet valid" for a half-typed address
    // without allocating or throwing.
    private static bool IsHexCharacter(char character)
        => character is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';
}
