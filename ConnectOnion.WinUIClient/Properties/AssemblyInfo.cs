using System.Runtime.InteropServices;

// Every P/Invoke in this assembly resolves from System32 and nowhere else.
//
// Without this, a `[DllImport("user32.dll")]` follows the default search order, which begins with
// the directory the executable was loaded from. That matters here specifically because this app
// ships as a **portable ZIP** that users extract wherever they like — including Downloads, which
// is exactly the shared, attacker-writable directory a planted user32.dll would sit in. An MSIX
// install would land in a protected location; a portable extract has no such guarantee.
//
// All twelve call sites target user32.dll or Comctl32.dll. Both are Windows-provided and both are
// on the KnownDLLs list, so System32 is not merely the safe answer — it is the only correct one,
// and narrowing to it costs nothing. Declared at assembly scope rather than repeated on each
// declaration so a new P/Invoke inherits it instead of having to remember it; that is also what
// keeps CA5392 satisfied for declarations nobody has written yet.
//
// If a P/Invoke to a library shipped *with* the app is ever added, this attribute must be
// reconsidered rather than deleted — the right answer then is a per-method
// [DefaultDllImportSearchPaths] on that one declaration, not widening the search path for all of
// them.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
