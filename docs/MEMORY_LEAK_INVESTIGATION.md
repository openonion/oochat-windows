# Memory leak investigation — conversation navigation

This document records the July 2026 investigation into memory that appeared to grow every time
a conversation page was opened. It also defines the regression test that now protects the
process-wide UI lifetime contract.

## Reported symptom

- Opening a conversation increased Task Manager memory substantially.
- Switching back to Home or another page did not restore the launch-time number.
- Repeating the navigation looked linear, initially by tens of megabytes per conversation.
- The footprint was mostly native: the managed heap was much smaller than the process working
  set, so a normal managed-object review was insufficient.

The important distinction throughout the investigation was:

1. **Live-object retention:** an old page, document tree, swap chain, handle, or thread is still
   reachable and growth remains linear.
2. **Allocator high water:** objects were released, but CLR, XAML, D3D, and Windows heaps retain
   committed pages for reuse. Task Manager does not return to the launch baseline, but repeated
   cycles eventually plateau.

Only the first is a leak. Testing merely whether Working Set returns to its initial value produces
false positives for virtually every non-trivial WinUI application.

## Investigation chronology

### 1. Page and event lifetime audit

The first pass found that chat-to-chat navigation reused one `ChatPage` and repointed its
`ListView`. Clearing `Messages` did not deterministically destroy the native item/document trees.
The page was changed to be uncached and disposable. Its unload/navigation path now cancels loads,
detaches page/ViewModel/scroll/timer subscriptions, stops live projection, and releases child
resources while the visual tree is still reachable.

This was necessary, but it did not make the process footprint plateau by itself. The lesson is
that fixing an obvious retention path is not evidence that it was the dominant allocation.

### 2. Real-process measurement instead of GC speculation

A temporary FlaUI loop opened the same real conversation and returned Home six times while
sampling four independent tripwires:

| Metric | Why it matters |
|---|---|
| Private Bytes | Best process-level signal for committed private memory |
| Working Set | Resident pages; useful context, but the OS may trim it independently |
| Handle count | Detects unreleased kernel/WinRT resources |
| Thread count | Detects render/audio/worker lifetime leaks |

The first measured run showed Private Bytes increasing from roughly 233 MB to 311 MB. More
importantly, thread count increased by about 21 whenever a fresh chat page created its composer.
That fingerprint pointed away from Markdown and toward a native render control.

### 3. Win2D waveform root cause

`ChatComposer` eagerly constructed a `CanvasAnimatedControl` for the speech waveform even if the
user never started recording. Each control created a native swap chain and a render-thread pool.
Calling `RemoveFromVisualTree` during disposal removed the visual attachment, but shared
Win2D/D3D resources and worker infrastructure were not synchronously returned at page navigation.

The waveform was replaced with ordinary XAML `Canvas`/`Line`/`Rectangle` elements. A 33 ms
`DispatcherTimer` updates the bars only while recording. After this change the repeated-navigation
thread count stayed within 85–87 instead of stepping upward by about 21 per page.

### 4. RichTextBlock and Markdig retention

Private Bytes still grew after the render-thread leak was removed. The remaining retention was in
virtualized document controls:

- `MarkdownTextBlock` retained the source string, Markdig AST, and native
  `RichTextBlock.Blocks` tree after its row unloaded.
- `HighlightedTextBlock` retained its `Blocks` tree.
- `ToolResultTextBlock` retained formatted `Inline` objects.

All three now release their native document collections on `Unloaded`. `MarkdownTextBlock` also
drops the parsed AST and source key. Dependency-property changes received while a row is detached
do not build a hidden native document; `Loaded` renders the current value when the row is realized
again.

### 5. Long-run validation

The final test repeated the same conversation-to-Home cycle 20 times:

| Observation | Result |
|---|---:|
| Private Bytes, cycle 0 | 245.3 MB |
| Private Bytes, cycle 12 | 333.7 MB |
| Private Bytes, cycles 13–19 | 332.1–335.7 MB |
| Threads in the stable tail | 87 |
| Handles | peaked near 1900, then fell to about 1848–1855 |

The first cycles warm the JIT, XAML type metadata, text services, glyph/font caches, SQLite, and
the CLR/native heaps. The tail is the relevant leak signal: it stayed in a roughly 4 MB band,
included downward samples, had stable threads, and had falling handles. The old linear leak was
gone even though Task Manager correctly did not return to the launch-time value.

### 6. The regression test found a second navigation allocation

The first end-to-end run of the permanent suite caught another issue outside the original chat
path. Repeatedly pressing **Add agent** while Home was already current forced a new navigation and
rebuilt the complete Home/AddAgentForm XAML tree. Its measured tail was about **4.42 MB/cycle**.
`HomePage` now implements the in-place reload contract used by `MainWindow`; the same scenario
then measured **0.81 MB/cycle** with a 5.3 MB tail range and stable handles/threads. This validates
that the test can find a new regression rather than merely encoding the original fix.

### 7. Rapid session switching retained detached pages

The follow-up same-session/two-session report exposed a separate lifetime gap in the loading
overlay. Every new `ChatPage` started fire-and-forget waits for ListView container realization,
scroll completion, and quiet layout frames. Those waits could remain subscribed to
`ContainerContentChanging` and `ViewChanged` for several seconds after navigation. Switching
quickly therefore kept several detached pages, native transcript trees, and message collections
alive concurrently; longer/different histories used a larger wait budget and showed more growth.

The waits now share a page-lifetime cancellation token. `Dispose` cancels them immediately,
disconnects the ListView from its ItemsSource/template selector, clears the ViewModel collection,
and removes cached approval delegates that could otherwise point back to an old ViewModel.
Attachment thumbnail caching now uses weak `BitmapImage` references, so the 32-entry lookup no
longer owns native decoded surfaces after every Image control releases them.

Post-fix real-process runs reached a bounded tail in both reported paths: reopening one
conversation measured **0.60 MB/cycle** with a 5.2 MB private-byte range; alternating between two
different conversations measured **0.46 MB/cycle** with a 4.4 MB range. Both held thread count
flat, while handle ranges remained within the regression budget.

### 8. Project-wide retained-state audit

A later whole-project audit covered static caches, singleton dictionaries, event subscriptions,
timers, queues, WebSocket registries, and large payload paths. It added these lifetime bounds:

- `EndpointResolver` now removes expired endpoint/info entries and caps each static cache at 128
  keys. TTL checks alone did not release unique agent/relay keys.
- `ConversationRunRegistry` keeps at most 64 terminal snapshots. Raw events are already removed
  at completion; the capacity limit also prevents one key/reply per historical conversation from
  remaining forever.
- deleting a conversation removes its connection-wiring marker; deleting an agent also removes
  the manager's agent config, presence result, and pending/offline notification state.
- the highlighted-text font-metric cache is capped at 32 family/size combinations.
- every eighth fully detached heavy `ChatPage` checks private bytes. Only pressure at or above
  320 MiB schedules an optimized, non-blocking background Gen2 collection, and a 30-second
  cooldown prevents periodic navigation pauses. Requests remain serialized and do not compact
  the LOH or wait for apartment-sensitive finalizers.

An attempted byte-weight limit on `ConversationCache` was rejected by measurement. The test
profile contains a large transcript; declining to cache it caused every navigation to recreate
the complete managed message graph and delayed native XAML reclamation. A 16-cycle run rose as
high as 4.52 MB/cycle. Restoring the existing four-entry LRU and extending the observation window
produced the real steady state. In the final full isolated suite, the same-conversation path
measured **0.67 MB/cycle** with a **7.9 MB** Private Bytes range. The two-conversation path
measured **0.15 MB/cycle** with a **4.9 MB** range; its handles and threads both fell in the tail.
An additional 32-cycle alternating stress run measured **0.29 MB/cycle**, a **6.8 MB** range,
handle range 9, and thread range 1.

That rejection applied to caching an otherwise complete transcript. The 2026-08-01 paging change
loads only the newest 160 rows and keeps older history on disk until the user scrolls upward, so
the cache now has a 16 MiB estimate cap in addition to its four-entry LRU without forcing ordinary
navigation to rebuild a complete large transcript. This newer design still needs the real-window
memory gate before its slope can be compared with the historical figures above.

Because WinUI and CLR reclamation is batched for large document trees, the regression defaults
are now 24 measured cycles, 6 warm-up cycles, and 750 ms settling. The pass/fail limits were not
loosened. Each surface also runs in a fresh app process, so JIT/XAML high-water marks warmed by an
earlier surface are not incorrectly charged to the next surface's slope.

## Permanent fixes

- `ChatPage` is not navigation-cached and performs full teardown on navigation.
- `ChatComposer` no longer creates a Win2D animation surface per page.
- `ThinkingIndicator` explicitly releases its exceptional, lazily-created Win2D surface.
- Markdown, highlighted text, and tool-result controls release native document trees on unload.
- Loading-overlay/container waits are cancelled as soon as their ChatPage navigates away.
- Chat teardown disconnects the ListView and clears approval callbacks before dropping messages.
- Attachment thumbnail cache entries are weak references rather than owners of decoded surfaces.
- Navigation buttons have stable AutomationIds so memory tests do not depend on localized text or
  the shape of the visual tree.
- `ChatPageLifetimeGuardTests` protects the critical source-level lifetime invariants in the
  headless suite.
- `MemoryLeakTests` exercises the real app process and asserts that navigation reaches a stable
  high-water band.
- Protocol endpoint caches, terminal run snapshots, font metrics, and deletion-time singleton
  state all have explicit capacity/removal guards.

## Automated regression suite

Run the complete gate from an interactive Windows desktop:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-MemoryLeaks.ps1
```

The script:

1. refuses to run while another ConnectOnion instance is open;
2. builds the Debug/x64 unpackaged app and UI test assembly;
3. runs protocol, Core, and SQLite integration tests unless `-SkipHeadless` is supplied;
4. copies `%AppData%\ConnectOnion` into a temporary isolated data root;
5. repeatedly exercises Settings, Add Agent, Agent Detail, one conversation, and alternating
   between two different conversations when the profile contains at least two; each scenario
   gets a fresh process and its own copy of the isolated baseline database;
6. writes a TRX report under `TestResults\memory\<timestamp>`;
7. copies application logs into the result directory if the run fails;
8. deletes the temporary profile in `finally` and never modifies the real database.

Useful options:

```powershell
# Explicitly select the current default observation window
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-MemoryLeaks.ps1 `
  -Cycles 24 -WarmupCycles 6 -SettleMilliseconds 750

# Shell-only run on a profile with no conversations
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-MemoryLeaks.ps1 `
  -AllowNoConversation

# Isolate one scenario while diagnosing a failure
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-MemoryLeaks.ps1 `
  -Scenarios agent-detail -Cycles 24 -SkipHeadless
```

### Pass/fail model

Each surface is warmed independently. Only the tail half of measured samples is evaluated. The
default failure limits are:

| Signal | Default limit |
|---|---:|
| Private Bytes least-squares tail slope | ≤ 1.5 MB/cycle |
| Private Bytes tail range | ≤ 24 MB |
| Handle-count tail range | ≤ 64 |
| Thread-count tail range | ≤ 8 |

These are plateau tests, not absolute-memory budgets. Absolute idle budgets remain in
`scripts/Measure-Performance.ps1`; leak tests answer whether repeated use is unbounded. Thresholds
can be tightened through script parameters after a new Release baseline is ratified, but should
not be loosened just to silence a regression.

## August 2026 follow-up — the same primitive, a different axis

The July suite measured **navigation**: open a surface, close it, repeat. It did not measure
**turns**: hold one conversation open and keep chatting. That was listed below as a known gap, and
a leak was living in it.

### Symptom

Memory climbed steeply during an ordinary text-only conversation — no images, no attachments.

### Measurement

`ShellSmokeTests.MemoryProbe.cs` (opt-in, `CONNECTONION_MEMORY_PROBE=1`) drives real turns through
the loopback `UiFakeAgentServer` and samples the app process after each one. A representative turn
is `llm_call` → 6 × `thinking` → `llm_result` → `assistant` → `OUTPUT`.

40 text-only turns, before the fix:

| turn | private MB | handles | threads |
|---|---|---|---|
| 0 | 132.6 | 1471 | 78 |
| 10 | 367.5 | 4236 | 652 |
| 20 | 665.8 | 7557 | 1346 |
| 40 | 983.9 | 10544 | 1865 |

Threads were the tell, exactly as this document's tripwire model predicts: ~55 per turn, never
released, and the private-byte slope is mostly their stacks and the D3D devices behind them.

### Bisection

Three controlled runs, one variable each:

1. **Turns carrying only `OUTPUT`** (no streamed events): threads flat at 136, 10 turns cost 30 MB
   — the transcript itself. So the growth was in the streamed events.
2. **6 vs 12 `thinking` events per turn**: identical slopes. So it was per *turn*, not per event.
3. **`ThinkingIndicator` forced to `x:Load="False"`**: 10 turns went 139.6 → 158.1 MB with threads
   78 → 81. The leak vanished entirely.

### Root cause

`ThinkingIndicator` wrapped a Win2D `CanvasAnimatedControl` — the same primitive section 3 removed
from the composer's waveform, reintroduced in a worse place. In the composer it was one instance
per page; here it sat inside a **virtualized `ListView` item template**, so an instance existed per
running activity row.

Each canvas carries its own D3D device, swap chain and render thread, and Win2D releases none of it
unless `RemoveFromVisualTree()` is called. Nothing called it:

- `OnUnloaded` only set `Canvas.Paused = true`, with a comment explaining that
  `RemoveFromVisualTree` was deliberately omitted because Win2D does not support re-attaching a
  canvas afterwards and a recycled row came back blank. Correct about Win2D; wrong conclusion.
- `Dispose()` was reached only from `ChatPage.DisposeThinkingIndicators`, which walks the **live**
  visual tree at page teardown — so it could not see instances `x:Load` had already detached.

The XAML comment claimed `x:Load` bounded the app to one canvas because only one row is ever
thinking. `x:Load` going false does not dispose what it unloads, and several rows per turn pass
through the running state, so the real bound was one canvas per *realization*.

### Fix

`ThinkingIndicator` is now ordinary XAML shapes — eight rounded `Rectangle`s driven by one
`Storyboard`. Every animated property is a `RenderTransform` or `Opacity`, so the loop is an
independent (composition-thread) animation and costs no layout pass. The control keeps its
`IDisposable`, `IsActive` and pause-on-`Unloaded` contract, so no call site changed.

After the fix, same 40 turns: **147.1 → 185.1 MB, threads 78 → 78, handles 1556 → 1684**, with the
curve flat from roughly turn 34.

**The rule this leaves behind:** a Win2D canvas is a per-window-scale resource. Do not put one in
anything a virtualizing host realizes, and do not treat `x:Load` as a lifetime guarantee — it
defers construction, it does not dispose.

## Scope and limitations

No finite automated test can prove that every allocation path in a process is leak-free. This
suite covers every persistent navigation surface and the known expensive native primitives, then
uses handles and threads as independent tripwires. The August 2026 probe above adds the
many-turns-in-one-conversation axis, but only for text turns against a loopback agent. It does not
exercise microphone capture, every attachment codec, multi-window behavior, GPU-driver-specific
caches, or hours-long soak behavior. Those paths still require targeted scenarios or WPR/WPA when
a regression report identifies them.

For investigation, always prefer Private Bytes plus handles/threads over Working Set alone, run
enough cycles to get past warm-up, and inspect the tail slope. A value that never returns to the
launch baseline is normal; a tail that remains linear is not.
