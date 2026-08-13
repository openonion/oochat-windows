# Trimming

**Production and CI publish trimmed as of 2026-08-05.** This document is the gate that decided
that, and it stays the gate: it is the record of what was proved, what was not, and how to
re-derive both. Read it before changing `PublishTrimmed` in either direction.

Related: `ConnectOnion.WinUIClient.csproj` (where the setting lives), `scripts/Invoke-TrimAudit.ps1`
(linker evidence: warnings, size, headless round trip), `scripts/Test-TrimmedRuntime.ps1`
(real-window evidence: the trimmed app rendering a restored turn, and the shell suite),
`tests/ConnectOnion.TrimSmoke/` (the headless runtime proof),
`tests/ConnectOnion.WinUIClient.UITests/TrimmedRuntimeTests.cs` (the rendering proof),
`tests/ConnectOnion.WinUIClient.UnitTests/Architecture/TrimmingGateTests.cs` (the guard).

**Trimming was enabled for Release on 2026-08-05.** The criteria below are the evidence it was
enabled on, and they now describe a shipped configuration rather than a proposed one.

### Why the signed-MSIX criterion was rescoped rather than waited on

The original gate required "signed MSIX functional matrix passes with trimming enabled". That
criterion was unsatisfiable by construction, and not because of trimming: **this repo does not build
an MSIX.** `.github/workflows/release.yml` ships a self-contained portable ZIP with
`AppxPackageSigningEnabled=false`, and the MSIX path is dormant (`docs/RELEASE.md`). So the gate was
holding a validated configuration back pending validation of a distribution channel that does not
exist — and, since restoring that channel needs a code-signing certificate, pending a purchase.

Signing and trimming are independent. Signing attests *who published* a binary; trimming changes
*what is in* it. The only real coupling was that this document had listed one as a precondition of
the other.

The criterion was therefore rescoped to the channel that actually ships, which is the honest form of
the same question — *is the artifact users download correct when trimmed?* — and which could be, and
was, answered:

- the latest nested-layout portable release rehearsal audits clean (466 entries, no forbidden
  content) and is **73.22 MB compressed / 176.85 MB unpacked**; the nested trimmed app itself
  remains the 170.85 MB like-for-like configuration measured below;
- the root launcher **from that ZIP** starts the nested trimmed executable, which passes the rendering check and required shell suite;
- and its 10-sample warm start is 606.0 ms with a 153.9 MB idle working set, both better than the
  same-machine untrimmed control.

**If MSIX publishing is ever restored, the matrix must be run against a trimmed package before that
channel ships.** It is listed below as deferred, not as met — nothing here has tested it.

## Why this is a gate and not a flag

`PublishTrimmed=true` does two separable things, and only the first is the one people expect.

1. **The linker removes unreferenced IL.** If it removes something reached only by reflection, you
   get a `MissingMethodException` or a silently empty object at runtime. This is the familiar risk,
   and `IL2026`/`IL3050` warnings are the linker telling you where it might happen.

2. **The SDK sets `JsonSerializerIsReflectionEnabledByDefault=false`.** This is the one that bites.
   Every reflection-based `JsonSerializer.Serialize`/`Deserialize` call then throws
   `InvalidOperationException: Reflection-based serialization has been disabled for this
   application` — **on the first call, unconditionally, whether or not the linker touched
   anything.** Note the exception type: `IL2026`'s wording ("can break functionality when
   trimming") suggests a `NotSupportedException` and a maybe. It is neither.

The 2026-07-25 release audit met (2) as a Tool Activity timeline that restored empty in Release and
worked perfectly in Debug. That is the shape of every bug in this class: invisible in development,
invisible in unit tests, invisible in the build log once the warnings are triaged away, and visible
only to a user reopening an old conversation.

**So a clean warning inventory does not mean trimming is safe.** It means the linker has no more
questions. The runtime still does.

## Current status

| Acceptance criterion | Status |
|---|---|
| No unexplained app-owned `IL2026`/`IL3050` in the experimental trimmed publish | **Met** — 0 app-owned sites (was 16) |
| Protocol and interactive serialization paths have direct unit or integration coverage | **Met** — see "Coverage" below |
| Tool Activity and every interactive card survive persist/restart in the trimmed publish | **Met** — `scripts/Test-TrimmedRuntime.ps1`, 2026-08-05. The trimmed smoke harness seeds a data root and the **trimmed app** is launched against it and asked to render the turn; both sides are trimmed binaries. See "Real-window evidence" below |
| Fake Agent and real-window smoke tests pass against the trimmed executable | **Met** — the original then-current 30 required shell tests passed against the trimmed executable, matching the untrimmed control; the 2026-08-09 refresh expands that evidence to 36/36. See the note on `HelpKeyboardShortcuts` below for the earlier 12-test suite issue that had to be fixed first |
| The **shipped** channel — the self-contained portable ZIP — audits clean and runs trimmed | **Met** — `scripts/Test-PackageContents.ps1` passes on the nested-layout ZIP (466 entries, no forbidden content, 73.22 MB compressed in the latest rehearsal), and the root launcher starts the nested trimmed executable which passes the rendering check and required shell suite |
| Signed MSIX functional matrix passes with trimming enabled | **Deferred, not met** — no MSIX is built, so there is nothing to run it against (`docs/RELEASE.md`). Replaced above by the channel that does ship. Run it against a trimmed package *before* MSIX publishing is restored |
| No key operation regresses >10% against the same-machine untrimmed baseline | **Met** — worst delta **+2.9%** (managed heap); every other metric improved. Table below |
| Package-size savings and compatibility risks documented | **Met** — this document |
| The setting stays explicit and commented in both configurations, and CI publishes what production ships | **Held** — enforced by `TrimmingGateTests`, which now pins Release trimmed, Debug untrimmed, and both workflows' publishes trimmed |

### Windows notification native resource

Windows App SDK 2.3.1's self-contained component folders omit
`Microsoft.WindowsAppRuntime.Insights.Resource.dll`, although the same Microsoft-supplied file is
present in the Runtime NuGet package's framework MSIX. An unpackaged app without that file reports
`AppNotificationManager.IsSupported() == true`, then fails `Register()` with `0x8007007E` and the
message `Unable to load resource dll`.

`ConnectOnion.WinUIClient.csproj` extracts the file from the current Runtime package during every
Windows App SDK self-contained build and copies it beside the executable. The 2026-08-09 Release,
trimmed, ReadyToRun verification changed startup from that exception to
`RegisterAndListen: registered for notification activation`. `TrimmingGateTests` pins the build
rule, and `scripts/Test-PackageContents.ps1` rejects a portable archive that omits the DLL. Remove
the workaround only after a later Windows App SDK supplies the resource through its normal
self-contained payload and the trimmed notification registration check still passes.

## What was actually wrong, and what changed

The audit's "clean publish" result did not reproduce on `main`: an experimental trimmed publish
emitted **16 app-owned `IL2026` sites**, every one of them a reflection-based `JsonSerializer` call
on a path that runs in normal use.

| Site | What it serialized | Now |
|---|---|---|
| `AgentConnectionService.cs:984` | **every outgoing WebSocket frame** | `WireJson.Serialize` |
| `AgentConnectionService.cs:809` | `session_sync` compaction | `WireJson.Serialize` |
| `InteractiveResponseBuilder.cs:31` | the `ask_user` field-form answer | `WireJson.SerializeStringMap` |
| `ToolActivityProjector.cs:379` | redacted tool arguments, on every persisted step | direct `Utf8JsonWriter` recursion |
| `AgentTurnExecutor.cs` ×7 | re-serialized interactive frames; `JsonElement` parsing | `WireJson.Serialize`, `JsonDocument.Parse` |
| `AgentSessionManager.cs` ×4 | reconnect/reconnected progress events | `WireJson.Serialize` |
| `AgentInfoService.cs:153` | the relay-composed `/info` blob | `EndpointResolver.SerializeAgentInfo` |
| `AppStorage.cs:80,101` | a generic file round-trip with **no callers** | deleted |

Two of these are worth calling out beyond the trim fix:

- **The outgoing-frame serializer covers the entire protocol.** `SendJsonAsync` is the single exit
  for `CONNECT`, `INPUT`, `INTERRUPT`, `mode_change`, `SESSION_STATUS`, `PONG`, `ONBOARD_SUBMIT`
  and all three interactive answers. Under trimming the app would have failed to send anything at
  all — it would not have connected, so nothing downstream could have been tested.

- **`AgentInfoService` had a latent bug the trim fix exposed.** It built the relay's `/info` blob
  from an anonymous type with no naming policy, so it emitted `{"Name":…,"Description":…}` for a
  skill and `{"Text":…,"Files":{"MaxFileSizeMb":…}}` for capabilities — while
  `NormalizeSkills`/`ParseAcceptedInputs` read `name`, `description`, `text` and
  `max_file_size_mb`. Every relay-composed blob read back with no skills and no declared
  capabilities, in *untrimmed* builds too. The writer now lives in `EndpointResolver` beside those
  parsers so the halves cannot drift apart again, and `AgentInfoSerializationTests` pins the round
  trip.

### The two sanctioned mechanisms

There is no third way, and that is the point:

- **Ours** — a blob we define and read back out of our own database: a source-generated context
  (`AppJsonContext`, `ConversationJsonContext`).
- **Theirs** — a shape the host defines, which is deliberately untyped on our side: `WireJson`
  (insertion-ordered, default escaping, byte-identical to what the reflection serializer produced)
  or a hand-written `Utf8JsonWriter`. `CanonicalJson` is the pre-existing example.

`WireJson.WriteValue` throws on an unrecognised value type rather than guessing. A builder that
starts putting something new on the wire fails in `WireJsonTests`, which is the moment to notice.

## Coverage

- `WireJsonTests` — 30+ cases, most asserting **byte equality against `JsonSerializer.Serialize` as
  an oracle**. The requirement is not "emits reasonable JSON", it is "emits the bytes the agent
  already receives", so the implementation being replaced is the right thing to diff against.
  Reflection still works in a test host, which is what makes the oracle available here and
  unavailable in the shipping build — that asymmetry is why the smoke harness below also exists.
- `AgentInfoSerializationTests` — `/info` round trip through its own parsers.
- `SanitizeJsonEncodingTests` — the redaction recursion, including nesting, arrays and the
  non-JSON fallback.
- `tests/ConnectOnion.TrimSmoke` — the runtime proof; see below.

## The smoke harness

`tests/ConnectOnion.TrimSmoke` is a plain console app, not an xunit project: a trimmed
self-contained publish has no VSTest host to load into, and the whole question is what happens
inside that binary.

It sets `JsonSerializerIsReflectionEnabledByDefault=false` **unconditionally**, so an ordinary
`dotnet run` already reproduces the runtime failure mode — the fast loop catches a reintroduced
reflection call without waiting for a publish. Publishing it trimmed adds the linker's half.

Its first check is a **negative control**: it calls the reflection serializer and requires it to
throw. Every other check passes trivially if the fallback is on, so without this a green run would
mean nothing.

`persist` and `verify` are separate phases so the audit script can run them as **two processes**
against one data root. Restarting for real is the only way to distinguish a row that survived from
an in-memory object that was handed back — which is exactly what the original bug turned on.

```powershell
# Fast loop (untrimmed, reflection fallback already off)
dotnet run --project tests\ConnectOnion.TrimSmoke\ConnectOnion.TrimSmoke.csproj -- all

# The real thing, plus both app publishes and the warning inventory
pwsh scripts\Invoke-TrimAudit.ps1

# Just the harness, trimmed
pwsh scripts\Invoke-TrimAudit.ps1 -SmokeOnly
```

**What it covers:** outgoing frame serialization for every frame shape, inbound `ask_user` /
`ONBOARD_REQUIRED` / `agent_image` / `files_received` parsing, the `/info` round trip, the
interactive response builders, tool-argument redaction, BIP39 derivation and DPAPI identity
storage, and — across a restart — the Tool Activity timeline with its steps, arguments, results
and derived invocations, the readable interactive cards with their resolved answers, attachment
metadata, and the preferences/shortcut-override map.

**What it does not cover, and this is the gap in criterion 3:** it links `Core` and `Protocol`
only. The WinUI app binary additionally trims XAML type information, `CommunityToolkit`,
`FluentIcons`, `H.NotifyIcon`, `Markdig`, `QRCoder`, `Win2D`, Serilog and the Windows App SDK.
Nothing here says anything about those.

## Warning inventory

Classification rule, as implemented in `Invoke-TrimAudit.ps1`:

- **App-owned** — the line begins with a repo source path and position:
  `D:\…\AgentConnectionService.cs(984,20): warning IL2026: …`. These block the gate.
- **Third-party** — the line begins with `ILLink :` and names generated code with no source
  position. These do not block the gate.

The trailing `[…\Some.csproj]` tag appears on *both* and cannot be used to tell them apart; the
leading token is what distinguishes them.

**Remaining third-party warnings: none.** The 35 `IL2081` warnings from CsWinRT's generated ABI
marshalling (`ABI.Windows.Foundation.*`, `ABI.System.Collections.Generic.KeyValuePair`) came from
the .NET SDK's `Microsoft.Windows.SDK.NET.Ref 10.0.19041.57`, which carried C#/WinRT 2.2. The repo
now pins `10.0.19041.87`, carrying C#/WinRT 2.3.1, and the same fully expanded trim analysis emits
zero warnings. The newer analyzer also exposed one app-owned `CsWinRT1032` on a collection
expression targeting `IReadOnlyList`; giving it an explicit array target fixed that warning.

No warnings are suppressed. A future `IL2081` that starts naming *our* types would be a real
finding, and blanket suppression would hide it.

Serilog 4.3.0 previously contributed one `IL2067` from
`Serilog.Capturing.PropertyValueConverter`. The core package is transitive here, so that version's
`build`-only trimming target was not imported. `Directory.Packages.props` now pins Serilog 4.4.0,
whose `buildTransitive` target reaches this project; the warning is absent from the Release trim
audit.

## Size

Measured 2026-07-27, SDK 10.0.301, `main` at `c2ddeb5`:

| Configuration | Size |
|---|---|
| Untrimmed + ReadyToRun (historical pre-trimming shipping shape) | 247.59 MB |
| Trimmed, no ReadyToRun | 89.35 MB |
| Difference | 158.24 MB (63.9%) |

Re-derive with `Invoke-TrimAudit.ps1`, which writes `artifacts/trim-audit/trim-audit.json`.

**These two are not directly comparable, and the difference is not the trimming saving.**
ReadyToRun precompiles IL to native code and roughly doubles assembly size; the shipping build has
it on and the audit's trimmed measurement has it off, because R2R makes the publish much slower and
changes nothing about the warning inventory that audit exists to produce. So the 63.9% figure is an
upper bound, not a saving.

### The like-for-like number (measured 2026-08-05)

Both publishes ReadyToRun, both self-contained (including the Windows App SDK runtime), same
machine, latest release rehearsal:

| Publish | Size |
|---|---|
| Untrimmed + R2R control | 310.28 MB |
| **Trimmed + R2R (shipping configuration)** | **170.85 MB** |
| Difference | **139.43 MB (44.9%)** |

Reproduce the trimmed side with the same publish the runtime script uses:

```powershell
dotnet publish ConnectOnion.WinUIClient/ConnectOnion.WinUIClient.csproj -c Release -r win-x64 `
  -p:Platform=x64 -p:RunUnpackaged=true -p:SelfContained=true `
  -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=true -p:PublishReadyToRun=true `
  -p:AppxPackageSigningEnabled=false -o artifacts/trim-audit/trimmed-r2r
```

### Runtime cost (measured 2026-08-05)

`scripts/Measure-Performance.ps1 -Exe <publish>`, WarmUnpackaged, 10 iterations each, same machine,
untrimmed+R2R vs trimmed+R2R:

| Metric | Untrimmed + R2R | Trimmed + R2R | Delta |
|---|---:|---:|---:|
| Warm start to first frame | 624.7 ms | 606.0 ms | −3.0% |
| Idle working set | 173.2 MB | 153.9 MB | −11.1% |
| Idle private bytes | 113.6 MB | 104.3 MB | −8.2% |
| Managed heap at first frame | 3.5 MB | 3.6 MB | +2.9% |
| Graceful shutdown | 121.0 ms | 118.5 ms | −2.1% |

Worst regression **+2.9%**, against a >10% budget. Nothing here argues against trimming: startup is
marginally faster and idle working set — the metric this document says is the real argument — is
19.3 MB lower.

**Do not read the audit's trimmed publish as a startup measurement.** It sets
`PublishReadyToRun=false`, and a cold first launch of that build logged **11.8 s** to first frame
because every method JITs. That number says nothing about trimming; it is the cost of dropping R2R,
which is why the like-for-like comparison above uses R2R on both sides.

### Real-window evidence

`scripts/Test-TrimmedRuntime.ps1` produces the two criteria a headless harness cannot reach. It
publishes trimmed+R2R, seeds a data root by running the **trimmed** smoke harness's `persist`, then
launches the **trimmed app** against that root — so neither side of the round trip is untrimmed.

- **Rendering (`TrimmedRuntimeTests`)**: the trimmed app restores the seeded turn and renders the
  tool-activity card *with its steps, arguments and results*, plus the `ask_user` and `plan_review`
  cards with their stamped answers, and correctly omits the settled approval. This is the assertion
  the original regression would have failed: the row was always readable, the card came back empty.
- **Shell suite**: all 30 tests in the original gate passed against the trimmed executable, matching the
  untrimmed control; the Explorer-based drag diagnostic remains explicitly skipped.

  Getting there required fixing `HelpKeyboardShortcuts_OpensFocusedAndClosesWithEscape`, which
  failed on both builds in a full-suite run while passing 3/3 in isolation. It was the suite's only
  interaction that needs a **physical** click — `MenuBarItem` exposes ExpandCollapse rather than
  Invoke, so a menu flyout cannot be opened through the UIA pattern route everything else uses — and
  a physical click goes to whatever owns the foreground. The previous test's app is killed in
  `Dispose`, and until that process finishes exiting it can still hold the foreground, so the click
  landed on a dying window. `OpenHelpMenuItem` now brings the window forward first and retries the
  open, because the click is a toggle and a stray earlier one leaves the menu in the opposite state.

  The lesson generalises: **always run the untrimmed control before attributing a failure here to
  trimming.** This one looked like a trimming difference for as long as nobody checked.

One caution learned while writing that test: the transcript is a virtualized list restored
asynchronously, so walking the automation tree the instant `MessageList` appears reads a partly
realized view and reports cards as missing that simply had not been built yet. That produced a
convincing false "trimming defect" against the untrimmed build. The test waits for a sentinel
before asserting; keep it that way.

## Compatibility risks if trimming is enabled

- **Reflection-based JSON anywhere new.** The mechanism is now closed off in the code that exists,
  but nothing stops the next `JsonSerializer.Serialize(someObject)`. The smoke harness catches it
  only on the paths it exercises.
- **XAML type resolution.** `x:Bind` is compiled and safe; `Binding`, `DataTemplateSelector`
  targets, `{ThemeResource}` lookups by string, and `XamlTypeInfo.g.cs` reflection metadata are
  the classic trimming casualties in WinUI, and none of them are exercised by the harness.
- **Third-party packages.** None of the eight UI packages declare `IsTrimmable`, so the linker
  keeps them whole today; that is why the trimmed size is 90 MB and not far smaller. If a future
  version starts declaring it, the risk profile changes without any edit here.
- **`Microsoft.Data.Sqlite` + `SQLitePCLRaw.provider.winsqlite3`** resolve a native provider
  through a static initializer that trimming has historically pruned. The harness covers this,
  which is the one third-party risk it does retire.
- **Serilog sinks** are resolved by configuration, and configuration-driven type resolution is
  reflection by another name.

## Re-deriving this decision, or reversing it

Trimming is on. This is the procedure that was followed to turn it on, and it is also what to run
before turning it off, changing the publish shape, or restoring MSIX.

1. `pwsh scripts\Invoke-TrimAudit.ps1` — must report 0 app-owned warning sites and a green
   persist/verify pair. This is the linker's opinion and the headless round trip.
2. `pwsh scripts\Test-TrimmedRuntime.ps1` — publishes trimmed+R2R, seeds a data root with the
   trimmed smoke harness, then launches the **trimmed app** against it. Covers the rendering
   criterion and the current 36-test required shell/chat suite in one run.
3. `pwsh scripts\Measure-Performance.ps1 -Exe <trimmed>` against a same-machine untrimmed
   baseline; no key operation may regress more than 10%. **Compare R2R against R2R** — the audit's
   trimmed publish has ReadyToRun off and is not a valid baseline for this.
4. Publish and zip exactly as `release.yml` does, then `pwsh scripts\Test-PackageContents.ps1
   -PackagePath <zip>`. This is the artifact users actually download; validating a lab build
   instead is how a packaging-only defect ships.
5. Exercise the trimmed app by hand against a live agent: a tool-using turn, an approval, an
   `ask_user` form, a plan review, a mode change, an image attachment, a reconnect, then **restart
   and reopen the conversation** — the restart is the step that would have caught the original bug.
   Steps 1–4 are automated; this one is not, and it is the only one that exercises a real agent.
6. If MSIX is ever restored: build a **trimmed** package and run `scripts/Test-ReleaseUpgrade.ps1`
   against it before that channel ships. Nothing in this document has tested MSIX.
7. Any change to `PublishTrimmed` means updating `TrimmingGateTests` in the same commit — the guard
   pins Release trimmed, Debug untrimmed, and both workflows' publishes trimmed, so moving it is
   part of taking the decision rather than an obstacle to route around. Re-ratify the size baseline
   in `scripts/Test-PackageContents.ps1` and `docs/RELEASE.md` too: it is an upper bound, and one
   left at the wrong configuration's number stops catching anything.
