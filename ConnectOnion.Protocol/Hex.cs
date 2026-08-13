using System;

namespace ConnectOnion.Protocol;

/// <summary>
/// Hex encoding for the wire, kept behind one call so the casing can't drift.
/// Every hex string the protocol emits — addresses, public keys, signatures — is
/// lowercase, because the reference signer (<c>ref-sign.js</c>) is and the Conformance
/// project compares the two byte-for-byte. An uppercase digit would be a different
/// string to the agent's signature check even though it decodes to the same bytes.
/// </summary>
public static class Hex
{
    public static string ToLowerString(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(bytes);
}
