using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ConnectOnion.Protocol;

// JS <-> C# signing conformance gate (Step 9 prerequisite).
//
// Signs an identical payload with the same seed in the Node reference
// (mirroring address.ts) and in the C# port, then asserts the address,
// canonical JSON, and Ed25519 signature all match byte-for-byte. If this
// fails, every signed CONNECT/ONBOARD would be silently rejected by the agent,
// so this must pass before any transport code is trusted.

// A fixed, published test vector — deliberately NOT a random seed. Both sides must derive the
// same address from the same input for the comparison to mean anything, and a hardcoded seed
// also makes a regression reproducible from the failure output alone. It is a test constant
// with no value attached; never reuse it as a real identity.
const string seedHex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

var seed = Convert.FromHexString(seedHex);
var identity = AgentIdentity.FromSeed(seed);

// Same payload as ref-sign.js (order irrelevant — both sides sort keys). Every field here is
// pulling its weight as a test case, so don't "tidy" them:
//   - the keys are listed out of alphabetical order, which is what proves the sorting;
//   - "abc+DEF/123" carries '+' and '/', exactly the characters System.Text.Json's default
//     encoder escapes and JS's JSON.stringify does not — this is the case that catches a
//     regression in the UnsafeRelaxedJsonEscaping setting;
//   - the two longs exercise number formatting, where a float-ish rendering would diverge.
var payload = new List<KeyValuePair<string, object?>>
{
    new("to", identity.Address),
    new("timestamp", 1700000000L),
    new("invite_code", "abc+DEF/123"),
    new("payment", 5L),
};

var canonical = CanonicalJson.Serialize(payload);
var signature = identity.Sign(canonical);

var reference = RunNodeReference(seedHex);

Console.WriteLine("field       | C#");
Console.WriteLine("------------+--------------------------------------------------");
Console.WriteLine($"address     | {identity.Address}");
Console.WriteLine($"  node      | {reference.Address}");
Console.WriteLine($"canonical   | {canonical}");
Console.WriteLine($"  node      | {reference.Canonical}");
Console.WriteLine($"signature   | {signature[..32]}…");
Console.WriteLine($"  node      | {reference.Signature[..32]}…");
Console.WriteLine();

// `&=` rather than `&&=`: every check runs and reports even after one fails, so a single run
// shows the full picture instead of stopping at the first divergence.
var ok = true;
ok &= Check("address", identity.Address, reference.Address);
ok &= Check("canonical", canonical, reference.Canonical);
ok &= Check("signature", signature, reference.Signature);
// Round-trip: the C# verifier must accept the Node signature.
var verified = AgentIdentity.Verify(reference.Address, reference.Canonical, reference.Signature);
ok &= Report("verify(node sig)", verified);

Console.WriteLine();
Console.WriteLine(ok ? "CONFORMANCE PASS" : "CONFORMANCE FAIL");
return ok ? 0 : 1;

static bool Check(string name, string csharp, string node)
    => Report(name, string.Equals(csharp, node, StringComparison.Ordinal));

static bool Report(string name, bool pass)
{
    Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {name}");
    return pass;
}

static Reference RunNodeReference(string seedHex)
{
    var scriptPath = Path.Combine(AppContext.BaseDirectory, "ref-sign.js");
    var psi = new ProcessStartInfo("node", $"\"{scriptPath}\" {seedHex}")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var process = Process.Start(psi)
        ?? throw new InvalidOperationException("Could not start node. Is Node.js on PATH?");
    // Both streams are drained before WaitForExit. Waiting first would deadlock as soon as the
    // reference script wrote more than a pipe buffer's worth: node blocks on the full pipe,
    // we block on node.
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"node reference failed: {stderr}");

    using var doc = JsonDocument.Parse(stdout);
    var root = doc.RootElement;
    return new Reference(
        root.GetProperty("address").GetString()!,
        root.GetProperty("canonical").GetString()!,
        root.GetProperty("signature").GetString()!);
}

internal readonly record struct Reference(string Address, string Canonical, string Signature);
