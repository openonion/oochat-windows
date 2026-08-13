# ConnectOnion Windows Desktop Client Pre-Release Performance Audit

> **Historical report.** This captures the 2026-07-25 pre-release state and must not be rewritten
> to match later code. Its conclusion that Release trimming was disabled was superseded on
> 2026-08-05 after the warning, persist/restart, real-window, package and performance gates passed.
> Read [`TRIMMING.md`](./TRIMMING.md) and [`PERFORMANCE.md`](./PERFORMANCE.md) for the current decision
> and benchmark method.

Audit date: 2026-07-25  
Code baseline: `00363e4` plus the pending release changes in the working tree  
Test configuration: Release / x64 / .NET SDK 10.0.301 / Unpackaged, with an unsigned x64 MSIX also produced  
Test machine: JUZI, Intel Core i9-13980HX, 32 logical processors, 31.6 GB RAM  
Operating system: Windows 11 Pro 25H2, 10.0.26200.8894

> All absolute measurements in this report apply only to this machine, dataset, and build.
> Anything that could not be captured is explicitly marked `NEEDS MANUAL VERIFICATION`.
> Task Manager Working Set alone is never treated as proof of a memory leak.

## 1. Executive Summary

The current build meets the release-candidate thresholds for common interactions, idle power,
chat-list virtualization, and the 50-cycle navigation and conversation-switching tests.

The audit found and fixed one genuine Release publishing defect: reflection-based JSON restoration
lost the required `ToolActivityViewModel` metadata in a trimmed publish. Historical Tool Activity
cards were therefore degraded to “Tool execution history could not be restored.” After the fix,
both the actual trimmed publish directory and the final untrimmed ReadyToRun publish restored the
same 15-step payload successfully. Tool Activity expansion took 7.2–12.8 ms.

The three most serious findings were:

1. **P1: Release trimming broke persisted Tool Activity, while additional IL2026 warnings remained
   on protocol and interactive-response paths.** Source-generated JSON metadata was added for the
   structured conversation payloads, and Release/CI trimming was disabled pending full coverage.
2. **P2: True cold-start evidence is incomplete and highly variable.** Single first-launch
   measurements ranged from 1.03 s to 8.82 s. The final build’s first launch from a new publish
   directory was 4.65 s. The standby list could not be cleared and WPR was blocked by system policy.
3. **P2: Speech, real WebSocket/Stop/reconnect, system notifications, and packaged installation
   were not fully stress-tested automatically.** These require a real agent, audio hardware,
   notification activation, or a signed package installation and remain manual release gates.

**Release-standard assessment:** The core performance and memory gates pass. Full GA release still
requires completion of the manual gates listed below.

**Recommendation: Ready with Known Risks.** The build is suitable for Release Candidate testing.
It should not be rolled out broadly until packaged cold-start, real-agent interaction, speech, and
notification checks have been signed off.

Raw evidence was captured under the ignored local `TestResults/` tree and is not part of the
repository. The original relative paths were:

- `TestResults/perf-audit/baseline-real-profile/report.md`
- `TestResults/perf-audit/baseline-after-untrimmed-real-profile/report.md`
- `TestResults/perf-audit/idle-5min-real-profile/summary.json`
- `TestResults/perf-audit/memory-50-release/memory-leaks.trx`
- `TestResults/perf-audit/memory-final-untrimmed-alternating/memory-leaks.trx`
- `TestResults/perf-audit/ui-performance-final-untrimmed.json`

## 2. Baseline Table

| Scenario | Original | After remediation | Change | Status |
|---|---:|---:|---:|---|
| Cold launch to first frame | 1,032 ms, n=1 | 4,655 ms, n=1 | Not comparable; first load from a new publish directory | NEEDS MANUAL VERIFICATION |
| Warm launch to first frame | 974.3 ms, n=4 | 717.6 ms, n=4 | 26.3% faster | PASS |
| 30 s idle Working Set | 206.4 MB | 204.7 MB | 0.8% lower | PASS (slightly above the 200 MB target, far below the 350 MB fail limit) |
| 30 s idle Private Bytes | 127.1 MB | 133.6 MB | 5.1% higher | PASS |
| Managed Heap at first frame | 5.9 MB | 5.7 MB | 3.4% lower | PASS |
| Five-minute tail CPU | 0.010% mean | Not repeated | — | PASS (the fix does not affect idle loops) |
| Open Settings | 68.5 ms median, 52.5–203.4 | 59.7 ms, 51.5–194.2 | 12.8% faster | PASS |
| Open Add Agent | 67.4 ms median, 55.2–91.0 | 62.2 ms, 49.4–92.0 | 7.7% faster | PASS |
| Restore from minimized | 13.5 ms median | 13.8 ms | 2.2% slower | PASS |
| Restore from hidden/tray state | 109.0 ms median | 75.2 ms | 31.0% faster | PASS |
| First open, 100-message conversation | 498.6 ms | 474.4 ms | 4.9% faster | PASS |
| First open, 500-message conversation | 521.3 ms | 625.6 ms | 20.0% slower, n=1 each | NEEDS MANUAL VERIFICATION |
| First open, 2,000-message conversation | 555.0 ms | 442.2 ms | 20.3% faster | PASS |
| Cached reopen, 100/500/2,000 messages | 371.8 / 388.6 / 361.8 ms | 336.1 / 387.9 / 383.9 ms | 9.6% faster / 0.2% faster / 6.1% slower | PASS |
| Expand Tool Activity | Card restoration failed in Release | 8.6 / 10.2 / 9.5 ms | Functionality restored | PASS |
| 50 cached-conversation switches, tail | 0.56 MB/cycle, 19.2 MB span | 0.03 MB/cycle, 1.2 MB span | Substantially more stable | PASS |
| Send the first real message | No real agent connected | No real agent connected | — | NEEDS MANUAL VERIFICATION |
| Open a live approval card | No live approval in the dataset | Not measured | — | NEEDS MANUAL VERIFICATION |

Both cold-launch groups contain only one “first” sample and neither run cleared the OS standby
list. Their percentage difference is therefore not statistically meaningful.

The original Release build also failed to restore Tool Activity, while the final dataset rendered
the full visual tree. The large-conversation before/after measurements are not identical workloads,
so not every difference should be attributed to execution speed.

## 3. Issues

### PERF-001 — P1 — Release trimming broke structured conversation payloads

- **Description:** The Release window degraded valid historical Tool Activity into a restoration
  failure card.
- **Reproduction:**
  1. Open a conversation containing persisted Tool Activity in Release x64.
  2. Generate 100-, 500-, and 2,000-message mixed conversations against the same database.
  3. Inspect the sidebar preview and message card.
- **Evidence:**
  - The Debug test host restored 15 steps from the same payload.
  - Before the fix, the Release UI showed “Tool execution history could not be restored” and
    exposed no expansion button.
  - `dotnet publish` reported 15 application-code IL2026 trim warnings across protocol, approval,
    ask-user, plan-review, and related runtime paths.
- **Root cause:** `ConversationRepository.Mapping` used reflection-based
  `JsonSerializer.Serialize/Deserialize<ToolActivityViewModel>`. The linker could not prove which
  runtime members were required.
- **Hot path:**
  `ConversationRepository.LoadMessagesAsync` →
  `RowToMessage` → reflection JSON metadata → required member trimmed → catch → activity fallback.
- **Modified files:**
  - `ConnectOnion.WinUIClient.Core/Data/ConversationJsonContext.cs`
  - `ConnectOnion.WinUIClient.Core/Data/ConversationRepository.Mapping.cs`
  - `ConnectOnion.WinUIClient/ConnectOnion.WinUIClient.csproj`
  - `.github/workflows/ci.yml`
  - `AGENTS.md` / `CLAUDE.md`
- **Fix:**
  - Added source-generated JSON metadata for `ToolActivityViewModel` and thought-step lists.
  - Changed repository reads and writes to use the `JsonTypeInfo` overloads.
  - Kept ReadyToRun but disabled Release and CI trimming until the remaining warned paths have
    trim-safe metadata and real-agent coverage.
- **Risk:** The uncompressed publish directory is larger; see Package Analysis. The persisted JSON
  format remains compatible, so functional risk is low.
- **Before/after:** Restoration failure → successful 15-step restoration; expansion 7.2–12.8 ms.

### PERF-002 — P2 — Cold launch is variable and lacks ETW root-cause evidence

- **Description:** The first launch from different fresh output directories measured 1.03 s,
  4.65 s, and 8.82 s.
- **Reproduction:** Launch a newly built or newly published directory and wait for
  `StartupTelemetry.TrackFirstFrame`.
- **Evidence:**
  - Isolated empty profile after build: 8,815 ms;
    `managedEntry=3,719`, `hostStarted=8,314`.
  - Final new publish directory: 4,655 ms;
    `managedEntry=1,723`, `hostStarted=4,208`.
  - Subsequent warm launches were stable at 695–752 ms.
- **Root cause:** Most of the time is before managed entry and during Host startup. WPR could not
  run, so disk cold pages, antivirus scanning, initial R2R mapping, and service initialization
  could not be separated further.
- **Production changes:** None.
- **Risk:** A Store or fresh-install machine may cross the five-second responsiveness threshold.
- **Before/after:** Warm launch improved by 26.3%; there is insufficient evidence to claim that
  true cold launch improved.

### PERF-003 — P2 — Settings navigation shows native handle high-water movement, but no proven leak

- **Description:** The tail half of the 50-cycle Settings test had a handle span of 58–60.
- **Reproduction:** Warm up 10 times, then open and close Settings 50 times while sampling after
  every departure.
- **Evidence:**
  - Private Bytes tail slope: `-0.09 MB/cycle`; span: `2.0 MB`.
  - `SettingsPage`, `SettingsViewModel`, and `SettingsOverlay` each remained at one instance in the
    0/25/50 snapshots.
  - `gcroot` showed only the expected `MainWindow → SettingsOverlay → SettingsPage` and framework
    roots.
  - A hypothesis that removed `Task.Run` from microphone enumeration produced no improvement and
    was reverted.
- **Root cause:** No managed event, static, or timer retention path was found. The result is more
  consistent with WinUI/UIA/device-enumeration native high-water behavior. Without handle-type
  ETW or WinDbg evidence, it is not classified as a leak.
- **Production changes:** None.
- **Risk:** Very long navigation runs should still be checked over 100–200 cycles with handle-type
  breakdown.
- **After result:** The final 50-cycle cached-conversation test had a handle tail span of 27 and a
  thread span of 5, both within the gate.

### PERF-004 — P3 — A 2,000-message conversation loads all data objects but keeps visuals virtualized

- **Description:** The repository and `ChatViewModel.RestoreConversationAsync` currently load the
  complete message history and then clear and repopulate the UI collection. There is no database
  paging.
- **Reproduction:** Open and cache-reopen 100-, 500-, and 2,000-message mixed conversations.
- **Evidence:**
  - Final 2,000-message first open: 442.2 ms; cached reopen median: 383.9 ms.
  - Only 14 `ListViewItem` containers were realized. The 100- and 500-message cases realized
    24 each.
  - The 2,000-message process sample was 346.0 MB Working Set and 270.6 MB Private Bytes.
- **Root cause:** The data layer is O(history), while the visual layer remains virtualized.
- **Production changes:** None. Current evidence does not justify risking paging and scroll-state
  semantics.
- **Risk:** Histories much larger than 2,000 items, or histories containing very long Markdown and
  image metadata, will continue to increase data-object memory.
- **Result:** The requirement that 2,000 messages must not create all visual elements is met.
  Database paging remains a P3 backlog item.

### PERF-005 — P3 — The startup benchmark’s shutdown harness is stale

- **Description:** `Measure-Performance.ps1` looks for the removed
  `ConnectOnionTrayHelper` native window, while the current tray implementation uses H.NotifyIcon.
- **Evidence:** All 15 original and final iterations reported “tray helper window not found” and
  were then killed by the harness.
- **Root cause:** The measurement script still describes the previous tray implementation.
- **Production changes:** None. Shutdown was not a core metric requested for this audit.
- **Risk:** The report cannot provide a valid graceful-shutdown duration. Startup and idle samples
  were completed before the forced termination.
- **Result:** All shutdown values are excluded. The harness’s 0/1 ms values are not treated as
  product measurements.

### PERF-006 — P2 — Hardware- and external-state-dependent scenarios remain under-covered

- **Scope:** Live WebSocket/Stop/reconnect, peak token streaming, live approval, SpeechRecognizer,
  audio hardware, system-notification activation, and packaged tray/activation behavior.
- **Static review:**
  - Run snapshots use one pending slot and one dispatcher drain, preventing one queued callback
    per token.
  - `ChatComposer` waveform timers run only while recording and are detached on disposal.
  - The image converter sets `DecodePixelWidth=280` and retains only weak `BitmapImage` references.
  - The Thinking Win2D control is deferred with `x:Load`.
- **Risk:** The lifecycle design is sound, but static review is not a substitute for hardware and
  ETW stress testing.
- **Status:** NEEDS MANUAL VERIFICATION.

## 4. Memory Test Results

### 4.1 Original five scenarios, sampled every five cycles

Each scenario was warmed up for 10 cycles before the 50 recorded cycles.

| Scenario | Cycle | Private MB | Working Set MB | Handles | Threads |
|---|---:|---:|---:|---:|---:|
| Settings | 0 | 148.5 | 234.4 | 1616 | 83 |
| Settings | 5 | 151.7 | 237.3 | 1633 | 84 |
| Settings | 10 | 152.4 | 238.3 | 1641 | 84 |
| Settings | 15 | 154.7 | 240.6 | 1631 | 79 |
| Settings | 20 | 156.2 | 242.4 | 1638 | 78 |
| Settings | 25 | 156.4 | 242.9 | 1627 | 77 |
| Settings | 30 | 156.8 | 243.3 | 1634 | 76 |
| Settings | 35 | 155.2 | 240.5 | 1654 | 76 |
| Settings | 40 | 155.5 | 241.5 | 1670 | 77 |
| Settings | 45 | 155.0 | 241.4 | 1675 | 77 |
| Settings | 49 | 155.0 | 241.4 | 1685 | 77 |
| Add Agent | 0 | 152.4 | 238.4 | 1570 | 83 |
| Add Agent | 5 | 158.2 | 244.3 | 1570 | 83 |
| Add Agent | 10 | 159.3 | 245.8 | 1572 | 83 |
| Add Agent | 15 | 155.1 | 241.8 | 1552 | 78 |
| Add Agent | 20 | 151.2 | 237.4 | 1548 | 77 |
| Add Agent | 25 | 151.1 | 237.6 | 1525 | 75 |
| Add Agent | 30 | 153.7 | 237.9 | 1523 | 75 |
| Add Agent | 35 | 152.6 | 237.4 | 1530 | 75 |
| Add Agent | 40 | 152.9 | 238.1 | 1541 | 76 |
| Add Agent | 45 | 152.9 | 238.4 | 1540 | 76 |
| Add Agent | 49 | 152.9 | 239.2 | 1541 | 76 |
| Agent Detail | 0 | 167.5 | 256.7 | 1602 | 85 |
| Agent Detail | 5 | 171.5 | 261.1 | 1602 | 85 |
| Agent Detail | 10 | 171.5 | 261.4 | 1602 | 85 |
| Agent Detail | 15 | 166.6 | 256.1 | 1573 | 77 |
| Agent Detail | 20 | 168.8 | 259.9 | 1549 | 77 |
| Agent Detail | 25 | 169.8 | 261.0 | 1541 | 75 |
| Agent Detail | 30 | 167.1 | 256.6 | 1541 | 75 |
| Agent Detail | 35 | 167.1 | 256.7 | 1547 | 75 |
| Agent Detail | 40 | 167.2 | 257.1 | 1555 | 76 |
| Agent Detail | 45 | 167.4 | 257.4 | 1554 | 76 |
| Agent Detail | 49 | 171.0 | 262.6 | 1554 | 76 |
| Conversation | 0 | 194.7 | 288.8 | 1625 | 84 |
| Conversation | 5 | 194.6 | 289.9 | 1622 | 84 |
| Conversation | 10 | 190.8 | 287.0 | 1624 | 84 |
| Conversation | 15 | 198.4 | 294.5 | 1584 | 78 |
| Conversation | 20 | 203.0 | 301.6 | 1580 | 77 |
| Conversation | 25 | 202.9 | 301.7 | 1583 | 77 |
| Conversation | 30 | 203.1 | 301.9 | 1590 | 77 |
| Conversation | 35 | 204.7 | 303.8 | 1601 | 78 |
| Conversation | 40 | 204.9 | 304.2 | 1599 | 78 |
| Conversation | 45 | 203.6 | 302.9 | 1599 | 78 |
| Conversation | 49 | 203.6 | 303.0 | 1600 | 78 |
| 4-session alternating | 0 | 209.7 | 305.6 | 1629 | 83 |
| 4-session alternating | 5 | 212.8 | 310.1 | 1631 | 83 |
| 4-session alternating | 10 | 214.9 | 313.2 | 1621 | 79 |
| 4-session alternating | 15 | 222.5 | 320.9 | 1602 | 77 |
| 4-session alternating | 20 | 220.9 | 320.9 | 1599 | 76 |
| 4-session alternating | 25 | 215.7 | 314.4 | 1604 | 76 |
| 4-session alternating | 30 | 220.1 | 318.8 | 1617 | 76 |
| 4-session alternating | 35 | 231.7 | 332.6 | 1632 | 77 |
| 4-session alternating | 40 | 234.8 | 335.8 | 1631 | 77 |
| 4-session alternating | 45 | 227.6 | 327.8 | 1636 | 77 |
| 4-session alternating | 49 | 230.8 | 331.6 | 1639 | 77 |

Tail trends:

| Scenario | Private slope MB/cycle | Private span MB | Handle span | Thread span | Result |
|---|---:|---:|---:|---:|---|
| Settings | -0.09 | 2.0 | 58 | 2 | PASS |
| Add Agent | -0.01 | 2.6 | 20 | 1 | PASS |
| Agent Detail | 0.03 | 4.0 | 15 | 1 | PASS |
| Conversation | 0.02 | 2.1 | 18 | 1 | PASS |
| 4-session alternating | 0.56 | 19.2 | 35 | 1 | PASS |

### 4.2 Final release candidate, four-session alternating test

| Cycle | Private MB | Working Set MB | Handles | Threads |
|---:|---:|---:|---:|---:|
| 0 | 213.9 | 299.7 | 1605 | 85 |
| 5 | 219.4 | 306.5 | 1608 | 85 |
| 10 | 220.3 | 308.5 | 1570 | 78 |
| 15 | 221.0 | 310.0 | 1569 | 77 |
| 20 | 220.6 | 311.5 | 1567 | 76 |
| 25 | 221.6 | 313.0 | 1576 | 77 |
| 30 | 222.0 | 313.9 | 1596 | 78 |
| 35 | 222.1 | 314.2 | 1597 | 78 |
| 40 | 222.2 | 314.4 | 1600 | 78 |
| 45 | 222.2 | 314.6 | 1592 | 74 |
| 49 | 222.6 | 315.5 | 1599 | 73 |

The tail slope was `0.03 MB/cycle`, with a `1.2 MB` Private Bytes span, handle span of `27`,
and thread span of `5`. All thresholds passed.

### 4.3 Three-stage managed snapshots and retention paths

| Snapshot | GC Heap bytes | Objects | SettingsPage | SettingsViewModel | CancellationTokenSource |
|---|---:|---:|---:|---:|---:|
| After warm-up | 5,177,940 | 60,966 | 1 | 1 | 26 |
| After 25 cycles | 5,508,120 | 60,184 | 1 | 1 | 26 |
| After 50 cycles | 5,710,072 | 63,331 | 1 | 1 | 25 |

Most of the additional objects at cycle 50 were profiler-triggered reflection metadata and strings,
not page instances. The actual `SettingsPage` root was the expected application framework/static
service → `MainWindow` → `SettingsOverlay` → `SettingsPage` chain. No second, departed Settings
visual tree was found.

Snapshot paths (also ignored local evidence):

- `TestResults/perf-audit/snapshots-settings/settings-00-baseline.gcdump`
- `TestResults/perf-audit/snapshots-settings/settings-25-cycles.gcdump`
- `TestResults/perf-audit/snapshots-settings/settings-50-cycles.gcdump`
- `TestResults/perf-audit/snapshots-settings/settings-50-cycles.dmp`

No per-snapshot Native Heap, XAML native retained-size, or COM dominator breakdown was available.
Private Bytes and handle/thread trends are process-level evidence and are not substitutes for a
native-heap snapshot.

## 5. UI Responsiveness Results

| Dataset | First open ms | Cached reopen median ms | Realized ListViewItems | Tool expansion ms |
|---:|---:|---:|---:|---:|
| 100 messages | 474.4 | 336.1 | 24 | 8.6 |
| 500 messages | 625.6 | 387.9 | 24 | 10.2 |
| 2,000 messages | 442.2 | 383.9 | 14 | 9.5 |

- **Virtualization: PASS.** The main list remains a standard `ListView`; it was not replaced with
  a `StackPanel`. Only 14 containers were realized for 2,000 messages.
- **Template selection: PASS.** `ChatMessageTemplateSelector` selects a separate template for each
  bubble kind.
- **Tool Activity: PASS with a deliberate design constraint.** The internal timeline intentionally
  uses a non-virtualizing `ItemsControl` to settle card height and avoid scroll jumps. Step rows use
  classic `{Binding}` so compiled bindings cannot retain rebuilt rows. The real 15-step fixture
  still expanded in less than 11 ms.
- **Deferred creation: PASS.** Thinking Win2D content and approval details use `x:Load`.
- **Images: PASS for static review and the limited dataset.** The converter decodes at 280 px and
  stores only a weak reference by path.
- **High-frequency streaming: static PASS, dynamic test unavailable.** `ChatViewModel` uses one
  pending snapshot slot and one dispatcher drain. There was no real high-throughput agent trace
  from which to quantify slow frames.
- **Slow frames, Measure/Arrange, and UI-thread call stacks: NEEDS MANUAL VERIFICATION.**
  WPR returned `0xc5585011 Failed to enable policy to profile system performance`, and Visual
  Studio XAML UI Responsiveness could not be automated in this session.
- **Accessibility and keyboard behavior:** The 690 Core tests and six real-window shell smoke tests
  passed. A complete Narrator walkthrough remains manual.

## 6. Startup Results

Warm-launch phase medians:

| Phase | Original ms | Final ms | Improvement |
|---|---:|---:|---:|
| managedEntry | 114.5 | 99.0 | 13.5% |
| hostStarted | 458.5 | 347.5 | 24.2% |
| windowCreated | 806.5 | 590.0 | 26.8% |
| windowActivated | 827.5 | 605.5 | 26.8% |
| firstFrame | 974.3 | 717.6 | 26.3% |

The final first launch from the new publish directory was:

`managedEntry 1,723 → hostStarted 4,208 → windowCreated 4,496 →
windowActivated 4,511 → firstFrame 4,655 ms`.

Static review of the pre-first-frame path found:

- `App` builds the Generic Host, and `OnLaunched` starts hosted services before creating the window.
- Notification settings/registration and keyboard settings load during Host startup.
- Image-cache pruning is already dispatched through `Task.Run`.
- Agent presence and network probing do not block the first frame.
- SQLite initialization ensures the schema and migrations only; it does not load all messages
  before the first frame.

There is no separate product telemetry marker for “fully interactive.” The final publish passed
real-window interaction with Settings and Add Agent. Initial default-conversation completion also
has no dedicated marker; the synthetic 442–626 ms first-open results are the available proxy.
Future telemetry should add `InteractiveReady` and `InitialConversationLoaded` marks.

## 7. Package Analysis

| Artifact | File count | Size |
|---|---:|---:|
| Original trimmed/ReadyToRun publish | 186 | 107.51 MB |
| Final untrimmed/ReadyToRun publish | 344 | 246.89 MB |
| x64 MSIX, compressed | 352 entries | 96.65 MB |
| MSIX uncompressed / approximate installed footprint | 352 entries | 246.84 MB |
| Separate installer | Not present | N/A |

Largest 30 entries in the final MSIX:

| # | File | Uncompressed MB | Compressed MB |
|---:|---|---:|---:|
| 1 | Microsoft.Windows.SDK.NET.dll | 52.75 | 14.22 |
| 2 | onnxruntime.dll | 20.67 | 7.02 |
| 3 | DirectML.dll | 17.84 | 9.08 |
| 4 | Microsoft.WinUI.dll | 15.64 | 4.15 |
| 5 | System.Private.CoreLib.dll | 15.28 | 6.41 |
| 6 | BouncyCastle.Cryptography.dll | 11.06 | 5.55 |
| 7 | System.Private.Xml.dll | 7.43 | 3.32 |
| 8 | coreclr.dll | 4.40 | 2.24 |
| 9 | System.Linq.Expressions.dll | 3.47 | 1.18 |
| 10 | Microsoft.InteractiveExperiences.Projection.dll | 3.36 | 0.95 |
| 11 | ConnectOnion.WinUIClient.dll | 3.14 | 1.10 |
| 12 | System.Data.Common.dll | 2.64 | 1.24 |
| 13 | Microsoft.Graphics.Canvas.Interop.dll | 2.59 | 0.76 |
| 14 | System.Security.Cryptography.dll | 2.43 | 1.05 |
| 15 | JetBrainsMono Nerd Font BoldItalic | 2.36 | 1.34 |
| 16 | JetBrainsMono Nerd Font Bold | 2.36 | 1.34 |
| 17 | JetBrainsMono Nerd Font Italic | 2.36 | 1.34 |
| 18 | JetBrainsMono Nerd Font Regular | 2.36 | 1.33 |
| 19 | Microsoft.DiaSymReader.Native.amd64.dll | 2.09 | 0.97 |
| 20 | clrjit.dll | 1.99 | 1.09 |
| 21 | System.Private.DataContractSerialization.dll | 1.97 | 0.86 |
| 22 | System.Text.Json.dll | 1.80 | 0.78 |
| 23 | System.Private.Windows.Core.dll | 1.76 | 0.61 |
| 24 | System.Net.Http.dll | 1.67 | 0.78 |
| 25 | Microsoft.Graphics.Canvas.dll | 1.65 | 0.62 |
| 26 | Microsoft.Web.WebView2.Core.Projection.dll | 1.38 | 0.39 |
| 27 | WinRT.Runtime.dll | 1.33 | 0.51 |
| 28 | Markdig.dll | 1.33 | 0.56 |
| 29 | mscordaccore.dll | 1.29 | 0.58 |
| 30 | Versioned mscordaccore copy | 1.29 | 0.58 |

Findings:

- The MSIX contains no PDBs, test assemblies, logs, test data, or legacy Electron/frontend assets.
- The unpackaged publish contains three PDBs totaling 0.82 MB. If the folder is distributed
  directly, they can safely be removed from the user payload and retained as a separate symbols
  artifact. The MSIX already excludes them.
- Only the x64 runtime is present; no duplicate x86 or ARM64 runtime was found.
- The four Nerd Font files total 9.44 MB uncompressed. They represent regular, italic, bold, and
  bold-italic styles and should not be removed without font-rendering regression evidence.
- ONNX Runtime and DirectML total 38.51 MB and arrive through the Windows AI/SDK dependency path.
  Their runtime use has not been disproven, so manually deleting those DLLs is not recommended.
- No `.msixupload` symbols package was generated because `mspdbcmf.exe` is missing on this machine.
- The unsigned MSIX built successfully but was not installed. First-install extraction,
  registration, and packaged cold-launch cost still require manual measurement.

## 8. Remaining Risks

1. True cold start requires an elevated standby-list purge and at least five WPR/WPA or Visual
   Studio Startup Profiler runs. The current first-launch samples are n=1.
2. The packaged/MSIX build was not signed and installed, so actual installed footprint and first
   registration cost are unavailable.
3. First send, peak token streaming, Stop, reconnect, background completion, and live approval
   were not stress-tested against a real agent.
4. SpeechRecognizer, AudioGraph, and Win2D waveform lifecycle tests require a real microphone and
   audio device.
5. System-notification activation, tray activation, and AppWindow/Window activation need a real
   50-cycle test.
6. Native Heap, COM/WinRT retained graphs, and XAML slow-frame/Measure/Arrange call stacks were not
   captured.
7. A 2,000-message conversation still loads all data objects at once. Main-list virtualization
   passes, but larger histories may require paging.
8. Release trimming is disabled. Before it is restored, all application IL2026 warnings must be
   resolved and a trimmed live-agent suite must cover WinUI, Windows App SDK, SQLite, Win2D,
   serialization, and notifications.
9. The performance script’s graceful-shutdown path is stale, so no valid shutdown metric is
   currently available.

## 9. Final Release Checklist

| Check | Status | Evidence / Notes |
|---|---|---|
| Release x64 build | PASS | Solution build succeeded; one unrelated CA1305 warning remains |
| Protocol unit tests | PASS | 161/161 |
| Core and architecture tests | PASS | 690/690 |
| SQLite integration tests | PASS | 113/113, including trim-safe Tool Activity round-trip |
| Protocol conformance | PASS | Address, canonical JSON, signature, and verification |
| Final real-window shell smoke | PASS | 6/6 |
| Warm-launch budget | PASS | 717.6 ms median |
| Statistically valid true cold launch | NEEDS MANUAL VERIFICATION | Standby list not purged; n=1 |
| Five-minute idle CPU and I/O | PASS | Tail CPU 0.010%; tail I/O zero |
| 50 Settings/Add Agent/Agent Detail/Conversation cycles | PASS | All tail thresholds passed |
| Final 50 cached-conversation switches | PASS | 0.03 MB/cycle; 1.2 MB span |
| Managed retention path | PASS | One page and VM; no duplicate visual-tree root |
| Native/XAML retention path | NEEDS MANUAL VERIFICATION | WPR policy prevented capture |
| 2,000-message main-list virtualization | PASS | 14 realized items |
| Tool Activity behavior and expansion | PASS | Restoration successful; 7.2–12.8 ms |
| High-throughput real streaming | NEEDS MANUAL VERIFICATION | No live-agent trace |
| Speech/Win2D/audio lifecycle | NEEDS MANUAL VERIFICATION | No hardware automation |
| 50 notification/tray cycles | NEEDS MANUAL VERIFICATION | Ten tray restores passed |
| x64 MSIX build | PASS | 96.65 MB, unsigned |
| Packaged install and first launch | NEEDS MANUAL VERIFICATION | Package not installed |
| Release trimming safety | PASS through disabling | ReadyToRun retained; CI override changed to false |
| Full screen-reader walkthrough | NEEDS MANUAL VERIFICATION | Automation names and keyboard tests do not replace Narrator |

## Final Conclusion

**Ready with Known Risks**

The current build is suitable for Release Candidate testing. The confirmed Release-only data
restoration defect is fixed, common paths show no continuous linear memory growth, idle CPU and I/O
are stable, the 2,000-message transcript remains visually virtualized, and all automated regression
tests pass.

Before broad production release, the manual gates for packaged cold start, real-agent workflows,
speech, notifications, and WPR/XAML tracing must be completed. Those areas cannot be treated as
passing without evidence.
