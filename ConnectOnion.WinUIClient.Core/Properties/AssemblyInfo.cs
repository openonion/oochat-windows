using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ConnectOnion.WinUIClient.UnitTests")]
[assembly: InternalsVisibleTo("ConnectOnion.IntegrationTests")]

// The app project, not just the tests. Core and the app share the ConnectOnion.WinUIClient root
// namespace, so a type that moves across the seam looks unchanged at every call site — but
// `internal` silently stops reaching the app the moment it lands here. Granting it keeps "move a
// file into Core to bring it under test" a pure move, instead of a move that also forces every
// internal member it exposes to become public. This is a one-way grant: Core still cannot see the
// app, which is what the ArchUnit layer gate exists to enforce.
[assembly: InternalsVisibleTo("ConnectOnion.WinUIClient")]
