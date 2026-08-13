# Engineering optimization notes

Last updated: 2026-08-09

## Implemented

- **Dependency governance:** package versions live in `Directory.Packages.props`; restore enables transitive pinning and NuGet auditing. `Directory.Build.props` enables deterministic builds and the current recommended .NET analyzers.
- **Layer boundary:** `ConnectOnion.WinUIClient.Core` owns WinUI-free models, SQLite repositories, notification policy, projections, and other testable services. The WinUI executable references Core, and ArchUnitNET tests prevent Core from taking a `Microsoft.UI` dependency or Data from depending on ViewModels.
- **Lifecycle and diagnostics:** the generic host is started before the window, stopped once on window/tray exit, and drains the run manager with bounded timeouts. Serilog writes structured rolling logs to `AppStorage.LogsDir` — `<data root>\logs`, so they follow the database instead of a fixed path (10 MB per file, 14 retained files). The teardown is instrumented end to end — exit funnel, page disarm outcome, minimize-to-tray, then each shutdown phase's duration — so a clean exit is distinguishable from a crash and from a minimize after the fact; see `docs/CONCURRENCY.md`.
- **Shutdown races:** pages disarm timers in `Unloaded`, which is not guaranteed to fire on window close, so `Views.IShutdownDisarmable` lets `MainWindow.DetachWindowServices` disarm the live page synchronously before the async host shutdown the dispatcher keeps pumping through.
- **Cancellation:** page unloading cancels conversation restore; cancellation tokens flow through view-model loading and SQLite reads/writes where supported.
- **Sidebar scale:** the shell binds one flat, heterogeneous `ItemsRepeater` instead of nested, non-virtualized repeaters. Only expanded agents load conversations, in keyset-paginated pages of 25, while active and pinned conversations remain reachable. Presence probing eagerly checks at most 32 agents and checks later rows as they are realized, with four probes in flight.
- **Navigation history:** each Frame entry carries a `ShellNavigationContext` with stable agent/conversation IDs. Back/Forward restores selection from SQLite, prunes entries whose entity was deleted, and preserves history across forced page reloads; only destructive navigation resets clear both stacks.
- **Storage scale:** sidebar, tray, and search surfaces read lightweight `AgentSummary` rows rather than cached `/info` blobs. Production session reads and writes are targeted; `SessionPage` keeps partial pages out of the whole-index `SaveAsync` API. Schema v9 gives FTS deletes an indexed stable-key map, v10 adds the execution-delete and sidebar-order indexes, and each connection uses WAL-compatible `synchronous=NORMAL` instead of paying a `FULL` fsync on every small transaction.
- **Durable attention state:** schema v11 persists per-conversation unread counts and approval attention. Notification policy updates those fields only when the conversation is not already visible, and opening the conversation clears them, so badges survive restart without deriving UI state from transient notifications. Schema v12 then removes the unused plaintext `agents.invite_code`; onboarding credentials remain in memory for one connection only.
- **Rendering:** `MarkdownTextBlock` caches its Markdig syntax tree. Search-highlight and theme rerenders no longer parse the same message again.
- **Resource lifecycle:** `ChatComposer` implements deterministic cleanup for speech recognition, AudioGraph nodes, Win2D stroke resources, timers, cancellation sources, and event subscriptions. Audio-meter startup observes cancellation after each asynchronous acquisition so page unload cannot leave a microphone graph behind.
- **Publish assets and exit:** unpackaged Release publish explicitly carries the `Assets` tree, and window icons resolve from an absolute application-base path. WebSocket shutdown has a 1.5-second hard budget, connections close concurrently, and structured logs record shutdown phase timings.
- **Shutdown ordering:** window activation, presence, and session callbacks detach before the Generic Host service provider is disposed. Closed callbacks are idempotent and use cached service references, preventing late WinUI events from resolving a disposed container.
- **Localization:** WinUI `.resw` resources cover English and Simplified Chinese under `Strings/en-US` and `Strings/zh-CN`; architecture tests enforce key-for-key parity, non-empty translations, XAML `x:Uid` coverage, and Core resource lookups. Keep fallback XAML text when adding resources so development builds remain diagnosable. Strings assembled at runtime cannot carry an `x:Uid`, so they go through `Common/LocalizedStrings.Get(key, fallback)`, which takes the English text as a parameter for the same reason.
- **Analyzer hygiene:** a clean unpackaged x64 application build completes with zero compiler/analyzer warnings. CA1848 is enforced, so *every* logging call site uses a cached `LoggerMessage` delegate, and persisted values use explicit culture semantics.
- **Atomic benchmark diagnostics:** startup-readiness JSON is written through `AtomicTextFile`, so the performance harness observes either the previous complete report or the next complete report while milestones are being updated, never a partially rewritten document.
- **Logging reaches the file three ways and no fourth:** constructor-injected `ILogger<T>` for container-created types, a `Configure(ILoggerFactory)` facade for the two `static` entry points in `Core` (`NotificationLog`, `IdentityStore`), and `AppServices.Logging` for framework-created code-behind. `Debug.WriteLine` is `[Conditional("DEBUG")]` and therefore never a failure path's only record — it appears only alongside a real logger call, or for development tracing (per-run-phase lines) that would be noise in a shipped log.
- **Testing:** the protocol fake server uses an in-process Kestrel WebSocket endpoint on an OS-assigned port, eliminating `HttpListener` ACL and port-reservation races. The FlaUI project discovers 41 tests: 36 required shell/chat tests, one explicitly skipped Explorer drag diagnostic, the trimmed-runtime scenario, and three opt-in diagnostics/audits. Real-window coverage includes page navigation/history, five modal-overlay Esc/focus-return cases, Agent Detail template/first-send flows, keyboard submit, startup-recovery UX, idempotent Stop, abnormal-drop resume, retry, mid-turn conversation switching, restart restore, markdown-aware in-chat find, agent rename persistence, cold notification routing, and agent-icon surfaces.
- **CI:** restore audits vulnerable packages; test runs emit Cobertura coverage and enforce a ratchet against `coverage-baseline.json` (currently 88.67% Protocol / 86.27% Core, 0.25pp tolerance) rather than fixed thresholds; Release publishes use ReadyToRun with trimming enabled (see TRIMMING.md); the isolated-profile 36-test FlaUI gate runs on every push and PR; and a separate manual-only job can exercise a real deployed agent.

## Operational checks

```powershell
dotnet restore ConnectOnion.WinUIClient/ConnectOnion.WinUIClient.sln -p:Platform=x64
dotnet build ConnectOnion.WinUIClient/ConnectOnion.WinUIClient.sln -c Release --no-restore -p:Platform=x64
dotnet test tests/ConnectOnion.Protocol.Tests/ConnectOnion.Protocol.Tests.csproj --collect:"XPlat Code Coverage" --settings coverage.runsettings --results-directory TestResults/protocol
dotnet test tests/ConnectOnion.WinUIClient.UnitTests/ConnectOnion.WinUIClient.UnitTests.csproj --collect:"XPlat Code Coverage" --settings coverage.runsettings --results-directory TestResults/client
dotnet test tests/ConnectOnion.IntegrationTests/ConnectOnion.IntegrationTests.csproj --collect:"XPlat Code Coverage" --settings coverage.runsettings --results-directory TestResults/integration
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Coverage.ps1 -ResultsDirectory TestResults
```

For a local real-window smoke test, publish the same unpackaged ReadyToRun shape as CI and point
the test at that executable. A normal build output does not carry the unpackaged Windows App SDK
bootstrapper reliably enough for this gate.

```powershell
dotnet publish ConnectOnion.WinUIClient/ConnectOnion.WinUIClient.csproj `
  --configuration Release --runtime win-x64 --no-restore -p:Platform=x64 `
  -p:RunUnpackaged=true -p:SelfContained=true -p:WindowsAppSDKSelfContained=true `
  -p:PublishTrimmed=true -p:PublishReadyToRun=true
$env:CONNECTONION_UI_TEST_EXE = (Resolve-Path 'ConnectOnion.WinUIClient/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/ConnectOnion.WinUIClient.exe')
$env:CONNECTONION_DATA_ROOT = Join-Path $env:TEMP 'ConnectOnionUiSmoke'
dotnet test tests/ConnectOnion.WinUIClient.UITests/ConnectOnion.WinUIClient.UITests.csproj `
  --configuration Release --filter 'Category=UiSmoke'
```

For launch time and steady-state memory, run `scripts/Measure-Performance.ps1` — see
`docs/PERFORMANCE.md` for the method, the budgets, and the current baseline.

For deeper performance investigations, capture an ETW trace with Windows Performance Recorder while restoring a long conversation and scrolling/searching it. Compare UI-thread CPU, allocations, SQLite I/O, and working set against a fixed conversation fixture; do not accept timing results from a Debug build.

## Follow-up backlog

- Keep en-US/zh-CN resource parity green and complete manual zh-CN layout, text-scaling, and Narrator validation.
- Expand real-window tests into Markdown visual output, direct tray-icon interaction, warm OS-notification delivery/click, and a deterministic drag/drop source. Cold notification routing is covered through an automation launch hook; the Explorer drag diagnostic stays skipped while DPI/layout can move its source target.
- Add reconnect, shutdown-drain, and cold-activation race integration coverage.
- Establish measured budgets for 1,000-message restore, search latency, and scrolling frame time. Cold start and steady-state memory are done — `scripts/Measure-Performance.ps1` measures them and `docs/PERFORMANCE.md` records the budgets and the current baseline.
