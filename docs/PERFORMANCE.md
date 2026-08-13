# Performance benchmark

How launch time and memory are measured, what the budgets are, and how to reproduce a run.
This closes the `docs/OPTIMIZATION.md` backlog item asking for measured budgets for cold start
and steady-state memory.

## What is measured, and why those metrics

The metrics follow the Windows app performance guidance: **time to first frame** for launch and
**working set / private bytes** for memory, taken from a Release build of the real shipping
binary.

| Metric | Where it comes from |
|---|---|
| Cold / warm start | Process creation → first composited frame, measured in-app |
| Launch phase breakdown | `managedEntry`, `hostStarted`, `windowCreated`, `windowActivated`, `firstFrame` |
| Readiness breakdown | `firstInteractive`, `sessionListLoaded`, `shellInitialized`, optional `firstConversationRendered` |
| Idle working set / private bytes | Sampled externally, 15 s after the required readiness milestones complete |
| Managed heap | `GC.GetTotalMemory` at first frame (only observable in-process) |
| Handles / threads | Sampled with the memory, as a leak tripwire |
| Graceful shutdown | Named-event exit request → process exit |

The in-process memory figures are captured **once, at first frame**, and reused by the later
report rewrites the readiness milestones trigger. The budget and the baseline below are written
against the first-frame figure, so re-sampling per milestone would redefine the metric while
keeping its name.

**Launch time is measured from `Process.StartTime`, not from a stopwatch started in managed
code.** On a cold start most of the wall clock is CLR + Windows App SDK bootstrap that runs
*before* the `App` constructor; a stopwatch started there reports a number no user experiences.
The gap before `managedEntry` in the report is exactly that bootstrap cost.

## Budgets

Ratified against the 2026-07-20 baseline below. `Target` is what we hold; `Fail` is the line
that makes a build unacceptable — the launch thresholds sit under the Store certification
requirement that an app be responsive within 5 s.

| Metric | Target | Fail |
|---|---:|---:|
| Cold start to first frame | 2000 ms | 5000 ms |
| Warm start to first frame | 1200 ms | 2500 ms |
| Idle working set | 200 MB | 350 MB |
| Idle private bytes | 180 MB | 320 MB |
| Managed heap at first frame | 40 MB | 80 MB |
| Graceful shutdown | 1500 ms | 4000 ms |

Budgets live in the `$Budgets` table at the top of `scripts/Measure-Performance.ps1`. Re-ratify
them when a run establishes a new baseline; don't quietly loosen one to make a run pass.

## Running it

```powershell
# Publish the same self-contained, trimmed, ReadyToRun unpackaged shape used for shipping.
dotnet publish ConnectOnion.WinUIClient\ConnectOnion.WinUIClient.csproj `
  --configuration Release --runtime win-x64 -p:Platform=x64 `
  -p:RunUnpackaged=true -p:SelfContained=true -p:WindowsAppSDKSelfContained=true `
  -p:AppxPackageSigningEnabled=false

$perfExe = Resolve-Path 'ConnectOnion.WinUIClient\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\ConnectOnion.WinUIClient.exe'

powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Measure-Performance.ps1 `
  -Exe $perfExe -Mode WarmUnpackaged -Iterations 5
```

Always pass `-Exe` when producing comparable evidence. The script's fallback can find a local
framework-dependent build, but that binary may require an installed Windows App SDK runtime and
does not represent the self-contained portable release.

Useful switches:

- `-Mode WarmUnpackaged|ColdUnpackaged|WarmMsix|ColdMsix` selects one independently
  reproducible launch mode. Never combine unlike modes in one report. Warm modes perform one
  unmeasured launch first; cold modes purge before every measured launch.
- `-Iterations 10` records more samples. Five is the enforced minimum.
- `-SettleSeconds` (default 15) is the idle period between the readiness milestones and the
  memory sample. Reaching the milestones means the shell is usable, not that startup work has
  drained; changing this makes a report incomparable to the baseline below.
- Cold modes purge the standby list before **every** sample and require an elevated shell. A
  failed purge fails the run; the sample is never labelled cold.
- MSIX modes resolve the installed package separately from the unpackaged build. Use
  `-PackageName` and `-PackageExecutable` if its identity differs from the defaults.
- `-DatasetId` records the fixture/profile identity. Add `-RequireConversation` when that fixture
  opens a conversation; the run then requires `firstConversationRendered`. Use `-FixturePath`
  to copy the same prepared data root into the isolated profile before every sample. In that
  benchmark-only mode the app opens the fixture's active conversation instead of Home.
- `-EnforceBudgets` — exit non-zero when a median breaches its `Fail` threshold. Off by default.
- `-UseRealDataRoot` — measure against `%AppData%\ConnectOnion` instead of a throwaway data root.
- `-AllowDebugBuild` — required to benchmark a Debug build, whose numbers are not budgets.

Each run writes `TestResults\perf\<timestamp>\` containing `report.md`, `results.json`, and the
per-iteration `startup-N.json`.

Two things the harness has to get right, both of which cost a wrong measurement first:

- **The app is single-instance.** A second launch redirects to the running one and exits
  immediately, which records as an absurdly fast startup — so every iteration kills leftovers
  first.
- **`WM_CLOSE` is not the exit path.** The benchmark creates a unique named Windows event before
  launch. `StartupTelemetry` arms it only for benchmark runs and dispatches it to
  `MainWindow.ExitApplication`, the same graceful path as File > Exit. It does not depend on tray
  window names or fixed command IDs. Setting the event proves nothing about whether the app
  opened it, so the `WaitForExit` that follows — not the `Set()` — is what decides a sample is a
  graceful shutdown; a failure to arm shows up as `Performance exit event ... could not be
  armed` in the app's own log.
- **The report is rewritten as each readiness milestone lands**, and each rewrite is a
  `File.Move`. Every read of it therefore has to be retried: the destination path becomes visible
  a moment before the move releases its handle, so a single read can hit a sharing violation on a
  file whose contents are complete.
- **`firstConversationRendered` is absent, not zero, when nothing rendered** — an empty
  conversation or a restore that threw records no mark, so `-RequireConversation` fails loudly
  instead of timing work that never happened.
- **A readiness mark is not necessarily after first frame.** `sessionListLoaded` and
  `shellInitialized` routinely land *before* `firstFrame`: that chain runs on the UI thread from
  `RootGrid.Loaded` and finishes before the compositor's first pass. On the default Home route
  `firstInteractive` is the only milestone that extends past the visible launch. Reports sort
  chronologically, so this shows up rather than being hidden.

## The `-RequireConversation` fixture contract

`-RequireConversation` sets `CONNECTONION_PERF_OPEN_CONVERSATION=1`, and `MainWindow` then
navigates to `ChatPage` instead of `HomePage` at startup. Two consequences worth stating plainly,
because neither is visible from the switch:

- **The measured startup path is not the shipped one.** The shipped app opens Home; this opens a
  conversation. Numbers from a `-RequireConversation` run answer "how fast does a conversation
  come back", not "how fast does the app start", and must not be compared with a run without it.
- **Which conversation is the fixture's business.** `ChatPage` is navigated with no parameter, so
  it restores whatever session the fixture's database has selected. `-FixturePath` must therefore
  point at a data root whose selected session has messages in it — an empty or unselected one
  records no `firstConversationRendered` and the run fails on the readiness timeout.

## Cold-cache qualification

Warm modes make no cache claim. Cold modes require an elevated process and a successful standby
list purge before every one of at least five samples. Failure is terminal and
`ColdCacheQualification` is written to `results.json`; there is no "cold-ish" fallback. Build
commit, dataset identity, power source, and power scheme are recorded because runs with different
values are not directly comparable.

## WPR / WPA trace

Run from an elevated PowerShell prompt on a machine where Windows Performance Recorder policy is
enabled:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Capture-StartupTrace.ps1 `
  -Mode ColdUnpackaged `
  -DatasetId empty-isolated-profile
```

The wrapper starts the built-in `GeneralProfile`, `DotNET`, `XAMLActivity`,
`XAMLAppResponsiveness`, and `DesktopComposition` profiles, runs five valid samples, then saves
`startup.etl` beside `results.json` and `report.md`. In WPA inspect Process Lifetimes, CPU Usage
(Sampled), File I/O (including `winsqlite3.dll` and `connectonion.db`), .NET GC/allocations, XAML
layout/responsiveness, and frame/compositor activity.

If elevation, profile availability, or machine policy blocks capture—including
`0xc5585011`—the wrapper exits non-zero, cancels WPR, and does not claim a trace. The manually
dispatched `release-performance.yml` workflow targets a prepared, elevated, real-desktop
self-hosted runner and retains aggregate JSON, Markdown, raw samples, and ETL for 30 days.

## Current shipping baseline — 2026-08-05

Release x64 unpackaged, self-contained (including Windows App SDK), trimmed + ReadyToRun, Windows
11 Pro 10.0.26200, 32 cores, 31.6 GB RAM, 10 warm iterations, memory sampled 15 s after all
required readiness milestones. This is the current portable release shape.

| Metric | Median | Min | Max | P95 | Budget status |
|---|---:|---:|---:|---:|---|
| Warm start to first frame | 606.0 ms | 584.1 ms | 618.2 ms | 618.2 ms | PASS |
| First interactive | 632.0 ms | 609.9 ms | 644.5 ms | 644.5 ms | informational |
| Idle working set | 153.9 MB | 153.3 MB | 154.2 MB | 154.2 MB | PASS |
| Idle private bytes | 104.3 MB | 103.2 MB | 105.5 MB | 105.5 MB | PASS |
| Managed heap at first frame | 3.6 MB | 3.6 MB | 3.6 MB | 3.6 MB | PASS |
| Graceful shutdown | 118.5 ms | 109 ms | 134 ms | 134 ms | PASS |

The same-machine untrimmed + ReadyToRun control measured 624.7 ms warm start and 173.2 MB idle
working set. Trimming therefore did not impose a runtime regression in this run; the complete
like-for-like comparison and compatibility evidence live in [TRIMMING.md](./TRIMMING.md).

This run makes no cold-cache claim. A current cold baseline still requires an elevated,
policy-enabled run whose standby-list purge succeeds before every sample.

## Historical baseline — 2026-07-20

Release x64 unpackaged, Windows 11 Pro 10.0.26200, 32 cores, 31.6 GB RAM, 5 iterations, memory
sampled 15 s after first frame, standby list not purged.

> **The cold-start figure below is not a cold start.** The purge it relied on never ran: the old
> `Clear-StandbyList` discarded `NtSetSystemInformation`'s return value, so the call failed with
> `STATUS_PRIVILEGE_NOT_HELD` on every machine — being in the Administrators group holds
> `SeProfileSingleProcessPrivilege` but leaves it disabled — and the failure was invisible. Every
> historical "cold" number in this file is a warm launch. A qualified cold run of the same binary
> measures **~1220 ms**, ~44% above the 850 ms recorded here. This section needs re-measuring on
> a quiet machine before it is trusted again; it is left in place only so the comparison above is
> reproducible.

| Metric | Median | Min | Max |
|---|---:|---:|---:|
| Cold start (n=1, not actually cold — see above) | 850 ms | — | — |
| Warm start (n=4) | 801 ms | 798 ms | 812 ms |
| Idle working set | 190.2 MB | 189.0 MB | 191.1 MB |
| Idle private bytes | 119.2 MB | 116.8 MB | 119.9 MB |
| Managed heap | 3.1 MB | — | — |
| Handles / threads | 1337 / 74 | — | — |
| Graceful shutdown | 153 ms | 150 ms | 158 ms |

Phase breakdown of a representative launch (ms since process start):

| managedEntry | hostStarted | windowCreated | windowActivated | firstFrame |
|---:|---:|---:|---:|---:|
| 109 | 441 (+332) | 698 (+257) | 715 (+17) | 798 (+83) |

Reading of the baseline:

- **The two costs that matter are host start (~330 ms) and window construction (~260 ms)**,
  together ~75% of launch. Managed entry is ~110 ms and activation → first frame ~85 ms; neither
  is worth optimizing before the other two.
- **Working set ~190 MB against a ~3 MB managed heap.** Almost none of the footprint is app
  objects — it is the WinUI 3 / Windows App SDK runtime and its native surfaces. Optimizing C#
  allocations will not move this number; reducing what is loaded at startup might.
- **Shutdown is ~150 ms**, comfortably inside the app's own 3 s + 1 s drain budgets. Note this
  measures an idle app; the ~1 s close recorded in earlier investigation involves in-flight
  presence probes.
- **A first-ever launch of a freshly built binary took 10.8 s** in an earlier trial (cold disk,
  first JIT, first SQLite create). That is not representative of a user's cold start and is not
  what the cold budget is written against, but it is the number to remember when someone reports
  "the first launch after install is slow".

## Optimizations tried and rejected

The two experiments below were measured with the harness and **neither reduced time to first
frame in its original form**. A later structural pass (2026-08-01) replaced eager overlay
construction with working on-demand creation, consolidated startup persistence reads, paged
transcripts, bounded caches, indexed global search, and reduced attachment/WebSocket buffer
retention. That pass is covered by build, regression tests, and the qualified warm report below.

The post-change `WarmUnpackaged` run on 2026-08-01 recorded medians of **912.6 ms** to first
frame, **187.5 MB** idle working set, **119.8 MB** private bytes, **3.6 MB** managed heap, and
**153 ms** graceful shutdown; all configured budgets passed. This is a health gate, not a causal
before/after attribution: it does not isolate any one change, qualify cold cache, or cover a large
conversation fixture.

**Deferring the modal overlays (`x:Load="False"`).** The old `MainWindow.xaml` built three full-window
modals eagerly — settings, keyboard shortcuts, About — and `SettingsOverlay` alone hosts the
entire `SettingsPage` and resolves its view model from the container. Deferring them cut
`MainWindow.InitializeComponent` from 189 ms to 110 ms and idle working set from ~190 MB to
~181 MB, but **time to first frame did not move** (the saving reappeared elsewhere, and total
launch stayed inside run-to-run noise). It also did not work: `FindName` returns null for these
elements from both `RootGrid` and the direct parent panel, so the generated field stayed null and
clicking Settings silently did nothing. Reverted — a ~9 MB working-set win did not justify
replacing declarative XAML with hand-rolled realization, and the latency win was zero.

That specific `x:Load` attempt remains rejected. The current implementation creates and wires
each overlay in `MainWindow.Overlays.cs` on first use, so it does not depend on deferred XAML name
resolution. Its behavior is verified, and the warm report above covers the complete build, but it
does not isolate how much of the result comes from overlay realization alone.

**Starting the Generic Host concurrently with window construction.** `OnLaunched` awaits
`_host.StartAsync()` before building the window, and host start is ~110 ms of SQLite reads plus
OS notification registration that the window does not depend on. Overlapping them changed
nothing measurable, and the phase report showed why: `hostStarted` and `windowCreated` came out
with **identical** timestamps on every iteration, because each hosted service's `StartAsync`
completes synchronously on the calling thread (the SQLite reads return inline), so the returned
task is already complete and there is nothing to overlap. Reverted — it moved `MainWindow` and
`Closed` assignment after an await and let the window exist before its services had loaded, for
no gain. Making it real would require pushing host start onto a background thread, which OS
notification registration is not obviously safe for.

**What the numbers say about where time actually goes.** Warm launch is ~800 ms: ~120 ms CLR +
Windows App SDK bootstrap before any app code runs, ~330 ms of App constructor and host start,
~260 ms of window construction, ~85 ms from activation to first frame. The two big middle
segments are UI-thread-bound and serialized; nothing above shortened them. Memory is ~190 MB
working set against a ~3 MB managed heap, so the footprint is the WinUI 3 runtime, not app
objects — allocation tuning cannot move it.

## Limitations

- Cold modes cannot run without an elevated, successful standby-list purge. Elevation alone is
  not enough: the harness must also *enable* `SeProfileSingleProcessPrivilege`, which an
  administrator token holds but leaves disabled.
- **A purged standby list is not an empty cache.** SysMain re-prefetches between samples, and
  pages resident in another live process's working set were never standby pages to begin with.
  Cold samples therefore trend faster across a run — a measured five-sample sweep went
  1405 → 1267 → 1167 → 1221 → 1071 ms — so iteration 1 is the coldest one and the median is
  the honest summary, not the minimum.
- Steady-state memory is sampled at idle with no conversation loaded. Restoring a large
  conversation, scrolling, and searching are **not** covered — those still want an ETW capture
  (see `docs/OPTIMIZATION.md`) and remain on the backlog as budgets.
- Not run in CI: unlike the bounded weekly FlaUI shell suite, this benchmark performs extended
  sampling and optional standby-list manipulation, so it remains a controlled local/release audit.

## Repeated-navigation memory leak gate

Idle memory and repeated-use retention are different tests. The launch benchmark above samples
one settled process; `scripts/Test-MemoryLeaks.ps1` repeatedly opens and closes every persistent
navigation surface and asserts that Private Bytes, handles, and threads plateau after warm-up.

See [MEMORY_LEAK_INVESTIGATION.md](./MEMORY_LEAK_INVESTIGATION.md) for the July 2026 incident,
the measured before/after curves, why Task Manager does not return to the launch baseline, and the
test's thresholds and limitations.
