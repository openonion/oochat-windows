# ConnectOnion Desktop — Test Plan & Test Cases

> Version 2.1 · Architecture and test-infrastructure status refreshed 2026-08-10 against the current working tree
> Test counts are a dated generated snapshot, not a permanent constant. Protocol Conformance and real-window UI tests are reported separately from the headless xUnit total.

---

## Table of Contents

1. [Document Goals](#1-document-goals)
2. [Project Analysis](#2-project-analysis-grounded-in-real-code)
3. [Test Scope](#3-test-scope)
4. [Out of Scope](#4-out-of-scope)
5. [Test Strategy](#5-test-strategy)
6. [Test Layers](#6-test-layers)
7. [Test Environment](#7-test-environment-and-ci)
8. [Test Data](#8-test-data-and-fixtures)
9. [Mock and Fake Strategy](#9-mock-and-fake-strategy)
10. [Per-Module Test Checklist](#10-per-module-test-checklist)
11. [Detailed Test Cases](#11-detailed-test-cases)
12. [End-to-End Flows](#12-end-to-end-flows)
13. [UI Automation Standards](#13-ui-automation-standards)
14. [Accessibility Testing](#14-accessibility-testing)
15. [Performance and Stability](#15-performance-and-stability)
16. [Regression Strategy](#16-regression-strategy)
17. [Defect Severity Levels](#17-defect-severity-levels)
18. [Test Pass Criteria](#18-test-pass-criteria)
19. [Release Gate Criteria](#19-release-gate-criteria)
20. [Test Directory Structure](#20-test-directory-structure)
21. [Implementation Priority](#21-implementation-priority-and-backlog)
22. [Appendix A — Design Gaps Surfaced by This Plan](#appendix-a--design-gaps-surfaced-by-this-plan)

---

> Verification refresh: 2026-08-10. The Core seam, CommunityToolkit.Mvvm, Generic Host dependency injection, ArchUnitNET boundaries, fake-agent fixtures, SQLite fixtures, the ratcheted coverage gate, and the FlaUI real-window project are implemented. The run runtime now sits inside the Core seam and has behavioural tests, and the shell smoke suite runs on every push and PR. See [AGENTS.md](../AGENTS.md), [PERFORMANCE.md](./PERFORMANCE.md), and [PERFORMANCE_AUDIT_2026-07-25_EN.md](./PERFORMANCE_AUDIT_2026-07-25_EN.md) for the authoritative architecture and measured evidence.

## 1. Document Goals

- Describe the **current** test architecture, Core/App boundary, project layout, and CI behavior.
- Record reproducible Release commands and a dated snapshot for volatile test counts.
- Keep headless xUnit, real-window UI tests, performance/memory evidence, and Protocol Conformance as distinct gates.
- Define release criteria and manual evidence requirements for desktop-, hardware-, and OS-dependent behavior.
- Give every unfinished coverage theme an owner, an expected outcome and a verification method in §21, so the backlog is explicit rather than folklore.

## 2. Project Analysis (grounded in real code)

### 2.1 Main modules

Production projects sit at the repo root, test projects under `tests/`. The live smoke test and trimmed console harness are intentionally outside the `.sln`:

| Project | In `.sln` | In CI | Responsibility |
|---|---|---|---|
| `ConnectOnion.WinUIClient` | ✅ | ✅ build | The WinUI 3 app: XAML, code-behind, window-bound services (`net10.0-windows10.0.19041.0`) |
| `ConnectOnion.WinUIClient.Core` | ✅ | ✅ via tests | Every WinUI-free surface of the client (models, repositories, notifications, projections, view models) → the headless test target |
| `ConnectOnion.Protocol` | ✅ | ✅ build | Transport-agnostic protocol library (`net10.0`), zero WinUI dependency → headless-testable |
| `ConnectOnion.Protocol.Conformance` | ✅ | ✅ **CI gate** | Cross-checks Ed25519 / canonical JSON against the Node.js `ref-sign.js` |
| `ConnectOnion.Protocol.LiveTest` | ❌ | manual only | Real deployed-agent smoke run only through an explicit workflow dispatch |
| `ConnectOnion.PortableLauncher` | ✅ | ✅ build + release | Dependency-free NativeAOT launcher at the portable ZIP root |
| `tests/ConnectOnion.Protocol.Tests` | ✅ | ✅ | xunit unit tests for the wire protocol |
| `tests/ConnectOnion.WinUIClient.UnitTests` | ✅ | ✅ | Headless xunit tests over `Core`, plus the ArchUnitNET layer-boundary gate |
| `tests/ConnectOnion.IntegrationTests` | ✅ | ✅ | xunit tests against a real SQLite file (schema, migrations, repositories, identity) |
| `tests/ConnectOnion.WinUIClient.UITests` | ✅ | ✅ | xUnit + FlaUI (UIA3) real-window tests. The project discovers all 41; 36 required shell/chat tests run on every push/PR, one Explorer drag diagnostic is skipped, the trimmed-runtime scenario has its own gate, and three diagnostics/audits remain opt-in. |
| `tests/ConnectOnion.TrimSmoke` | ❌ | opt-in audit | Trimmed console harness for serialization, persistence, and identity restart paths |

The test projects can't reference the app project (`net10.0-windows` + Windows App SDK won't load in a headless test host), so they take a `ProjectReference` on `ConnectOnion.WinUIClient.Core`, which holds every WinUI-free surface of the client. **Moving a production file into `Core` is how it comes under test.** (This replaces the older `<Compile Include>` link lists — those are gone.)

### 2.2 Key pages and windows

- **Window**: `MainWindow` — the single top-level window with a custom title bar, split across partials:
  `MainWindow.xaml.cs` plus `.Agents.cs`, `.FileMenu.cs`, `.EditMenu.cs`, `.ViewMenu.cs`, `.ChatShortcuts.cs`, `.HelpMenu.cs`, `.Shortcuts.cs`, `.Notifications.cs`, `.Overlays.cs`, `.Placement.cs`, `.SessionSearch.cs`, `.DragDrop.cs`, `.Tray.cs`, `.Tray.Interop.cs`, and `.Tray.Menu.cs`
- **Pages**: `HomePage`, `ChatPage` (+ `ChatPage.Find.cs`), `AgentDetailPage` inside `ContentFrame`; `SettingsPage` hosted directly by `SettingsOverlay` rather than navigated to. `Architecture/NavigationReachabilityTests` fails the build on a page that is neither — which is what `SessionsPage` had become before it was deleted.
- **In-window modal overlays** (**not separate Windows, not ContentDialogs**):
  `SettingsOverlay`, `KeyboardShortcutsDialog`, `AboutOverlay`, `AddAgentForm`, `SessionSearchOverlay` — all share one shape: dimmed backdrop, centered card, Esc / backdrop / close-button dismissal, focus returned to the opener. Created lazily by `MainWindow.Overlays.cs`; `IsModalOverlayOpen` gates every global accelerator, `UpdateModalFocusScope` takes the background out of the Tab order, and each returns a `ModalOverlayAutomationPeer` so UIA sees a dialog (and so `ByAutomationId` resolves at all).
- **Custom controls**: `ChatComposer` (+`.Speech`), `ShellSidebar` (+`.Events`), `AddAgentForm`, `AgentAvatar`, `AgentShareDialog`, interactive chat cards, `ToolActivityView`, `MarkdownTextBlock`, `HighlightedTextBlock`, `InAppNotificationHost`, `OfflineNoticeBar`, `IconText`, settings panes, and identity dialogs

### 2.3 Main ViewModels

MVVM is implemented with **CommunityToolkit.Mvvm 8.4.2** source generators. Models and view models are `partial` classes deriving from the repository's `Common/ObservableObject.cs` bridge over `CommunityToolkit.Mvvm.ComponentModel.ObservableObject`.

- `[ObservableProperty]` is applied to partial properties.
- `[NotifyPropertyChangedFor]`, partial `On<Property>Changed` hooks, and `[RelayCommand]` are used for generated notification and command plumbing.
- A property remains hand-written only when its setter transforms or normalizes the incoming value.
- View models receive dependencies through constructor injection and are registered as transient services in the Generic Host.
- Framework-created pages obtain their view model through `App.GetService<T>()` because WinUI creates pages through parameterless constructors and `Frame.Navigate`.

| ViewModel | Main responsibility |
|---|---|
| `ChatViewModel` (`.Run`, `.Conversation`, `.StreamEvents`) | Conversation loading, sending/stopping, run subscription, stream projection, and interactive responses |
| `SettingsViewModel` | Theme, font, sidebar, microphone, notification, shortcut, identity, and usage settings |
| `AgentDetailViewModel` | Agent details, capabilities, sharing, and presence |
| `KeyboardShortcutsViewModel` | Shortcut groups, search, rebinding, conflict handling, and reset |
| `PresenceAwareViewModel` | Shared online-presence behavior |

### 2.4 Main Services

- **Composition and dependency injection**: `App` builds a .NET Generic Host; `Services/ServiceRegistration.AddAppServices` registers singletons, hosted services, resilient HTTP, logging, and transient view models. `App.Services` is the composition root. `Services/AppServices.cs` is a typed, get-only accessor used by framework-created code-behind, not a second container.
- **Runtime (Core)** `Services/Runtime/`: `AgentSessionManager`, `AgentConnectionRegistry`, `ConversationRunRegistry`, `ChatTurnProjection`, `ToolActivityProjector`, `ToolActivityMigration`, and related run/persistence services.
- **Notifications** `Services/Notifications/`: `NotificationCoordinator`, `NotificationPolicy`, `DedupCache`, window-presence and navigation abstractions, activation routing, settings, and scheduling.
- **Attachments** `Services/Attachments/`: validation, encoding, intake, image caching, file picking, and drag/drop handling. WinUI-bound picker/drop services remain in the app project; platform-free logic lives in Core.
- **HTTP resilience**: the named `agent` client is created by `IHttpClientFactory` and uses Polly retry plus per-attempt timeout. Protocol code receives the configured `HttpClient` and remains package-agnostic.
- **Other shared services**: `AgentInfoService`, `AgentPresenceService`, `ConnectionTester`, `ThemeService`, `ClipboardService`, `MimeTypeResolver`, `ConversationCache` (four-entry idle LRU), `AppVersionService`, `KeyboardShortcutService`, and `KeyboardShortcutCatalog`.

### 2.5 Persistence

SQLite (`Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.provider.winsqlite3`), WAL journal, `foreign_keys = ON`.
Path: unpackaged → `%AppData%\ConnectOnion\connectonion.db`; packaged → MSIX LocalState. Caches, agent icons and logs sit beside it under the same root.

**Logical tables (schema v12)**: `app_meta`, `agents` (incl. `icon_path`, a path relative to the data root — never image bytes; `invite_code` was removed by v12's table rebuild, since a trust credential must not sit in plaintext beside a DPAPI-protected seed), `sessions` (incl. approval mode, custom-title, unread-count and attention state), `preferences`, `messages` (one row per rendered bubble, not a JSON blob), `message_attachments` (metadata + local cache path only), `executions`, `trace_events`, `identity_keys` (DPAPI-protected seed and mnemonic), `usage_events` (the non-cascading token-usage ledger), `message_search` (FTS5), and `message_search_map` (stable key → assigned FTS rowid). SQLite also creates internal shadow tables for the FTS virtual table; those are implementation details rather than application-owned schema entries.

**Migration status:** `SchemaMigrator` versions the database with `PRAGMA user_version`, applies ordered forward migrations transactionally, and rejects databases created by a newer app version. Schema evolution remains high-risk, so every migration must add an old-snapshot integration test (see §10.4 and `DB-MIGRATION-*`).

Write path: `ConversationRepository.UpsertMessagesAsync` is the **only** writer — incremental `INSERT ... ON CONFLICT(conversation_id, id) DO UPDATE`, with each written row's attachments replaced wholesale by a `message_id`-scoped delete + re-insert.

### 2.6 Networking

`ConnectOnion.Protocol.AgentConnectionService` (a C# port of the TS SDK's `remote-agent.ts`):

- Handshake `CONNECT` → `CONNECTED`; send `INPUT`; resolve on `OUTPUT`
- **Real timeout constants**: `ConnectTimeoutMs = 30_000`, `SilenceTimeoutMs = 60_000` (the 60s silence watchdog is the *only* bounded wait — `ask_user` may legitimately pend on a human indefinitely)
- Events: `StreamEvent`, `ConnectionLost`, `AskUserRequested`, `ApprovalRequested`, `PlanReviewRequested`, `OnboardRequired`
- Response API: `RespondAskUserAsync`, `RespondApprovalAsync(approved, scope)`, `RespondPlanReviewAsync`
- `EndpointResolver`: relay lookup, direct-endpoint probing, `/info` capability parsing
- Identity: `AgentIdentity` + `CanonicalJson` + `Hex` (Ed25519, verified byte-for-byte by the Conformance project)

### 2.7 Application lifecycle

- `Program.cs` owns the custom entry point (`DISABLE_XAML_GENERATED_MAIN`) and enforces the single-instance contract with `AppInstance.FindOrRegisterForKey("ConnectOnion.Main")`. A secondary process redirects activation and exits before constructing a second WinUI `Application`.
- The `App` constructor configures Serilog, builds the Generic Host, and exposes the service provider through `App.Services`.
- `OnLaunched` ensures application storage exists, starts hosted services, creates and activates the single `MainWindow`, and then flushes pending activation work.
- Cold-start activation can arrive before the window exists, so the request is buffered and replayed after window creation.
- `AgentSessionManager` owns runs and sockets at application scope; page navigation does not cancel an active turn.
- `App.ShutdownAsync` is idempotent, drains the run manager, stops the Generic Host under bounded timeouts, logs each phase, and flushes Serilog last.
- Tray, notification, and activation behavior remain desktop-session-sensitive and therefore need a mix of headless integration tests, FlaUI tests, and manual checks.

### 2.8 Existing tests and verified snapshot

**Verified snapshot (2026-08-10): 1,601 passing headless xUnit tests.** The three suites are deliberately counted separately so future changes can identify where drift occurred:

| Suite | Project | Passing tests | CI behavior |
|---|---|---:|---|
| Protocol xUnit | `tests/ConnectOnion.Protocol.Tests` | **225** | Runs on every push and pull request |
| Core unit + architecture | `tests/ConnectOnion.WinUIClient.UnitTests` | **1,179** | Runs on every push and pull request; includes the ArchUnitNET boundary gate |
| SQLite integration | `tests/ConnectOnion.IntegrationTests` | **197** | Runs on every push and pull request against temporary real SQLite files |
| **Headless xUnit total** | — | **1,601** | Sum of the three rows above |

`ConnectOnion.Protocol.Conformance` is a separate Release gate. It cross-checks address derivation, canonical JSON, signatures, and verification against the Node.js reference and is **not** included in the 1,601 xUnit total.

> Agent naming, markdown rendering/layout, diagnostic-output, portable-release, and sidebar
> selection/cache contracts account for the latest Core/architecture growth, while targeted agent
> rename coverage accounts for the latest SQLite integration growth. `tests/Directory.Build.props`
> keeps one output path per configuration so a platform switch cannot silently report a stale
> assembly. Re-derive counts with a plain `dotnet test` per project.

The real-window project currently discovers **41 xUnit/FlaUI tests**: 36 required shell/chat tests, one explicitly skipped Explorer drag diagnostic, one trimmed-runtime scenario, and three opt-in diagnostics/audit tests. The CI filter selects 38 because the class-level category also includes the no-op memory probe and skipped diagnostic; the verified trimmed run reports 37 passed and one skipped.

| File | Test | Execution requirement |
|---|---|---|
| `ShellSmokeTests.cs` | `Launch_ShowsResponsiveConnectOnionWindow` | Published unpackaged executable via `CONNECTONION_UI_TEST_EXE` |
| `ShellSmokeTests.cs` | `Launch_StartupFailure_ShowsRecoveryMessageAndExitsCleanly` | Same; deterministic pre-window failure hook verifies native recovery UX |
| `ShellSmokeTests.cs` | `SidebarSettingsClick_ShowsSettingsOverlay` | Same |
| `ShellSmokeTests.cs` | `SidebarSettingsRow_IsAReachableFooterCommand` | Same |
| `ShellSmokeTests.cs` | `CloseToTray_SecondLaunch_RestoresTheResponsiveWindow` | Same |
| `ShellSmokeTests.cs` | `SettingsAgents_Click_ShowsAgentManagementActions` | Same |
| `ShellSmokeTests.cs` | `SettingsIdentity_Click_ShowsBackupAndRestoreActions` | Same |
| `ShellSmokeTests.cs` | `SidebarAddAgent_Click_OpensShellOverlayAndFocusesInput` | Same |
| `ShellSmokeTests.cs` | `UsageHeatmap_RendersDaySquares_WithAccessibleDescriptions` | Same |
| `ShellSmokeTests.cs` | `FirstRunHome_ShowsEmptyStateAndReachableAddAgentAction` | Same; isolated empty profile |
| `ShellSmokeTests.cs` | `SessionSearch_OpensFocusedAndClosesWithEscape` | Same; isolated SQLite fixture |
| `ShellSmokeTests.cs` | `HelpKeyboardShortcuts_OpensFocusedAndClosesWithEscape` | Same |
| `ShellSmokeTests.cs` | `HelpAbout_OpensFocusedAndOkClosesIt` | Same |
| `ShellSmokeTests.ChatExtended.cs` | `Navigation_HomeAgentChatAndAgentsLibrary_AllOpen` | Same; isolated agent/session fixture |
| `ShellSmokeTests.ChatExtended.cs` | `AgentDetail_SuggestionTemplate_PopulatesComposerDraft` | Same; real Agent Detail composer |
| `ShellSmokeTests.ChatExtended.cs` | `NewChat_AgentDetailFirstSend_NavigatesToChatAndCompletesTurn` | Same; File → New Chat, keyboard input, loopback fake agent |
| `ShellSmokeTests.ChatExtended.cs` | `Chat_StopResponse_DisablesImmediatelyAndSettlesExactlyOnce` | Same; loopback fake agent holds OUTPUT until INTERRUPT is observed |
| `ShellSmokeTests.ChatExtended.cs` | `Chat_AgentErrorThenRetry_CompletesOnTheNextInput` | Same; loopback fake agent emits ERROR then OUTPUT |
| `ShellSmokeTests.ChatExtended.cs` | `Chat_SwitchConversationDuringTurn_ReturnsToOnePersistedReply` | Same; loopback fake agent + SQLite assertion |
| `ShellSmokeTests.ChatExtended.cs` | `Chat_ProcessRestart_RestoresSentTurnFromSQLite` | Same; two real app processes |
| `ShellSmokeTests.ChatExtended.cs` | `NotificationActivation_ColdStart_OpensTargetConversation` | Same; automation launch hook enters the production activation router |
| `ShellSmokeTests.ChatExtended.cs` | `AgentIcon_ContextMenuRemovesCustomIcon_AndAddFormExposesPicker` | Same; real context menu and Add Agent overlay |
| `MemoryLeakTests.cs` | `NavigationSurfaces_RepeatedOpenClose_ReachesStableHighWaterMark` | Opt-in: `CONNECTONION_MEMORY_TEST=1`; normally run through `scripts/Test-MemoryLeaks.ps1` |
| `PerformanceAuditTests.cs` | `ReleaseUiOperations_AreMeasuredAgainstSyntheticLargeHistories` | Opt-in: set `CONNECTONION_UI_PERF_OUT` to an output JSON path |

All 36 required shell/chat tests pass together against a trimmed, self-contained Release ReadyToRun unpackaged executable; the Explorer drag diagnostic is skipped. The portable root launcher and nested `app\` payload remain covered by the release rehearsal. CI executes the shell/chat suite on every push and pull request using an isolated data root and uploads TRX/screenshot evidence unconditionally.

#### Reproducible count snapshot

Run from the repository root with a .NET 10 feature-band SDK. The verified snapshot used Release configuration; do not combine UI tests or Protocol Conformance into the xUnit total.

```powershell
dotnet --version

dotnet test tests\ConnectOnion.Protocol.Tests\ConnectOnion.Protocol.Tests.csproj --configuration Release
dotnet test tests\ConnectOnion.WinUIClient.UnitTests\ConnectOnion.WinUIClient.UnitTests.csproj --configuration Release
dotnet test tests\ConnectOnion.IntegrationTests\ConnectOnion.IntegrationTests.csproj --configuration Release

dotnet test tests\ConnectOnion.WinUIClient.UITests\ConnectOnion.WinUIClient.UITests.csproj --configuration Release --list-tests
dotnet run --project ConnectOnion.Protocol.Conformance\ConnectOnion.Protocol.Conformance.csproj --configuration Release
```

This is a **generated snapshot dated 2026-08-10**. When counts change, update the three suite values, the total, verification date, SDK/build mode, and the evidence commit in the same pull request. Count changes alone are not a quality signal.

CI collects Cobertura data with `coverage.runsettings`, uploads the raw reports, and then runs
`scripts/Test-Coverage.ps1`. The gate merges source lines by file and line number so Core code
exercised by both unit and integration suites is counted once. Generated `obj/` sources are
excluded.

The gate is a **ratchet**, not a fixed pair of thresholds. `coverage-baseline.json` holds the
high-water mark per assembly; a run fails when coverage falls below it by more than 0.25pp, or
below the absolute floor in the script's parameters. Raising the baseline is a deliberate
`-UpdateBaseline` commit. The previous fixed thresholds (86.0% / 88.5%) were 3 and 6 lines above
the measured values, which made any change that added an uncovered branch a CI failure — a gate
that tight rewards padding tests rather than real ones.

Current baseline: **88.67% `ConnectOnion.Protocol`**, **86.27% `ConnectOnion.WinUIClient.Core`**.
Core's figure is lower than the old 88.5% threshold because *more code is now measured*, not
because less is tested: the ~1,300-line run runtime moved into `Core` from the app project, where
it had been outside instrumentation entirely. Its still-uncovered socket half is now visible in
the denominator instead of hidden by exclusion.

The gate additionally reports the size of what it **cannot** measure — `ConnectOnion.WinUIClient`
is ~24.8k lines of C# and ~9.9k of XAML that no Cobertura report can include, because a headless
host cannot load the Windows App SDK and the FlaUI suite runs the app out-of-process. That number
is source lines and is not comparable to the coverage table.

### 2.9 Remaining gaps

The test infrastructure itself is implemented: the Core seam, ArchUnitNET boundary tests, fake-agent server, temporary SQLite fixtures, FlaUI project, startup/performance harnesses, and memory-leak gate all exist. The remaining work is expansion and hardening rather than creating those projects from scratch.

- Real-window coverage now includes 36 required shell/chat tests plus one explicitly skipped Explorer drag diagnostic and three opt-in diagnostics/audits. It covers page navigation and history, all five modal overlays, Agent Detail template and keyboard-first-send behavior, Stop/interrupt settlement, abnormal-drop reconnect/resume, retry, mid-turn conversation switching, restart restoration, markdown-aware in-chat find, agent rename persistence, cold activation routing, startup-failure recovery, and agent-icon surfaces. Direct tray-icon interaction, actual OS notification delivery/click, and deterministic Explorer drag/drop remain manual; the cold-activation test injects the post-click routing arguments and therefore does not display a system toast. The Release trimmed publish now carries `Microsoft.WindowsAppRuntime.Insights.Resource.dll`; startup registration was verified successfully after the previous `0x8007007E` failure, and the portable package audit fails if the DLL is missing.
- The activation lost-wakeup race has a deterministic 1,000-iteration concurrency contract, and startup recovery has a real-window test. Deeper reconnect and shutdown integration coverage remains open.
- Large-history UI measurements now fail on explicit shell, first-open, cached-reopen, tool-expand, virtualization, and private-bytes ceilings. Qualified WPR/ETW frame evidence remains manual.
- High-contrast resource coverage and critical live-region/AutomationId contracts are automated. Narrator reading order, text scaling, and full keyboard traversal still require manual evidence.
- Release trimming is enabled and guarded by `TrimmingGateTests`; `docs/TRIMMING.md` records the original then-current 30-test evidence, and this refresh verifies the expanded 36-test required suite against the same trimmed shape.
- The startup harness now uses a named event that reaches the real graceful-exit path. Qualified cold-start/WPR evidence still depends on an elevated, policy-enabled desktop runner and remains open.
- Signed MSIX publishing is deferred unless that distribution channel is restored; the portable ZIP is the current release channel.
- Hardware/external-state scenarios— real-agent high-throughput streaming, speech, notifications, packaged activation, and native/XAML profiling— remain manual until suitable automation evidence exists.

### 2.10 High-risk modules and flows

Ranked by *probability × blast radius*:

| # | Module | Risk |
|---|---|---|
| 🔴 1 | `AppDatabase` schema evolution | A migration framework now exists, but forward-only schema changes can still cause irreversible data loss without old-snapshot and rollback-on-failure coverage. |
| 🔴 2 | `AgentSessionManager` + `ConversationRunRegistry` | Cross-page ownership of connections and turns. In-flight runs during page close / conversation switch / app exit are a concurrency minefield. |
| 🔴 3 | `ConversationRepository.UpsertMessagesAsync` | The single write path, incremental upsert plus attachment delete+insert. A bug here permanently corrupts user data. |
| 🔴 4 | `ChatTurnProjection` | The single event→ bubble mapping, **driven by both the live and the headless path**. Divergence between the two produces double-rendered bubbles. |
| 🟠 5 | `AgentConnectionService` timeouts / reconnect / dispose | 30s connect timeout, 60s silence watchdog, and unbounded `ask_user` interact in non-obvious ways. |
| 🟠 6 | `NotificationCoordinator` + `WindowPresenceService` | Decides whether to interrupt the user. False positives are user-visible spam. |
| 🟠 7 | Single-instance + cold-start activation race | The `Interlocked` buffer/replay only misbehaves under specific timing. |
| 🟡 8 | `WinUiMarkdownRenderer` | Already crashed in production over the `InlineUIContainer` constraint — direct evidence that this area has no test protection. |
| 🟡 9 | `ChatMessageTemplateSelector` | Resolved once per container; if a message kind ever becomes mutable, the row keeps the wrong template forever. |
| 🟡 10 | `IdentityStore` DPAPI | Historical plaintext seeds migrate in place; copied-profile/corrupt protected blobs still require a visible identity reset. |

**High-risk user flows**: ① switching conversations mid-stream and returning ② reconnect after a drop ③ history restore after restart ④ background turn completion + notification ⑤ answering and persisting interactive cards (approval / ask_user).

---

## 3. Test Scope

- All of `ConnectOnion.Protocol` (wire format, connection state machine, identity signing, run registry)
- `ConnectOnion.WinUIClient`: ViewModels, Services, Data, Common (converters, selectors)
- WinUI controls, DataTemplates, overlays, focus and keyboard behavior
- SQLite persistence and restore
- WebSocket connection and its failure paths
- Application lifecycle (single instance, cold-start activation, tray, exit)
- Notification decisions and activation routing
- Accessibility semantics
- Performance and resource leaks

---

## 4. Out of Scope

- Browser compatibility, SEO, cookies, DOM selectors, cross-browser CSS, and web breakpoints — this is a native Windows application.
- Correctness or answer quality of a remote agent/LLM. Client tests verify protocol handling and user-visible behavior using controlled agents or fakes.
- Reliability guarantees for Windows itself; tests cover registration, payload construction, routing, and the client response to failures.
- Hardware-dependent speech/audio quality without a controlled device lab.
- Treating Task Manager Working Set alone as proof of a memory leak.

MSIX is outside the currently shipped portable channel. If publishing it is restored, signing, clean-machine installation, upgrade/uninstall behavior, packaged activation, and packaged cold-start become mandatory release work; historical unsigned-package evidence does not satisfy that future gate.

## 5. Test Strategy

1. **Keep the architecture testable.** Protocol and Core remain free of WinUI dependencies; the ArchUnitNET gate enforces the Core/App boundary.
2. **Prefer the cheapest reliable layer.** Pure behavior belongs in headless xUnit; SQLite behavior uses real temporary databases; wire behavior uses the fake WebSocket server; rendered desktop behavior uses FlaUI; OS/hardware-only evidence remains manual.
3. **Run comparable gates separately.** Headless xUnit counts, real-window tests, performance/memory audits, and Protocol Conformance must never be merged into one misleading number.
4. **Failure paths before polish.** Connection loss, restart/restore, background completion, interactive-card resolution, migration failure, and Release-only serialization are higher priority than another happy-path click test.
5. **Regressions follow defects.** Every fixed P0/P1 defect receives a reproducible regression test or a documented manual gate when automation is not feasible.
6. **Backlog lives in §21.** This document describes coverage and evidence; every unfinished theme belongs in that table with an owner and completion evidence, rather than being asserted in passing prose.

## 6. Test Layers

| Layer | Framework / method | Environment | Current role |
|---|---|---|---|
| **A. Protocol unit** | xUnit | Headless `net10.0` | Wire schema, encoding, identity, connection state, and run registry |
| **B. Core unit + architecture** | xUnit + ArchUnitNET | Headless, no `Microsoft.UI` | View models, projections, policies, caches, services, and enforced layer boundaries |
| **C. SQLite integration** | xUnit + real temporary SQLite | Headless Windows | Schema, migrations, repositories, persistence, identity, and lifecycle integration |
| **D. Static UI contracts** | xUnit source/XAML checks | Headless | Automation IDs, shared typography, source contracts, resource and accessibility invariants |
| **E. Real-window UI / E2E** | xUnit + FlaUI UIA3 | Published unpackaged process in a real desktop session | 36 required shell/chat tests, one skipped Explorer drag diagnostic, a trimmed-runtime check, plus three opt-in diagnostics/audits |
| **F. Protocol Conformance** | Console release gate + Node reference | Release | Cryptographic/canonical-JSON compatibility; reported separately |
| **G. Manual** | Checklist and profiler evidence | Real hardware/OS state | Narrator, high contrast, speech/audio, packaged install, notifications, ETW/WPR, real-agent stress |

There is **no separate MSTest WinUI Unit Test App** in the current solution. UI-bound behavior is either pushed into Core/static contracts or exercised against the real published process with FlaUI.

## 7. Test Environment and CI

| Item | Value |
|---|---|
| OS | Windows 11 for desktop evidence; CI uses `windows-latest` |
| SDK | .NET 10.0.302; `global.json`, CI, and release all pin the exact patch (`rollForward: disable`) because SDK-injected ILLink/ILCompiler packages participate in locked restore |
| CI build | `Release`, `x64`, packaging disabled during solution build |
| UI publish | `win-x64`, unpackaged, ReadyToRun enabled, **trimming enabled** (matches the shipped configuration) |
| Node | Node.js 22 for Protocol Conformance |
| Database | One temporary real SQLite file/profile per test; never the user's `%AppData%` database |
| UI test executable | `CONNECTONION_UI_TEST_EXE` must point to the published unpackaged executable |
| Performance evidence | Release/x64 on a real desktop session; machine/build/date recorded in the report |
| Accessibility matrix | Light/Dark, keyboard-only, Narrator/manual checks, and increased Windows text scaling |

`.github/workflows/ci.yml` performs the following sequence:

1. Checkout, .NET 10 setup, Node 22 setup, and NuGet cache.
2. Restore `ConnectOnion.WinUIClient/ConnectOnion.WinUIClient.sln` for x64.
3. Audit vulnerable direct and transitive NuGet packages.
4. Build the solution in Release/x64 with package generation, signing and ReadyToRun disabled for the build step. (Trimming applies only to `publish`, so it is not passed here.)
5. Run the Protocol, Core/architecture, and SQLite integration xUnit suites with Cobertura collection.
6. Compile and discover the UI test project.
7. Publish a trimmed, self-contained ReadyToRun `win-x64` unpackaged executable.
8. On every push and pull request, execute the 36 required real-window shell/chat tests against that published executable and an isolated data root; the Explorer drag diagnostic remains skipped.
9. Upload UI TRX/screenshot evidence when that suite runs, plus coverage artifacts on every run.
10. Finish with Protocol Conformance as a separate gate.

The standalone `dotnet test` and Conformance commands intentionally do not receive `-p:Platform=x64`; the solution maps some projects to `Any CPU`, and leaking a Windows `PLATFORM` environment variable into MSBuild produces incorrect output-path lookups.

## 8. Test Data and Fixtures

The current suites prefer generated or isolated fixtures over long-lived mutable files.

| Fixture / evidence | Current implementation | Used by |
|---|---|---|
| Fake agent server | Scriptable local HTTP/WebSocket server in the test code | Protocol handshake, timeouts, interactive events, disconnects, and lifecycle failures |
| Temporary SQLite profile | One isolated real database/data root per test or scenario | Schema, migrations, repositories, identity, usage, and runtime integration |
| Static XAML/source contracts | Repository XAML and C# source inspected by headless tests | Automation IDs, the shared type ramp, accessibility names, help-menu and architecture contracts |
| Synthetic performance conversations | Generated by `PerformanceAuditTests` at 100, 500, and 2,000 messages | First-open, cache-reopen, virtualization, Tool Activity expansion, and memory samples |
| Isolated memory profiles | Created by `MemoryLeakTests` / `scripts/Test-MemoryLeaks.ps1` | Repeated Settings, Add Agent, Agent Detail, conversation, and alternating-session scenarios |
| Published unpackaged executable | Release `win-x64` output selected through `CONNECTONION_UI_TEST_EXE` | All real-window FlaUI tests |
| Performance evidence | `TestResults/perf-audit/**` plus the dated audit document | Release decisions and before/after comparisons |

Any persistent binary or database fixture added later must document its schema/version, creation command, expected use, and owner. Tests must never point at the developer's real `%AppData%\ConnectOnion` profile.

## 9. Mock and Fake Strategy

| Dependency | Strategy | Rationale |
|---|---|---|
| Remote agent WebSocket | Scriptable local fake server | Exercises the real protocol and failure paths without an external deployment |
| Agent HTTP (`/health`, `/info`, relay) | Stub `HttpMessageHandler` or controlled local endpoint | Deterministic retry, timeout, and capability tests |
| SQLite | Real SQLite on a temporary file/profile | SQL, migrations, FK behavior, WAL, and DPAPI-adjacent persistence are the behavior under test |
| Windows notifications | Notification abstractions/fakes for headless decisions; real activation remains desktop/manual | OS delivery is not deterministic in a headless runner |
| Window presence/navigation | Programmable fakes | Makes notification policy and routing headlessly testable |
| Dispatcher/scheduling | Injected scheduling abstractions where available | Avoids timing sleeps and keeps orchestration deterministic |
| URI launch | `IUriLauncher` fake | The static WinRT call has already been wrapped and is testable |
| DPAPI identity | Real CurrentUser DPAPI against isolated test data | Validates the actual protection/reset contract |
| Real-window UI | Published executable + isolated data root + FlaUI | Verifies the real XAML/UIA surface instead of a mocked visual tree |

The Generic Host is the production composition root. Tests normally construct the smallest subject graph directly or use registered abstractions; they do not mutate a parallel container. `AppServices` is a typed accessor for framework-created code-behind, while view models use constructor injection.

## 10. Per-Module Test Checklist

| § | Module | Coverage |
|---|---|---|
| 10.1 | Startup & lifecycle | First launch, existing data, DB init failure, second instance, cold-start activation replay, tray restore, exit with in-flight runs |
| 10.2 | Agent management | CRUD, empty state, validation (blank address, untested connection), deleting the selected agent, deleting a connected agent, avatar initial |
| 10.3 | Agent connection | Handshake, 30s timeout, 60s silence watchdog, user disconnect, abnormal drop, reconnect, duplicate connect, agent switch mid-connect, dispose |
| 10.4 | Database | Idempotent schema, ordered versioned migrations, legacy snapshots, incremental upsert, attachment replacement, message ordering, concurrent writes, deletion ordering, usage-ledger retention, and corrupt/newer-schema failure |
| 10.5 | Chat & streaming | Send validation, duplicate-send guard, event projection, interrupt/cancel/timeout, survives page switch, correct persistence, interactive card answers |
| 10.6 | Markdown | Full syntax, unclosed fences, search highlight (**known crash**), baseline alignment, both themes |
| 10.7 | Notifications | `NotificationPolicy.Decide` all branches, dedup, foreground suppression, click routing, cold-start activation |
| 10.8 | Window / navigation / keyboard / focus / a11y / performance | See §11 |

---

## 11. Detailed Test Cases

> Fields: ID · Title · Layer · Priority · Preconditions/Data · Steps · Expected · Automatable · Framework · Code location · Risk

### 11.1 Startup and lifecycle (LC)

| ID | Title | Layer | Pri | Preconditions / Data | Steps | Expected | Auto | Framework | Code location |
|---|---|---|---|---|---|---|---|---|---|
| **LC-START-001** | First launch creates DB + directories | D | **P0** | Delete `%AppData%\ConnectOnion` | Launch | Directories and `connectonion.db` created; all 12 logical schema-v12 tables present; WAL on; `foreign_keys=ON`; `synchronous=NORMAL` on each connection | ✅ | xunit | `AppStorage.EnsureDirectories`, `AppDatabase.EnsureInitializedAsync`, `AppDatabaseSchemaTests` |
| **LC-START-002** | Launch with existing data loses nothing | D | **P0** | Seed DB with 2 agents / 3 sessions | Launch | Schema creation is idempotent; all data readable | ✅ | xunit | `AppDatabase.EnsureInitializedAsync` |
| **LC-START-003** | DB init failure is logged and does not leave a broken window | C | **P0** | Set `CONNECTONION_UI_STARTUP_FAILURE=1`; keep an unwritable-profile check as a manual OS boundary | Launch | A recovery message is shown; after dismissal services shut down and the process exits deterministically | ✅ automated fault injection + manual ACL boundary | FlaUI | `ShellSmokeTests.Launch_StartupFailure_ShowsRecoveryMessageAndExitsCleanly` |
| **LC-INST-001** | Second instance redirects and exits | E | **P0** | App already running | Launch the exe again | Second process exits; no second window; existing window comes forward | ✅ | FlaUI + process assertions | `Program.Main`, `AppInstance.FindOrRegisterForKey` |
| **LC-INST-002** | Cold-start activation is buffered and replayed | A | **P1** | — | Race activation and window creation 1,000 times, then flush | Activation is honored exactly once, not dropped | ✅ | xUnit | `DeferredActivationGateTests` |
| **LC-TRAY-001** | Tray click restores the window | E | P1 | Minimized to tray | Click tray icon | Window restores to foreground | ⚠️ | FlaUI | `Shell/MainWindow.Tray.cs` `RestoreFromTray` |
| **LC-EXIT-001** | Graceful shutdown drains in-flight runs | C | **P0** | A run is streaming | Call `ShutdownAsync()` | Completes within 8s; all sockets released; no unhandled exception | ✅ | xunit + fake server | `AgentSessionManager.ShutdownAsync` |
| **LC-EXIT-002** | Shutdown still releases when a run hangs | C | P1 | Fake server never replies | `ShutdownAsync()` | Returns after the 8s bound; sockets disposed anyway | ✅ | xunit | same |

### 11.2 Agent management (AGENT)

| ID | Title | Layer | Pri | Steps | Expected | Auto | Framework | Code location |
|---|---|---|---|---|---|---|---|---|
| **UT-AGENT-001** | Empty connection cannot be tested or added | A | **P0** | Open the form with an empty `Agent connection` input | `CanTest` and `CanAdd` remain false; the untouched empty field does not show an error | ✅ | xUnit | `AddAgentViewModelTests.EmptyInput_DoesNotShowValidationError` |
| **UT-AGENT-002** | Reject add before a successful connection test | A | **P0** | Enter a valid `0x…` address or HTTP(S) URL, skip Test | Test is enabled but Add remains disabled until that exact input passes | ✅ | xUnit | `AddAgentViewModelTests.HttpUrl_IsValidAndEnablesTesting` / `AgentAddress_IsValidAndEnablesTesting` |
| **UT-AGENT-003** | Missing reported name falls back locally | A | P1 | Successful connection response contains no agent name | Stored name uses a stable connection-derived fallback; the add form has no name field | ✅ | xUnit | `AddAgentViewModelTests.MissingReportedName_UsesStableConnectionFallback` |
| **DB-AGENT-001** | Agent create / read / delete | D | **P0** | CRUD | Round-trips correctly | ✅ | xunit | `AgentRepository` |
| **DB-AGENT-002** | Deleting an agent cascades to its sessions | D | **P0** | Delete an agent with conversations | `sessions` / `messages` stay consistent; no orphan rows (FK enforced) | ✅ | xunit | `AgentRepository` + FK |
| **UT-AGENT-004** | Delete an agent that is currently connected | C | **P0** | Agent has an active run | Run is cancelled, socket released, no dangling references | ✅ | xunit + fake | `AgentSessionManager.ReleaseConversationAsync`, `ConversationCache` invalidation |
| **UI-AGENT-001** | Empty state when no agents exist | B | P1 | Empty DB | HomePage shows the empty state and an add entry point | ✅ | FlaUI `FirstRunHome_ShowsEmptyStateAndReachableAddAgentAction` | `HomePage.xaml` |
| **UT-AGENT-005** | Avatar initial generation | A | P2 | Names: `"connectonion"`, `""`, `"🙂x"` | Yields `C`, `?`, and a whole grapheme respectively | ✅ | xunit | `NameInitialTests`; the shared `NameInitial` helper reads a whole grapheme |

### 11.3 Agent connection (CONN)

| ID | Title | Layer | Pri | Steps | Expected | Auto | Framework | Code location |
|---|---|---|---|---|---|---|---|---|
| **UT-CONN-001** | CONNECT → CONNECTED handshake | C | **P0** | Fake server responds normally | Connection ready; `IsConnecting` clears | ✅ | xunit + fake ws | `AgentConnectionService` |
| **UT-CONN-002** | 30s connect timeout | C | **P0** | Server accepts TCP but never sends CONNECTED | Times out at 30s; never hangs forever | ✅ (inject a controllable clock) | xunit | `ConnectTimeoutMs = 30_000` |
| **UT-CONN-003** | 60s silence watchdog fires | C | **P0** | Server goes silent after handshake | `ConnectionLost` raised at 60s | ✅ | xunit | `SilenceTimeoutMs = 60_000` |
| **UT-CONN-004** | Watchdog does **not** fire during `ask_user` | C | **P0** | Server sends ask_user then goes silent 5 min | Connection stays up (a human may take a while) | ✅ | xunit | `AgentConnectionService` — 🔴 **the behavior most likely to be "optimized" away by accident** |
| **UT-CONN-005** | Handshake failure (HTTP 4xx / malformed frame) | C | **P0** | Server returns garbage | Comprehensible exception; no leaked socket | ✅ | xunit | same |
| **UT-CONN-006** | Service unreachable | C | **P0** | Point at a closed port | Fails fast; UI can retry | ✅ | xunit | `ConnectionTester`, `EndpointResolver` |
| **UT-CONN-007** | User-initiated disconnect | C | P1 | Dispose | `ConnectionLost` is **not** raised (deliberate vs. abnormal must differ) | ✅ | xunit | `AgentConnectionService.DisposeAsync:543` |
| **UT-CONN-008** | Dispose is idempotent | C | P1 | `DisposeAsync()` ×3 | No exception | ✅ | xunit | same |
| **UT-CONN-009** | Rapid repeated Connect creates one socket | C | **P0** | Call `GetOrCreate` concurrently 20× | Exactly one connection instance and one factory invocation | ✅ implemented | xunit | `AgentConnectionRegistryTests.GetOrCreate_ConcurrentCallsForSameConversation_CreateOneConnection` |
| **UT-CONN-010** | Per-conversation connection isolation | C | **P0** | Create two conversation connections; remove one | Independent instances; removing one leaves the other registered | ✅ implemented | xunit | `AgentConnectionRegistryTests` |
| **UT-CONN-011** | Idle socket eviction | C | P1 | Create more than five idle connections with a controlled clock | Least-recently-used idle sockets are released; busy sockets are spared | ✅ implemented | xunit | `AgentConnectionRegistryTests.TrimIdleAsync_*` |
| **UT-CONN-012** | Switch agent while connecting | C | P1 | Navigate away mid-connect | Old connection cleaned up; no leak | ✅ | xunit | `ChatViewModel.Cleanup:63` |
| **E2E-CONN-001** | Automatic reconnect after an abnormal drop | E | **P0** | See E2E-005 | — | ✅ | FlaUI + loopback WebSocket | `ShellSmokeTests.Chat_DroppedConnection_ReconnectsAndCompletesTheTurn` |

### 11.4 Database and persistence (DB)

| ID | Title | Layer | Pri | Steps | Expected | Auto | Framework | Code location |
|---|---|---|---|---|---|---|---|---|
| **DB-SCHEMA-001** | Schema creation is idempotent | D | **P0** | Init ×3 | No error; schema unchanged | ✅ | xunit | `CreateSchemaAsync` |
| **DB-MIGRATION-001** | **An old DB (v0 snapshot) opens under the current build** | D | **P0** | Initialize current code against the legacy schema fixture | No crash, no data loss; `user_version` advances | ✅ | xunit | `SchemaMigrationTests` exercises the real `SchemaMigrator` against a temporary SQLite file. |
| **DB-MIGRATION-001A** | **A pre-`messages` envelope database preserves transcript history** | D | **P0** | Initialize against the historical `conversations.envelope_json` schema | Messages import to normalized rows, FTS is populated, legacy blobs remain, and all changes commit atomically | ✅ | xunit | `AppDatabaseSchemaTests.ApplyAsync_PreMessagesDatabase_ImportsConversationEnvelopeAndRepairsBaselineColumns` |
| **DB-MIGRATION-002** | A DB missing a column fails loudly | D | P1 | Hand-craft a DB with a column removed | Explicit error, not silent data loss | ✅ | xunit | same |
| **DB-MIGRATION-003** | A v5 DB gains `agents.icon_path` without disturbing its agents | D | **P0** | Stamp a baseline DB at `user_version = 5` with an existing agent, then migrate | `user_version` reaches the latest; the column exists; the existing agent's `icon_path` is NULL (no custom icon is the normal upgrade state) | ✅ | xunit | `AppDatabaseSchemaTests.ApplyAsync_VersionFiveDatabase_…` |
| **DB-ICON-001** | `icon_path` round-trips through the agent repository | D | P1 | Save an agent carrying an icon path, reload | The relative path comes back verbatim; blank normalizes to NULL so "no icon" has one representation | ✅ | xunit | `AgentRepositoryTests` |
| **DB-ICON-002** | **A stored icon path cannot escape the managed directory** | D | **P0** | Resolve `../connectonion.db`, `avatars/../../secrets.png`, a non-leaf filename | Rejected; the display path reports failure instead of throwing, and falls back to the initial avatar | ✅ | xunit | `AppStorageTests` — **`icon_path` is hand-editable, and the same resolution guards the delete paths** |
| **DB-MSG-001** | Incremental upsert touches only the given rows | D | **P0** | 100 existing rows, upsert 3 | Only 3 rows written; others' `created_at` unchanged | ✅ | xunit | `ConversationRepository.UpsertMessagesAsync` |
| **DB-MSG-002** | `created_at` survives an update | D | **P0** | Upsert an existing id | `created_at` not overwritten (it is absent from the DO UPDATE list) | ✅ | xunit | same |
| **DB-MSG-003** | Message ordering is stable on `(conversation_id, id)` | D | **P0** | Upsert out of order | Read-back order is correct | ✅ | xunit | same |
| **DB-MSG-004** | Attachments replaced wholesale per `message_id` | D | **P0** | Agent bubble gains an image mid-turn | Old attachment rows deleted, new inserted, no duplicates | ✅ | xunit | same |
| **DB-MSG-005** | **Base64 payloads are never persisted** | D | **P0** | Receive `agent_image` | `message_attachments` holds only a local path + metadata | ✅ | xunit | `AttachmentImageCacheService` — **security + database-size risk** |
| **DB-MSG-006** | `GetNextMessageIdAsync` = MAX(id)+1 | D | P1 | Empty conversation / 100 rows | Returns 1 / 101 | ✅ | xunit | `ConversationRepository` |
| **DB-MSG-007** | `LoadLastAgentMessageAsync` returns the last agent row | D | P1 | Mixed roles | Correct row | ✅ | xunit | same |
| **DB-CONC-001** | Concurrent writes do not deadlock (WAL) | D | **P0** | 10 concurrent upserts | All succeed; no `database is locked` | ✅ | xunit | `AppDatabase` (WAL + shared cache) |
| **DB-FK-001** | Deleting a conversation leaves no orphan rows | D | **P0** | Delete a conversation | Attachments, messages, traces and executions are all gone; no orphans | ✅ | xunit | **There is no `ON DELETE CASCADE`.** Children are deleted explicitly, in order, before the `sessions` row (`ConversationRepository.DeleteConversationAsync` → `DeleteExecutionsAndTracesAsync` → `SessionRepository.SaveAsync`). Assert the *ordering contract*: doing it in the wrong order raises an FK violation that the repository `try/catch` swallows into a log line |
| **DB-USAGE-001** | Deleting a conversation does **not** erase its usage | D | **P0** | Record usage, then delete the conversation | `usage_events` rows survive; per-model totals are unchanged | ✅ | xunit | `usage_events` has no FK and is in no cascade — 🔴 **the ledger invariant: totals must never shrink because a user tidied their sidebar** |
| **DB-USAGE-002** | Usage insert is idempotent | D | **P0** | Persist the same run twice (retry / replay) | No double counting (`ON CONFLICT(id)` — the id is the server's event id) | ✅ | xunit | `UsageRepository.InsertAsync` |
| **DB-USAGE-003** | A failed / cancelled run still records its usage | C | **P0** | Run fails mid-turn after 2 LLM calls | Both calls appear in the ledger | ✅ | xunit | `AgentSessionManager.PersistFailedAsync` → `PersistUsageAsync` — a ledger that only counts successes does not add up |
| **UT-USAGE-001** | `UsageProjector` extracts one row per `llm_result` | A | **P0** | Turn using two models | Two rows, correct model attribution | ✅ | xunit | `UsageProjector.Extract` |
| **UT-USAGE-002** | Malformed / model-less / zero-token events are skipped | A | P1 | Bad frames | No rows, no exception | ✅ | xunit | same |
| **UT-USAGE-003** | Clear is the only deletion path | A | **P0** | — | No other code path deletes `usage_events` | ✅ | code review + grep | `UsageRepository.ClearAsync` |
| **DB-CORRUPT-001** | Corrupt database file | D | P1 | Write garbage bytes | Comprehensible error; **must not silently wipe the DB** | ✅ | xunit | `AppDatabase.OpenAsync` |
| **DB-ID-001** | Identity seed is DPAPI-encrypted at rest | D | **P0** | Generate identity | `private_seed` is not plaintext; round-trips | ✅ | xunit | `IdentityStore` |
| **DB-ID-002** | Pre-DPAPI plaintext identity migrates in place | D | **P0** | Seed a matching address + 64-hex seed, then load | Address is unchanged; seed is rewritten with CurrentUser DPAPI; no reset is reported | ✅ | xunit | `IdentityStoreTests.EnsureIdentity_PreDpapiPlaintextSeed_IsProtectedWithoutChangingAddress` |
| **DB-ID-003** | Unreadable stored seed resets identity with an explicit warning | D | **P0** | Tamper with the seed | A fresh identity is generated; `WasReset`/`ResetReason` latch, `IdentityReset` fires, and the shell warns that agents must re-authorize | ✅ | xunit + contract | `IdentityStoreTests` and shell identity-reset notification contract |
| **DB-PREF-001** | Preferences round-trip (single row, `CHECK id = 1`) | D | P2 | Write twice | Still exactly one row | ✅ | xunit | `PreferencesRepository` |

### 11.5 Chat and sending (CHAT)

| ID | Title | Layer | Pri | Data | Expected | Auto | Framework | Code location |
|---|---|---|---|---|---|---|---|---|
| **UT-CHAT-001** | Empty message is not sent | A | **P0** | `""` | `SendMessageAsync` not called; no bubble | ✅ | xunit | `ChatViewModel.SendAsync:292` |
| **UT-CHAT-002** | Whitespace-only message is not sent | A | **P0** | `"   \t\n"` | Same | ✅ | xunit | same |
| **UT-CHAT-003** | `CanSend` gate | A | **P0** | All combinations of `HasAgent` / `IsOnline` / `IsProcessing` / `IsConnecting` | Full truth table (4 inputs → 16 rows) | ✅ | xunit | `ChatViewModel.CanSend:112` |
| **UT-CHAT-004** | Send is rejected while processing | A | **P0** | `IsProcessing = true` | Second send ignored | ✅ | xunit | same |
| **UT-CHAT-005** | Rapid repeated send guard | C | **P0** | 10 clicks in 100ms | Exactly one run | ✅ | xunit | `AgentSessionManager.SendMessageAsync:79` |
| **UT-CHAT-006** | Send while disconnected | A | **P0** | Offline | Rejected with a message; no exception | ✅ | xunit | `CanSend` |
| **UT-CHAT-007** | User message appears instantly and persists | C | **P0** | Normal send | Bubble is immediate; user row written before the reply | ✅ | xunit | `AgentSessionManager` (persists the user message on send) |
| **UT-CHAT-008** | Unicode / Chinese / emoji / multiline / very long | A | P1 | See test data | Preserved verbatim; not truncated or mangled | ✅ | xunit | `InputMessageBuilder` |
| **UT-CHAT-009** | `CanRetry` / `ShowRetryBar` logic | A | P1 | After a failure | Retry bar appears and is mutually exclusive with the offline notice | ✅ | xunit | `ChatViewModel:119,133` |
| **UI-CHAT-001** | Composer clears and keeps focus after send | B | P1 | — | Focus remains in the input | ✅ | FlaUI against trimmed app | `Chat_SendMessage_RendersUserAndAgentBubbles`; `NewChat_AgentDetailFirstSend_NavigatesToChatAndCompletesTurn` |
| **UI-CHAT-002** | Disclosure bubbles animate open and closed | B | P1 | Thought process, interactive cards, tool activity and tool steps | 140 ms fade/vertical transition; header is guarded against repeat clicks; OS animation preference is respected | ✅ contract + manual appearance | xUnit contract / WinUI | `TestPlanShellContractTests.DisclosureAnimation_IsBoundedGuardedAndHonoursTheOsPreference` |

### 11.6 Streaming and projection (STREAM)

| ID | Title | Layer | Pri | Expected | Auto | Framework | Code location |
|---|---|---|---|---|---|---|---|
| **UT-STREAM-001** | thinking / tool / assistant events project to the right bubbles | A | **P0** | `EventKind` maps 1:1 to a template; Thinking is collapsed and loading until assistant output, then reaches a terminal state on success/failure/cancel | ✅ | xunit | `ChatTurnProjection.Apply`, `ChatMessageTests` |
| **UT-STREAM-002** | **Live and headless paths produce identical bubbles** | A | **P0** | Same event sequence through an `ObservableCollection` target and a plain `List` target yields **identical** output | ✅ | xunit | `ChatTurnProjection` — 🔴 **the core invariant that prevents double-rendering when a page opens mid-turn** |
| **UT-STREAM-003** | Final reply is de-duplicated | A | **P0** | `AppendFinalReply` adds nothing when it equals the last assistant bubble | ✅ | xunit | `ChatTurnProjection.AppendFinalReply` |
| **UT-STREAM-004** | Repeated `event_key` merges instead of appending | A | **P0** | running → done updates the same row | ✅ | xunit | `ChatTurnProjection` |
| **UT-STREAM-005** | Unknown event kinds are safely ignored | A | P1 | No crash, no blank bubble | ✅ | xunit | same |
| **UT-STREAM-006** | Out-of-order / empty-payload events | A | P1 | No exception | ✅ | xunit | same |
| **UT-STREAM-007** | Cancelled run ends in Cancelled state | C | **P0** | `CompleteToolActivity(Cancelled)`; persisted status correct | ✅ | xunit | `AgentSessionManager.PersistFailedAsync:260` |
| **UT-STREAM-008** | Failed run keeps generated content and appends an error bubble | C | **P0** | `[connection error] …` bubble present; earlier bubbles retained | ✅ | xunit | `PersistFailedAsync:278-287` |
| **UT-STREAM-009** | **Run survives page close and still persists** | C | **P0** | With no page subscribed, the run completes and history is correct | ✅ | xunit | `AgentSessionManager` + `ConversationRunRegistry` — 🔴 core architectural promise |
| **UT-STREAM-010** | Page opened mid-turn: load history, replay live events, no double render | C | **P0** | Bubble count is correct | ✅ | xunit | `MarkHistoryPersisted` / `IsRunHistoryPersisted:155` |
| **UT-STREAM-011** | Interactive card answers persist (approve / reject) | C | **P0** | `EventMeta` = "Approved once" / "Rejected"; status correct | ✅ | xunit | `RecordInteractiveAnswer:73` + `ResolveInteractiveCards:357` |
| **UT-STREAM-012** | Unanswered interactive cards are sealed as "No selection" | C | **P0** | Reloaded history shows "No selection", not dead controls | ✅ | xunit | `NoSelectionMeta` (`AgentSessionManager:50`) |
| **UT-STREAM-013** | Interactive card default collapse state | A | P1 | Resolved → collapsed; awaiting → expanded | ✅ | xunit | `ChatMessage.IsInteractiveExpanded` |
| **UT-STREAM-014** | 1000 events in one turn, nothing dropped | C | P1 | All projected, in order | ✅ | xunit | `ConversationRun` |
| **UT-STREAM-015** | Graceful stop uses the latest wire protocol | B | **P0** | Existing socket receives exactly `{ "type": "INTERRUPT" }`; ordinary OUTPUT still completes the turn | ✅ | xunit | `AgentConnectionServiceTests.SendInterruptAsync_*` |
| **UT-STREAM-016** | Stop during approval uses the FIFO-safe hard rejection | B | **P0** | Exactly `approved=false`, `mode=reject_hard`, `feedback=用户中断`; no `type`/`scope` fields | ✅ | xunit | `AgentConnectionServiceTests.RejectApprovalForStopAsync_*` |
| **UT-STREAM-017** | Interrupted OUTPUT is marked without an extra activity bubble | A | **P0** | Final agent message has `EventMeta=Stopped`; failed interrupt send leaves it unmarked | ✅ | xunit | `ChatTurnProjectionTests.AppendCompletedTurn_AfterInterrupt*` |
| **UI-STREAM-001** | Stop enters an idempotent disabled intermediate state | B | **P0** | Button is visible only after INPUT, disabled while `IsStopping`, and disappears on OUTPUT/ERROR/disconnect | ✅ | FlaUI against trimmed app | `Chat_StopResponse_DisablesImmediatelyAndSettlesExactlyOnce` |

### 11.7 Conversation management (SESSION)

| ID | Title | Layer | Pri | Expected | Auto | Code location |
|---|---|---|---|---|---|---|
| **DB-SESSION-001** | Create / rename / delete a conversation | D | **P0** | Persists correctly | ✅ | `SessionRepository` |
| **DB-SESSION-002** | List ordered by `updated_at DESC` | D | P1 | Index `ix_sessions_agent_updated` is used | ✅ | same |
| **UT-SESSION-003** | Delete a conversation that is mid-reply | C | **P0** | Run cancelled, cache invalidated, no residual socket | ✅ | `AgentSessionManager.ReleaseConversationAsync:180` |
| **UT-SESSION-004** | `ConversationCache` caches only idle conversations | A | **P0** | A conversation with an active run **never** reads from cache (it must load authoritative history) | ✅ | `ConversationCache` — 🔴 violating this shows stale history |
| **UT-SESSION-005** | LRU cap of 4; invalidated on delete | A | P1 | The 5th evicts the oldest; deleting a session/agent clears it | ✅ | `ConversationCache` |
| **UT-SESSION-006** | Conversations are isolated per agent | D | P1 | Not visible across agents | ✅ | `SessionRepository` |
| **E2E-SESSION-001** | History restored after restart | E | **P0** | See E2E-006 | ✅ | FlaUI `Chat_ProcessRestart_RestoresSentTurnFromSQLite` |
| **E2E-SESSION-002** | Switch away during a turn and return | E | **P0** | Exactly one persisted/rendered reply | ✅ | FlaUI `Chat_SwitchConversationDuringTurn_ReturnsToOnePersistedReply` |

### 11.8 Markdown rendering (MD)

| ID | Title | Layer | Pri | Expected | Auto | Code location |
|---|---|---|---|---|---|---|
| **UI-MD-001** | Full syntax renders (headings, bold/italic, strikethrough, lists, quotes, tables, rules) | B | P1 | Correct element types | ✅ contract + real-window render | `MarkdownRendererCoverageTests`; `Chat_FindAcrossMarkdownCodeLinksAndUnclosedFence_DoesNotCrash` |
| **UI-MD-002** | Unclosed code fence does not crash | B | **P0** | Degrades gracefully | ✅ | FlaUI `Chat_FindAcrossMarkdownCodeLinksAndUnclosedFence_DoesNotCrash` |
| **UI-MD-003** | **Search highlight inside inline code does not crash** | B | **P0** | No `ArgumentException` | ✅ regression | FlaUI `Chat_FindAcrossMarkdownCodeLinksAndUnclosedFence_DoesNotCrash` |
| **UI-MD-004** | Search highlight inside a fenced code block does not crash | B | **P0** | Same | ✅ regression | same |
| **UI-MD-005** | Search highlight inside a link / bare URL does not crash | B | **P0** | Falls back to the bold+underline path | ✅ regression | same |
| **UI-MD-006** | `` [`code`](url) `` does not crash | B | P1 | Inline code degrades to a monospace `Span` | ✅ regression | same + `MarkdownRendererCoverageTests` |
| **UI-MD-007** | Highlight match count equals the number of highlights | A | **P0** | The fallback path must still increment `matchIndex`, or Ctrl+F's "n of m" desyncs | ✅ | `HighlightedTextBlock.AddHighlightedRuns` |
| **UI-MD-008** | Code blocks and highlights sit on the text baseline | F | P2 | No upward float | ⚠️ partial | `HighlightedTextBlock.DescentFor` derives font metrics; final appearance remains visual |
| **UI-MD-009** | Correct colors in both themes | B | P1 | Shared brushes are cached by key and invalidated on `ThemeApplied`; no stale colors after a theme flip | ✅ contract + manual | `ThemeBrushResolver`, `RenderHotPathContractTests` |
| **UI-MD-010** | Table presentation and alignment | B | P1 | Cells with default/repeated parser column indices advance sequentially without overlap; header has distinct surface/weight; `:---`, `:---:`, `---:` align left/center/right; long cells wrap at a bounded width | ✅ structural contract + manual visual | `MarkdownRendererCoverageTests.TableLayout_AdvancesColumnsAlignsCellsAndBoundsLongContent` |
| **UI-MD-011** | Wide table scrolling does not break message-list scrolling | B | **P0** | Table owns horizontal overflow only; mouse wheel and touch continue to scroll the outer virtualized message list vertically | ✅ horizontal-only contract + manual wheel/touch | same (`HorizontalScrollBarVisibility`; vertical disabled) |
| **UI-MD-012** | Fenced code language header | B | P2 | A language info string renders as a compact themed header; blocks without a language keep the simpler layout | ✅ structural contract + manual visual | `MarkdownRendererCoverageTests.FencedCodeLanguageHeader_IsConditionalAndThemed` |
| **PERF-MD-001** | Large markdown (10k lines) render time | E | P2 | < 500ms | ✅ | performance test |

### 11.9 Tool calls (TOOL)

| ID | Title | Layer | Pri | Expected | Auto | Code location |
|---|---|---|---|---|---|---|
| **UT-TOOL-001** | start / running / success / failure / cancel state machine | A | **P0** | `ToolActivityStatus` transitions are correct | ✅ | `ToolActivityProjector` |
| **UT-TOOL-002** | Consecutive tool calls collapse into one timeline | A | P1 | One `tool_activity` bubble with multiple steps | ✅ | same |
| **UT-TOOL-003** | Failed migration rows fall back to the Empty template | A | P1 | No crash; renders a blank row | ✅ | `ToolActivityMigration` + `ChatMessageTemplateSelector.Empty` |
| **UT-TOOL-004** | `EventKind` → template mapping | A | **P0** | user / agent / activity / tool_activity / ask_user / approval / plan_review / other → Empty | ✅ | `ChatMessageTemplateSelector.SelectTemplateCore` |
| **UT-TOOL-005** | **Template is resolved exactly once per container** | A | **P0** | `Role` / `EventKind` / `ToolActivity` must not change after a message enters the list | ✅ | `ChatMessageTemplateSelector` (its class comment is the contract) — 🔴 violating it locks in the wrong template forever |

### 11.10 Overlays: Settings / About / Keyboard Shortcuts (OVL)

| ID | Title | Layer | Pri | Expected | Auto | Code location |
|---|---|---|---|---|---|---|
| **UI-OVL-001** | All five overlays close on Esc | B | **P0** | `OverlayRoot_KeyDown` handles Escape | ✅ | FlaUI theory `ModalOverlay_EscapeClosesAndReturnsFocusToOpener`; `OverlayInteractionContractTests` |
| **UI-OVL-002** | Backdrop click closes; card click does not | B | P1 | `Backdrop_Tapped` vs. `ModalContainer_Tapped` (`e.Handled = true`) | ✅ contract | `OverlayInteractionContractTests.Overlay_EscapeAndBackdropClose_WhileCardTapStaysInside` |
| **UI-OVL-003** | Re-invoking never opens a second instance | B | **P0** | When `IsOpen`, `Show()` only re-focuses | ✅ contract | `OverlayInteractionContractTests.Shell_CreatesOneInstancePerOverlay_AndCyclesFocusInsideIt` |
| **UI-OVL-004** | Focus returns to the opener on close | B | **P0** | Focus lands back on the command that opened the overlay | ✅ | five-case FlaUI theory + source contract |
| **UI-OVL-005** | Global accelerators are inert while an overlay is open | B | **P0** | `IsModalOverlayOpen` short-circuits the File/Edit/View/Help KeyDown handlers | ✅ contract | `OverlayInteractionContractTests.EveryGlobalAcceleratorHandler_StopsBehindAModalOverlay` |
| **UI-OVL-006** | Opening an overlay does not disturb background runs | C | **P0** | Streaming continues; connections stay up | ✅ | architectural assertion (overlays are a pure UI layer) |
| **UI-ABOUT-001** | Shows app name, description, icon, copyright | B | P1 | Copy is correct and says "ConnectOnion" | ✅ contract + real-window open | `TestPlanShellContractTests.AboutOverlay_HasTheRequiredProductCopyAndIcon`; `HelpAbout_OpensFocusedAndOkClosesIt` |
| **UT-ABOUT-002** | **Version is not hard-coded** | A | **P0** | Packaged → `Package.Current.Id.Version`; unpackaged → `AssemblyInformationalVersion` with any `+sha` stripped; fallback `1.0.0` | ✅ | xunit · `AppVersionService.ResolveDisplayVersion` |
| **UT-ABOUT-003** | Copyright year is taken from the clock | A | P2 | `© {DateTime.Now.Year} ConnectOnion` | ✅ | `AppVersionService.CopyrightText` |
| **UI-ABOUT-004** | High DPI (150% / 200%): text is not clipped | F | P2 | Layout adapts | ❌ manual | `AboutOverlay.xaml` (ScrollViewer backstop) |
| **UT-HELP-001** | Help menu contains Keyboard shortcuts / ConnectOnion Docs / About | B | P1 | All three present, in order | ✅ | `HelpMenuContractTests.HelpMenu_ContainsExpectedCommandsInOrder` |
| **UT-HELP-002** | Docs opens the correct URL | A | **P0** | `https://docs.connectonion.com/` | ✅ | `HelpMenuContractTests` canonical-URL contract |
| **UT-HELP-003** | A failed Docs launch surfaces an error toast (never silent) | A | P1 | `NotificationType.Error` toast | ✅ | `HelpMenuContractTests.HelpMenuCode_FailedDocsLaunchSurfacesAnErrorToast` |
| **UT-HELP-004** | Docs and About are separate commands | A | P2 | Two independent handlers; no shared event branching on text | ✅ code review | `Shell/MainWindow.HelpMenu.cs` |
| **UT-KBD-001** | The shortcut catalog matches real handlers | A | **P0** | Every entry in `KeyboardShortcutCatalog` has a corresponding KeyDown handler in `MainWindow.*Menu.cs` | ✅ | xunit (reflection / string comparison) — the file's own comment demands this |
| **UT-KBD-002** | Ctrl+Shift+/ opens the shortcuts window | B | P1 | A key that produces `/` in the active layout + Ctrl + Shift | ✅ contract + real window | `LayoutKeys.ProducesSlash`, `MainWindow.HelpMenu.cs`; VK 191 is fallback only |
| **UI-KBD-003** | Shortcut search filters | A | P1 | Filters by keyword; empty state when no results | ✅ | `KeyboardShortcutsViewModel.IsEmpty` |
| **UI-SET-001** | Settings persist across restart | D | **P0** | Theme / font size / sidebar written to `preferences` | ✅ | `PreferencesRepository` + `SettingsViewModel` |
| **UI-SET-002** | Theme switch applies immediately with no stale colors | B | P1 | Theme-bound XAML updates and code-resolved brushes invalidate on `ThemeApplied` | ✅ contract + real window | `ThemeService`, `ThemeBrushResolver`, `Brushes.xaml` |

### 11.11 Notifications (NOTIF)

| ID | Title | Layer | Pri | Expected | Auto | Code location |
|---|---|---|---|---|---|---|
| **UT-NOTIF-001** | Globally disabled → Suppress | A | **P0** | `Suppress("notifications disabled")` | ✅ | `NotificationPolicy.Decide:19` |
| **UT-NOTIF-002** | Type disabled → Suppress | A | **P0** | `Suppress("type disabled")` | ✅ | `:22` |
| **UT-NOTIF-003** | Already viewing that conversation → Suppress | A | **P0** | `Suppress("viewing target conversation")` | ✅ | `:33` |
| **UT-NOTIF-004** | Already viewing the approval → Suppress | A | **P0** | `Suppress("viewing approval")` | ✅ | `:29` |
| **UT-NOTIF-005** | Foreground but on another view → in-app toast | A | **P0** | `NotificationChannel.InApp` | ✅ | `:41` |
| **UT-NOTIF-006** | Background → system notification | A | **P0** | `NotificationChannel.System` | ✅ | `:44` |
| **UT-NOTIF-007** | Dedup cache suppresses repeats | A | P1 | The same run notifies once | ✅ | `DedupCache` |
| **UT-NOTIF-008** | Click activates the window and opens the right conversation | C | **P0** | `IChatWindow.ShowConversationAsync(agentId, conversationId)` invoked | ✅ | `ConversationNavigationService` + `Shell/MainWindow.Notifications.cs` |
| **UT-NOTIF-009** | Target conversation was deleted → falls back to HomePage | C | P1 | No crash | ✅ | `MainWindow.ShowConversationAsync:47` |
| **UT-NOTIF-010** | Cold-start notification click | C | **P0** | `HandleColdStart(arguments)` buffers until the window is ready | ✅ | Headless router tests + FlaUI `NotificationActivation_ColdStart_OpensTargetConversation`; OS delivery/click remains manual |
| **UT-NOTIF-011** | Notification registration failure does not block startup | C | P1 | `NotificationLog.Warn` records it; the app continues | ✅ | `App.HandleColdStartActivation` try/catch |
| **UT-NOTIF-012** | Invalid notification arguments | A | P1 | Ignored, no crash | ✅ | `NotificationActivationRouter` |
| **F-NOTIF-013** | Focus Assist / notifications denied at OS level | F | P2 | App does not crash; the in-app channel still works | ❌ manual | — |

### 11.12 Window and navigation (NAV)

| ID | Title | Layer | Pri | Expected | Auto | Code location |
|---|---|---|---|---|---|---|
| **UI-NAV-001** | Back/Forward enablement after navigation | B | P1 | `UpdateNavigationButtons` | ✅ contract + real window | `TestPlanShellContractTests.BackForwardButtonsTrackFrameState_AndNavigationClosesFind`; `Navigation_BackForwardRestoresEntities_ClosesFind_AndSurvivesRapidUse` |
| **UI-NAV-002** | Navigation closes the Find overlay | B | P2 | `CloseFindOverlay()` | ✅ contract + real window | same |
| **UI-NAV-003** | Forced navigation preserves Back/Forward history while replacing the outgoing entry's entity context | A | **P0** | Stored agent/conversation selection is restored; deleted entities are pruned; destructive resets alone clear both stacks | ✅ contract + real window | `NavigationHistoryContractTests`; `Navigation_BackForwardRestoresEntities_ClosesFind_AndSurvivesRapidUse` |
| **UI-NAV-004** | Navigation does not cancel an in-flight run | C | **P0** | The run continues | ✅ | architectural assertion |
| **UI-NAV-005** | Rapid repeated navigation does not crash | E | P1 | 10 fast page switches | ✅ | FlaUI `Navigation_BackForwardRestoresEntities_ClosesFind_AndSurvivesRapidUse` |
| **UI-NAV-006** | Custom title bar drag region works | F | P2 | The window can be dragged | ❌ manual | `TitleBarDragRegion` |
| **UI-NAV-007** | Multi-monitor / DPI change | F | P2 | Layout stays correct | ❌ manual | — |
| **UI-FIND-001** | Ctrl+F opens Find; `IFindHost` pages respond | B | P1 | Match count correct; next/prev works; navigation closes it | ✅ | FlaUI `Chat_Find_NavigatesMatchesAndCloses`; navigation regression above |

### 11.13 Keyboard and focus (FOCUS)

| ID | Title | Layer | Pri | Expected | Auto |
|---|---|---|---|---|---|
| **A11Y-FOCUS-001** | Initial focus on overlay open | B | **P0** | Settings → first control; Shortcuts → SearchBox; About → OkButton | ✅ FlaUI shell suite |
| **A11Y-FOCUS-002** | Focus returns to the opener on close | B | **P0** | See UI-OVL-004 | ✅ five-case FlaUI theory |
| **A11Y-FOCUS-003** | Tab / Shift+Tab order is sane and skips hidden controls | B | P1 | Collapsed regions are not tabbable; modal background leaves the Tab order; focus cycles inside the overlay | ✅ contract + manual traversal | `OverlayInteractionContractTests.Shell_CreatesOneInstancePerOverlay_AndCyclesFocusInsideIt` |
| **A11Y-FOCUS-004** | Disabled controls do not take focus | B | P1 | Test/Add remain disabled until their validated states permit action | ✅ contract | `OverlayInteractionContractTests.AddAgent_DisabledActionsFollowTheValidatedViewModelState`; `AddAgentViewModelTests` |
| **A11Y-FOCUS-005** | Focus visuals are visible (including restyled buttons) | F | P1 | `FocusVisualPrimaryBrush` applies | ❌ manual |
| **E2E-015** | **Full send-message flow using only the keyboard** | E | **P0** | See §12 | ✅ FlaUI `NewChat_AgentDetailFirstSend_NavigatesToChatAndCompletesTurn` |

### 11.14 Accessibility (A11Y)

| ID | Title | Layer | Pri | Expected | Auto | Evidence |
|---|---|---|---|---|---|---|
| **A11Y-ID-001** | **Named interactive controls carry an `AutomationId`** | B | **P0** | Per the table in §13.1 | ✅ automated | 158 IDs are unique; the 53 critical E2E locators and every named interactive XAML element are guarded by `AutomationContractTests` |
| **A11Y-NAME-001** | Icon-only buttons carry `AutomationProperties.Name` | B | **P0** | Every icon-only button has an accessible name | ✅ static scan | `AutomationContractTests` |
| **A11Y-NAME-002** | Interactive card headers announce their state | B | P1 | `InteractiveAccessibilityName` includes eyebrow, title, outcome, expanded/collapsed and raises a change notification | ✅ | `ChatMessageTests.InteractiveAccessibilityName_AnnouncesDisclosureStateAndUpdatesWhenToggled` |
| **A11Y-NAME-003** | Keycaps are not double-announced | B | P2 | Keycaps are `AccessibilityView=Raw`; the group name carries the full combo | ✅ contract | `HelpMenuContractTests.ShortcutKeycaps_AreRawAndTheGroupAnnouncesTheWholeChord` |
| **A11Y-COLOR-001** | Color is not the only carrier of meaning | F | P1 | Approve/reject also show visible text | ✅ contract | `HighContrastContractTests.ApprovalDecisions_UseVisibleTextInAdditionToColorAndIcons` |
| **A11Y-CONTRAST-001** | Text contrast ≥ WCAG AA | F | P1 | Critical foreground/background pairs in both themes meet 4.5:1 | ✅ numeric contract + manual Accessibility Insights | `ThemeContrastContractTests`; light warning/attention color corrected to `#9A6700` |
| **A11Y-HC-001** | High-contrast mode is usable | F | P2 | — | ❌ manual | — |
| **A11Y-SCALE-001** | 200% text scaling does not clip | F | P1 | — | ❌ manual | — |
| **A11Y-LIVE-001** | State changes are announced | B | P2 | Connection status, error messages | ✅ contract | `HighContrastContractTests` live-region assertions |

### 11.15 Error handling (ERR)

| ID | Title | Layer | Pri | Expected | Auto | Evidence |
|---|---|---|---|---|---|---|
| **UT-ERR-001** | Network errors are shown and recoverable | C | **P0** | Error bubble + retry bar; UI stays interactive | ✅ | headless + FlaUI `Chat_AgentErrorThenRetry_CompletesOnTheNextInput` |
| **UT-ERR-002** | WebSocket exceptions are not swallowed | C | **P0** | `ConnectionLost` fires | ✅ | — |
| **UT-ERR-003** | Database errors are not silent | D | **P0** | Explicit exception | ✅ | — |
| **UT-ERR-004** | Markdown render errors degrade | B | **P0** | Fallback paragraph (`RenderMarkdown`) | ✅ contract + real-window malformed markdown | `MarkdownRendererCoverageTests`; `Chat_FindAcrossMarkdownCodeLinksAndUnclosedFence_DoesNotCrash` |
| **UT-ERR-005** | External-link failure is surfaced | A | P1 | See UT-HELP-003 | ✅ | `HelpMenuContractTests.HelpMenuCode_FailedDocsLaunchSurfacesAnErrorToast` |
| **UT-ERR-006** | Errors leak no sensitive data | A | **P0** | No private seed, invite code, or full token in any message | ✅ | assertions + code review — 🔴 security |
| **UT-ERR-007** | One bad image does not fail the whole persist | C | P1 | `Task.WhenAll(PendingImages)` is wrapped in try/catch | ✅ | `AgentSessionManager:228` |

---

## 12. End-to-End Flows

> Conventions: all E2E use **FlaUI** and locate app-owned elements by `AutomationId`; platform-generated controls may use a role/name fallback scoped inside an identified dialog. Synchronization uses bounded condition polling, never an unconditional fixed-duration sleep (§13.3).
> Scheduled CI currently uploads TRX and screenshot evidence. For diagnosis, also retain app logs, the isolated `connectonion.db`, fake-server interaction logs where applicable, and a UIA tree dump.
>
> The current suite has 36 required shell/chat tests. In addition to launch, startup-failure recovery, first-run, Settings, agent management, tray restoration, search, Shortcuts, and About, it drives page navigation/history; five modal-overlay Esc/focus-return cases; Agent Detail template/keyboard-first-send behavior; Stop/interrupt settlement; abnormal-drop resume; error/retry; mid-turn conversation switching; restart restore; markdown-aware in-chat find; agent rename persistence; cold activation routing; and agent-icon surfaces. One Explorer drag-and-drop diagnostic is retained but explicitly skipped because its source coordinates vary with desktop DPI/layout. The broader flows below remain the target catalog.
>
> Four of those smoke paths also form the reproducible README screenshot harness. Setting
> `CONNECTONION_README_SCREENSHOT_DIR` makes them capture the real 1400×900 window as
> `home.png`, `settings-general.png`, `chat.png`, and `approval-request.png`; without it, capture is
> a no-op. Each documentation run uses an isolated data root and the loopback fake agent. See
> `DEVELOPMENT.md` for the exact regeneration command.

| ID | Flow | Preconditions | Steps | Expected | Automation difficulty | Stability measures |
|---|---|---|---|---|---|---|
| **E2E-001** | First launch and add an agent | Empty `%AppData%\ConnectOnion` | Launch → `AddAgentButton` → fill `AgentAddressInput` → Test → Submit | Agent appears in `AgentList`; one row in `agents` | Connection test depends on the network | Point at the fake server; wait for the success status text before submitting |
| **E2E-002** | Connect and send a message | An agent exists | Select agent → new conversation → type → `SendMessageButton` | User bubble appears immediately; reply arrives | Connect time varies | Wait for `ConnectionStatus` = online; wait for `MessageList` count +2 |
| **E2E-003** | Receive a streamed reply | Same | Fake server emits thinking → tool → assistant → OUTPUT | Bubbles appear in order; final content persists | Event timing | Wait for the terminal state (`StopResponseButton` disappears) |
| **E2E-004** | Switch conversation mid-stream and return | A stream is in flight | Switch away → wait 3s → switch back | **No double rendering**; content complete and correctly ordered | Race between DB load and live replay | Assert bubble count == DB row count — 🔴 **the highest-value E2E in this suite** |
| **E2E-005** | Reconnect after a drop | Connected | Fake server drops → click reconnect | Offline notice → reconnect succeeds → sending works again | Drop timing | Wait on `ConnectionStatus` text transitions |
| **E2E-006** | History restored after restart | 2 conversations with messages | Close → relaunch → open a conversation | Content, order, and roles identical; interactive cards show their resolved outcome | Process restart | After relaunch, wait for `MessageList` to appear |
| **E2E-007** | Notification after a background reply | Window minimized | Send → minimize → wait for completion | System notification raised (asserted through the fake channel) | OS notifications are not directly assertable | Assert on the fake `INotificationAbstractions` |
| **E2E-008** | Notification click opens the right conversation | Same | Trigger activation | Window comes forward; navigates to that conversation | Cold and warm start are different paths | Test both; assert `ShowConversationAsync` arguments |
| **E2E-009** | Open and close Settings | — | Ctrl+, → change theme → Esc | Setting applies and persists; focus returns | — | Wait for `SettingsOverlay` visible/hidden |
| **E2E-010** | Open About | — | Help → About ConnectOnion | Shows name, version, copyright; Esc / OK / close all behave identically | — | Wait for `AboutOverlay` |
| **E2E-011** | Open Keyboard Shortcuts | — | Ctrl+Shift+/ | Window opens; search works; Esc closes | Keyboard layout | Cover the menu-click path too |
| **E2E-012** | Open the docs via ConnectOnion Docs | — | Help → ConnectOnion Docs | Default browser opens `docs.connectonion.com` | **The external browser cannot be asserted** | Use the implemented `IUriLauncher` fake for the URI assertion; keep browser launch as a manual boundary |
| **E2E-013** | Second instance activates the existing window | App running | Launch the exe again | No second window; existing one comes forward; the second process exits | Process timing | Poll the process count and window handle |
| **E2E-014** | Long conversation scrolling and virtualization | Isolated synthetic 2,000-message profile | Open and scroll to the bottom | Smooth scrolling; memory does not grow linearly with scroll | Performance assertions are flaky | Sample repeatedly, take the median |
| **E2E-015** | **Complete a send using only the keyboard** | Connected | Tab / Enter / accelerators only | The message sends; focus is visible throughout | Focus order | Assert the `AutomationId` of `FocusedElement` at each step |

---

## 13. UI Automation Standards

### 13.1 `AutomationId` implementation status

App XAML contains **158 unique IDs**. `Accessibility/AutomationContractTests.cs` verifies global uniqueness, requires an ID on every named interactive XAML element, and separately pins the **53 critical IDs** used by the current real-window suite. Repeated item-template controls use stable role IDs and are scoped through their identified list/container. Add a locator to the critical subset when a new E2E flow starts depending on it. Remaining E2E scenarios and broader accessibility semantics are still open.

| AutomationId | Location | Status |
|---|---|---|
| `MainWindow` | `MainWindow.xaml` root | ✅ |
| `AgentList` / `AgentAvatarButton` | `ShellSidebar` agent tree | ✅ |
| `AddAgentButton` / `AgentAddressInput` / `SubmitAgentButton` | Sidebar and `AddAgentForm.xaml` | ✅ |
| `AgentCapabilitiesLoadingIndicator` | `AgentDetailPage.xaml` | ✅ |
| `MessageList` | `ChatPage.xaml` message list | ✅ |
| `MessageInput` / `SendMessageButton` / `StopResponseButton` | `ChatComposer.xaml` | ✅ |
| `ConnectionStatus` | `ChatPage.xaml` connection-phase label below the composer (Connected / Reconnecting / Offline) | ✅ |
| `SettingsButton` / `SidebarResizeHandle` | shell navigation | ✅ |
| `HelpMenuButton` | `MainWindow.xaml` `HelpMenuBarItem` | ✅ |
| `AboutMenuItem` / `ConnectOnionDocsMenuItem` / `KeyboardShortcutsMenuItem` | `MainWindow.xaml` Help menu | ✅ |
| `SettingsOverlay` / `SettingsSearchBox` / `SettingsCategoryPicker` / `SettingsCloseButton` | Settings modal and its adaptive navigation | ✅ |
| `AgentsNav` / `SettingsAgentList` / `SettingsAddAgentButton` | Settings agent-management pane | ✅ |
| `AboutOverlay` / `AboutOkButton` / `AboutCloseButton` | About modal | ✅ |
| `KeyboardShortcutsOverlay` / `ShortcutsSearchBox` / `KeyboardShortcutsCloseButton` | shortcuts modal | ✅ |
| `SessionSearchButton` / `SessionSearchOverlay` / `SessionSearchBox` / `SessionSearchResults` | global chat search | ✅ |
| `EmptyStateAddAgentButton` / `EmptyStateDocsLink` | first-run home state | ✅ |
| `ApprovalModeButton` | chat composer approval-mode selector | ✅ |
| `RecoveryPhraseDialog` | first-run identity recovery surface | ✅ |

`AgentNameInput` is intentionally absent because the current add-agent form has no agent-name field. Connection-action and interactive-card locators remain to be added with the corresponding chat E2E flows.

### 13.2 Hard prohibitions

UI automation must **never** depend on:

- Absolute screen coordinates
- A fixed position in the visual tree ("the 2nd Button inside the 3rd Grid")
- Control indices
- Text content as the sole locator (copy changes; it may be localized)
- An unconditional fixed-duration sleep used as synchronization (short sleeps inside a bounded condition-polling helper are permitted)
- The current display resolution or DPI

### 13.3 Async waiting

Every wait is a **condition wait** with a timeout and a polling interval:

```csharp
// Correct
await Wait.UntilAsync(
    () => window.FindFirstDescendant(cf => cf.ByAutomationId("MessageList"))
               ?.FindAllChildren().Length == expectedCount,
    timeout: TimeSpan.FromSeconds(15),
    interval: TimeSpan.FromMilliseconds(100));

// Forbidden
Thread.Sleep(3000);
```

Permitted conditions: element appears · element becomes clickable · status text changes · message count changes · connection state changes · loading indicator disappears · `StopResponseButton` disappears (i.e. the turn ended).

---

## 14. Accessibility Testing

| Check | Method | Tool |
|---|---|---|
| AutomationId coverage | Static XAML scan + UIA tree dump | Custom script / Accessibility Insights |
| Icon-button naming | Assert every `Button` with icon-only content has `AutomationProperties.Name` | Static scan (**can be a CI gate**) |
| Reading order | Manual | Narrator |
| Keyboard reachability | E2E-015 | FlaUI |
| Contrast | Both themes | Accessibility Insights for Windows |
| 200% text scaling | Manual | Windows Settings |
| High contrast | Manual | Windows Settings |
| Color not sole carrier | Code review (approval cards already carry a text outcome) | Human |

**Recommended CI gate**: a XAML static scan that fails the build when a new icon-only button lacks `AutomationProperties.Name`. Cheap to build, high payoff.

---

## 15. Performance and Stability

Performance evidence is split between the repeatable launch benchmark in [PERFORMANCE.md](./PERFORMANCE.md), the real-window memory gate, and the [2026-07-25 pre-release audit](./PERFORMANCE_AUDIT_2026-07-25_EN.md). Absolute values are machine/build specific; budgets and trends are the release contract.

### 15.1 Launch and idle budgets

| Metric | Target | Fail threshold | Method |
|---|---:|---:|---|
| Cold start to first frame | 2,000 ms | 5,000 ms | `scripts/Measure-Performance.ps1`; first composited frame |
| Warm start to first frame | 1,200 ms | 2,500 ms | Same, median of repeated launches |
| Idle working set | 200 MB | 350 MB | External process sample after first frame |
| Idle private bytes | 180 MB | 320 MB | External process sample |
| Managed heap at first frame | 40 MB | 80 MB | In-process telemetry |
| Graceful shutdown | 1,500 ms | 4,000 ms | Valid: the benchmark's named event invokes `MainWindow.ExitApplication`; at least five graceful samples are required |

The 2026-07-25 final audit measured a **717.6 ms warm-launch median**, approximately **204.7 MB** idle Working Set and **133.6 MB** idle Private Bytes. True cold-start evidence remains statistically insufficient because each fresh-publish group had only one first sample and the OS standby list was not cleared.

### 15.2 Real-window responsiveness and memory evidence

- Twelve shell/settings/search/help smoke tests pass together against the published executable.
- The UI performance audit exercises synthetic 100-, 500-, and 2,000-message histories. The final 2,000-message first open was 442.2 ms and only 14 `ListViewItem` containers were realized, confirming visual virtualization.
- Tool Activity restoration and expansion passed after Release JSON metadata remediation; expansion measured 7.2–12.8 ms.
- The final 50-cycle four-session alternating memory test had a tail slope of 0.03 MB/cycle, a 1.2 MB Private Bytes span, handle span 27, and thread span 5— all within the configured gate.
- Task Manager memory is not expected to return to the initial launch baseline. The gate detects an unbounded tail after warm-up and corroborates it with handles/threads.

### 15.3 Reproduction

```powershell
# Launch benchmark and idle memory
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Measure-Performance.ps1

# Repeated-navigation/session memory gate
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-MemoryLeaks.ps1

# The UI audit test is opt-in and writes JSON only when configured
$env:CONNECTONION_UI_TEST_EXE = '<published ConnectOnion.WinUIClient.exe>'
$env:CONNECTONION_UI_PERF_OUT = '<output.json>'
dotnet test tests\ConnectOnion.WinUIClient.UITests\ConnectOnion.WinUIClient.UITests.csproj --configuration Release
```

### 15.4 Open performance themes

- Maintain the startup milestones and the UI audit ceilings; re-baseline only with same-machine evidence.
- Capture qualified WPR/ETW evidence for slow-frame attribution on an elevated, policy-enabled desktop runner.
- Maintain the enabled trimming gate and re-run `scripts/Invoke-TrimAudit.ps1` plus the real-window trimmed-runtime check when serialization, reflection-heavy UI dependencies, or the publish shape changes.
- Re-run packaged performance evidence only if a packaged distribution channel is restored.

## 16. Regression Strategy

1. **Every fixed P0/P1 defect gets a regression case**, ID-suffixed with the issue number — e.g. `UI-MD-003 (REG-#42)`.
2. **Known real regression points in this repo** (these have already happened; lock them down permanently):
   - `UI-MD-003/004/005` — `InlineUIContainer` is legal only inside a `RichTextBlock` paragraph (this crashed)
   - `UT-STREAM-002` — live/headless projection parity (prevents double rendering)
   - `UT-SESSION-004` — conversations with an active run must bypass the cache
   - `UT-CONN-004` — the silence watchdog must not fire during `ask_user`
   - `UT-TOOL-005` — the template selector resolves once per container
   - XAML traps already documented in CLAUDE.md: `x:Bind` DataTemplate types must not use `required` / `init`; a `ResourceDictionary` cannot be shared across elements
3. **Regression suite = all P0 + everything tagged REG**, run on every PR.
4. Any wire-format change must update both `ConnectOnion.Protocol.Tests` and the Conformance reference.

---

## 17. Defect Severity Levels

| Level | Definition | Example | SLA |
|---|---|---|---|
| **S1 Blocker** | Data loss/corruption, crash, cannot send messages, cannot launch | Persist overwrites user history; app crashes on start | Fix immediately; blocks release |
| **S2 Critical** | Core flow impaired, workaround exists | Reconnect requires an app restart | Must fix before release |
| **S3 Major** | Non-core functionality broken | Shortcut search shows no empty state | Scheduled |
| **S4 Minor** | Cosmetic / polish | Code block sits 2px off the baseline | Opportunistic |

---

## 18. Test Pass Criteria

All of the following must hold before release:

- [ ] **All P0 tests pass** — no exceptions, no waivers
- [ ] **No S1 or S2 defects** open
- [ ] Core user flows pass: E2E-001 … E2E-006
- [ ] **All persistence tests pass** (every `DB-*`, especially `DB-MSG-001/002/004`)
- [ ] **Connection recovery passes** (`UT-CONN-002/003/004/005` + E2E-005)
- [ ] Critical UI automation paths pass (E2E-002/003/004/006/015)
- [ ] Accessibility blockers cleared (`AutomationId` coverage; 100% of icon buttons named)
- [ ] No evident resource leaks (`STAB-LEAK-001…004` green)
- [ ] Every fixed severe defect has a regression case **that runs in CI**
- [ ] `ConnectOnion.Protocol.Conformance` passes (the existing CI gate)

---

## 19. Release Gate Criteria

In addition to §18:

- [x] The app, portable launcher, Core, Protocol, Conformance project, and all four test projects are wired into the solution as documented.
- [x] CI restores, audits dependencies, builds Release/x64, runs all three headless suites, collects coverage, compiles/discovers UI tests, publishes the unpackaged executable, and runs Protocol Conformance.
- [x] Headless snapshot verified on 2026-08-10: 225 Protocol + 1,179 Core/architecture + 197 SQLite integration = 1,601.
- [x] Thirty-six required real-window shell/chat tests pass together against the CI-equivalent trimmed Release executable; the portable root launcher is covered by the release rehearsal and the Explorer drag diagnostic is skipped.
- [x] Memory and UI performance audit tests have passed separately against a published executable.
- [x] Coverage is ratcheted in CI against `coverage-baseline.json`: 88.67% Protocol and 86.27% Core merged line coverage, failing on a drop of more than 0.25pp.
- [ ] If MSIX distribution is restored, signed install/upgrade/uninstall and clean-machine launch pass before shipping that channel.
- [x] Trimming is enabled and proven across warning analysis, the trimmed persist/restart harness, the trimmed app rendering scenario, the expanded 36-test shell suite, and the shipped portable ZIP; see `TRIMMING.md`.
- [ ] Qualified cold-start/WPR evidence is captured on an elevated, policy-enabled desktop runner. The graceful-shutdown harness itself is repaired.
- [ ] Critical reconnect/lifecycle and expanded real-window flows pass.
- [ ] Accessibility quality gates and a manual Narrator/text-scaling pass are complete.
- [ ] Hardware/external-state manual gates for real-agent streaming, speech/audio, notification activation, packaged tray behavior, and native/XAML profiling are signed off.

## 20. Test Directory Structure

Production projects sit at the repository root and all implemented test projects live under `tests/`. The structure below describes the **current repository**, not a proposed future layout.

```text
connectonion-desktop/
├── ConnectOnion.WinUIClient/                 # WinUI 3 app
├── ConnectOnion.WinUIClient.Core/            # WinUI-free test seam
├── ConnectOnion.Protocol/                    # protocol library
├── ConnectOnion.Protocol.Conformance/        # separate Release gate
├── ConnectOnion.Protocol.LiveTest/           # manual-only deployed-agent CI run; not in solution
├── ConnectOnion.PortableLauncher/            # NativeAOT launcher at portable ZIP root
├── scripts/
│   ├── Measure-Performance.ps1
│   ├── Test-Coverage.ps1
│   └── Test-MemoryLeaks.ps1
└── tests/
    ├── Directory.Build.props                  # pins one output path per configuration
    ├── ConnectOnion.Protocol.Tests/           # xUnit; 225 snapshot
    ├── ConnectOnion.WinUIClient.UnitTests/    # xUnit + ArchUnitNET; 1,179 snapshot
    │   ├── Accessibility/
    │   ├── Architecture/
    │   ├── Common/
    │   ├── Models/
    │   ├── Runtime/
    │   ├── Services/
    │   └── ViewModels/
    ├── ConnectOnion.IntegrationTests/         # xUnit + temporary real SQLite; 197 snapshot
    │   ├── Database/
    │   ├── Runtime/
    │   │   └── AgentSessionManagerTests.cs    # run-runtime persistence behaviour
    │   └── Services/
    ├── ConnectOnion.TrimSmoke/                # opt-in trimmed console harness; not in solution
    └── ConnectOnion.WinUIClient.UITests/      # xUnit + FlaUI; 41 discovered tests
        ├── ShellSmokeTests.cs                 # shared harness and shell smoke cases
        ├── ShellSmokeTests.Chat.cs            # send, reconnect, find, skipped Explorer drag diagnostic
        ├── ShellSmokeTests.ChatExtended.cs    # navigation, first send, retry, switch/restart, activation, icons
        ├── ShellSmokeTests.MemoryProbe.cs      # opt-in many-turn conversation memory probe
        ├── UiFakeAgentServer.cs               # loopback HTTP/WebSocket test agent
        ├── MemoryLeakTests.cs                 # opt-in lifecycle/memory gate
        └── PerformanceAuditTests.cs           # opt-in large-history UI audit
```

The solution is `ConnectOnion.WinUIClient/ConnectOnion.WinUIClient.sln`. `ConnectOnion.Protocol.LiveTest` and `tests/ConnectOnion.TrimSmoke` are intentionally outside it: the former runs only through explicit workflow dispatch, while the latter is published and run by the trimming audit.

### Naming conventions

- **Test classes**: `{ClassUnderTest}Tests`.
- **Test methods**: `MethodUnderTest_Scenario_ExpectedOutcome`.
- **Regression tests**: include the issue number in the method name or nearby test comment when that improves traceability.
- **UI tests**: describe the user-visible action and outcome; avoid encoding visual-tree structure in the name or locator.

## 21. Implementation Priority and Backlog

Completed infrastructure no longer belongs in the open backlog. The following are already implemented and should be maintained rather than re-proposed: the Core seam, Generic Host DI, CommunityToolkit.Mvvm migration, ArchUnitNET boundaries, fake-agent servers, isolated SQLite fixtures, current CI wiring, FlaUI project, 36 required shell/chat smoke tests, memory-leak gate, performance-budget audit, Protocol Conformance, and the separate manual-only live-protocol job.

Every unfinished P0/P1 theme is listed below; individual test IDs in §11–§13 may share one theme. This table is the backlog of record for this plan and deliberately carries no tracker numbers — the document ships with the source, which is not necessarily where the work is tracked.

| Priority | Unfinished theme | Completion evidence |
|---|---|---|
| P0/P1 | Expand reconnect and shutdown integration beyond the activation/startup race coverage | Deterministic integration tests for each remaining race/failure path |
| P0/P1 | Expand remaining real-window OS-notification, tray-icon, and deterministic drag/drop workflows | Stable E2E coverage for remaining flows with isolated profiles and documented desktop requirements |
| P1 | Accessibility, keyboard reachability, focus, names, contrast, scaling | Automated quality gates plus manual Narrator/scaling evidence |
| P1/P2 | Qualified long-conversation frame-time evidence | Repeatable WPR/ETW capture that complements the enforced UI latency, virtualization, and memory ceilings |
| P2 | Maintain en-US/zh-CN parity and complete Simplified Chinese UI validation | Automated resource parity plus manual zh-CN layout, text-scaling, and Narrator evidence |
| P2 | Agent-detail and modal-surface UI changes require regression coverage | Updated smoke/accessibility tests alongside the UI changes |

### Manual gates retained for now

Multi-monitor behavior, high contrast, full Narrator reading order, real microphone/audio lifecycle, Focus Assist/system notification behavior, signed-package activation, and WPR/XAML native profiling remain manual until repeatable automation exists for them.

### Backlog rule

Do not add a new unfinished theme to this document without an owner, an expected outcome, and a verification method. When one is completed, move its evidence into the relevant implemented/current-state section instead of leaving it listed here as planned work.

## Appendix A — Design Gaps Surfaced by This Plan (all resolved)

These were never testing problems — they are real design gaps that writing the test plan **exposed**, and all seven have since been fixed. The appendix is kept for two reasons: each fix is the reason a specific test case is writable at all (delete this and those cases lose their provenance), and it records *why* a change was made in a form that does not fit in a code comment.

**The durable architectural facts now live in [`CLAUDE.md`](../CLAUDE.md)** — schema versioning, the `IdentityStore` reset contract, `IUriLauncher`. Read that for how the code works today; read this for how it got there.

| # | Gap | Resolution |
|---|---|---|
| 1 | 🔴 **No DB migration framework** — the documented recovery was "delete the database" | **Fixed.** `Data/SchemaMigrator.cs` versions the DB via SQLite's `PRAGMA user_version` and applies ordered forward migrations in a transaction. Existing databases (version 0) are stamped as the version-1 baseline without running anything, since that is exactly the shape pre-versioning builds produced. A database written by a *newer* build now fails loudly instead of being silently mangled. `AppDatabase.EnsureInitializedAsync` calls it right after the baseline schema. **Enables `DB-MIGRATION-001/002`.** |
| 2 | 🔴 **`IdentityStore` silently regenerated the identity on decrypt failure** | **Fixed.** `Load()` now distinguishes *nothing stored* (normal first run → generate quietly) from *stored but unreadable* (identity is being thrown away → report). The latter raises `IdentityStore.IdentityReset` and latches `WasReset` / `ResetReason`; `MainWindow` surfaces it as an error toast telling the user their address changed and agents must re-authorize. **Enables `DB-ID-002`.** |
| 3 | 🟠 **`Launcher.LaunchUriAsync` is static and unmockable** | **Fixed.** `Services/UriLauncher.cs` introduces `IUriLauncher` + `SystemUriLauncher`, exposed as the settable `AppServices.UriLauncher`. `Shell/MainWindow.HelpMenu.cs` now goes through it. A test substitutes a fake and asserts the URI. **Enables `UT-HELP-002` and `E2E-012`; INFRA-4 is done.** |
| 4 | 🟠 **The original composition model behaved like a static locator** | **Replaced.** The app now uses a Generic Host and constructor injection. `AppServices` remains only as a typed, get-only bridge for framework-created code-behind; tests construct focused subject graphs or use registered abstractions. |
| 5 | 🟡 **Ctrl+Shift+/ hard-coded VK 191** | **Fixed.** `ProducesSlash(VirtualKey)` asks the *current keyboard layout* what character a key types (`MapVirtualKeyW` with `MAPVK_VK_TO_CHAR`), so the shortcut works on layouts where `/` is not on VK_OEM_2. Numpad divide is accepted too; VK 191 remains only as a fallback. **Removes the `UT-KBD-002` layout risk.** |
| 6 | 🟡 **`Name[..1]` splits emoji / surrogate pairs** | **Fixed.** The bug existed in *four* places (`ChatViewModel`, `HomePage`, `AgentDetailViewModel`, `ShellAgentItem`), which is why it now lives in one: `Common/NameInitial.cs` takes the first *grapheme* via `StringInfo.GetTextElementEnumerator`. **Enables `UT-AGENT-005`.** |
| 7 | 🟡 **`ApplyBaselineNudge`'s `0.22` was a guessed em-ratio** | **Fixed.** `HighlightedTextBlock.DescentFor(fontFamily, fontSize)` measures a probe `TextBlock` and derives the real descent from `DesiredSize.Height - BaselineOffset`, cached per (family, size); the ratio survives only as a fallback when measurement is impossible. Each call site now passes the font that actually governs its alignment — the code face for inline code, the document face for a code block. Search highlights inside code also inherit the monospace face now, which they previously did not. |

**Net effect on this plan**: the migration, identity-reset, URI-launch, keyboard-layout, grapheme, and baseline-measurement gaps are resolved. Remaining activation/lifecycle coverage is a real, owned gap — not an unowned document-only infrastructure item.

---

*End of document. Snapshot: 1,601 headless xUnit tests (225 Protocol + 1,179 Core/architecture + 197 SQLite integration), 41 discovered real-window UI tests (36 required shell/chat + 1 skipped Explorer drag diagnostic + 1 trimmed-runtime scenario + 3 opt-in diagnostics/audits), Protocol Conformance, and the manual-only live-protocol smoke reported separately. Verified 2026-08-10.*
