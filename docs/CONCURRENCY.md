# Concurrency and thread safety

Where threads come from in this app, what shared state they touch, and which primitive guards
each piece. Written from the code, with file references — when this document and the code
disagree, the code is right.

The short version: this is not "a UI thread plus a background worker". Six independent sources
of execution reach the same state, and the busiest path — one agent turn — crosses three of them
before it reaches the screen.

## Where the threads come from

| Source | Count | Where |
|---|---|---|
| WebSocket receive loop, silence watchdog, reconnect loop | 3 long-lived `Task.Run` per socket | `ConnectOnion.Protocol/AgentConnectionService.cs` |
| Turn executor — one per in-flight turn | 1 `Task.Run` per turn | `ConversationRunRegistry.RunAsync` |
| `HttpClient` + Polly continuations (presence probes, `/info`) | thread pool | `AgentPresenceService`, `EndpointResolver` |
| Notification grace-period timer (`System.Threading.Timer`) | thread pool callback | `Services/Notifications/ThreadingScheduler.cs` |
| SQLite async read/write continuations | thread pool | `Core/Data/*Repository.cs` |
| UI thread | `DispatcherQueue` | everything in `Controls/`, `Views/` |

Different conversations run genuinely in parallel — one active run each, one socket each — so all
of the above can be live several times over at once.

### The hot path

A streaming reply crosses three threads before it is visible:

```
socket receive thread  →  turn executor thread  →  UI thread
(AgentConnectionService)  (ConversationRunRegistry)  (ChatViewModel → ObservableCollection)
```

Every mechanism below exists to make some hop of that chain safe.

## The primitives, and what each one is actually for

The inventory changes as features move across the Core seam, so this section documents the
invariants and owners rather than brittle source-token counts.

### 1. Run state machine — one lock per run, publish outside it

`ConnectOnion.Protocol/Runtime/ConversationRunRegistry.cs` is the densest concurrency in the repo
and states its invariants in the type's doc comment. Every mutation path has the same shape:

```
take run.Gate → mutate → Sequence++ → snapshot inside the lock → RELEASE → publish
```

Two rules make it work:

- **No network, DB, or UI work while holding a run's lock.** Subscribers run arbitrary code,
  including UI marshalling; holding the gate across that is how deadlocks get built. `Finalize`
  says so at the exact line where it releases.
- **Terminal exactly once.** `Finalize` checks and sets `run.Finalized` inside the lock, and every
  other mutation path bails on it. That is what holds when a cancellation and a completion arrive
  at the same instant.

What is published is an **immutable snapshot carrying a monotonic `Sequence`**, so a subscriber
can never observe a torn intermediate state.

Ordering guarantee worth knowing: on success the reply is **persisted before** the run is marked
`Completed`, so a page opening mid-turn loads authoritative history from SQLite and replays live
events on top, instead of double-rendering.

### 2. Lazy singletons in concurrent maps — `ConcurrentDictionary` + `Lazy<T>`

`Core/Services/Runtime/AgentConnectionRegistry.cs` keys one socket per conversation. The subtlety
its comment calls out: **`GetOrAdd`'s factory can run on several threads at once**, and the losers
are discarded. With a raw socket that means silently leaking a live WebSocket. Wrapping the value
in `Lazy<T>` means only the winner is ever constructed.

`AgentSessionManager` uses the same family for seven maps — agents by id, per-conversation
interactive-answer queues (`ConcurrentQueue`), pending interactive prompts, wired connections,
interrupt requests — all "many readers, occasional writer, no cross-key invariant", which is
exactly what `ConcurrentDictionary` is for. Note it is *not* a substitute for a lock when two keys
must change together; nothing here needs that.

### 3. Run-once and single-flight — `Interlocked`

| Use | Site |
|---|---|
| Idempotent shutdown (run at most once) | `App.ShutdownAsync`, `MainWindow.DetachWindowServices` |
| Re-entrancy guard (one reconnect loop at a time) | `AgentConnectionService` `CompareExchange` |
| One-bit cross-thread mailbox (cold-start activation) | `App` activation buffering |

### 4. Coalescing mailbox — the UI back-pressure fix

`ChatViewModel.SubscribeToRun` is the most interesting piece,
and it solves a real memory problem rather than a correctness one.

The executor produces snapshots far faster than the UI thread can apply them. Enqueuing each one
let the dispatcher queue grow unbounded for the length of a reply, holding a closure and a
snapshot per update. Instead:

- the newest snapshot is swapped into a **single slot** (`Interlocked.Exchange`);
- only the thread that flips the drain flag `0 → 1` (`Interlocked.CompareExchange`) queues the
  drain — everyone else has already handed its snapshot to that pending drain.

Dropping intermediate snapshots is safe **because snapshots are cumulative, not incremental**, and
`ApplyRunSnapshot` replays from `_appliedEventCount`, so applying only the newest still projects
every event exactly once. **Terminal snapshots are always queued** — a terminal transition must
not be swallowed by a run that starts immediately after.

### 5. Bounded and serialized work — `SemaphoreSlim`

Four gates serve different invariants:

| Gate | Invariant |
|---|---|
| `AppDatabase.InitGate` | Baseline creation and ordered migrations run once even when repositories open concurrently at first frame |
| `AgentPresenceService.ProbeGate` | At most four presence probes are in flight, preventing an agent library from flooding the network/thread pool |
| `OpenOnionAccountService._gate` | Account-opening work is single-flight for one service instance |
| `SidebarStateRepository._saveGate` | Sidebar state writes are serialized so an older save cannot overwrite a newer snapshot |

`AppDatabase` keeps a deliberately unsynchronized fast-path read of `_initialized`: the flag only
ever moves `false → true`, cannot be read torn, and the worst case is redundantly taking the gate.

### 6. UI affinity — `DispatcherQueue.TryEnqueue`

Services publish events from whatever thread they finished on and say so in their contract:
`AgentPresenceService`'s doc comment states `PresenceChanged` may fire off the UI thread and that
**subscribers must marshal**.

Always `TryEnqueue`, never the throwing overload — if the window is closing the queue is gone, and
a dropped presence update is the correct outcome, not an exception on a background thread with no
handler.

### 7. Plain `lock` for small shared state

`WindowPresenceService` (focus/visibility/current conversation) and `NotificationCoordinator`
(grace-period bookkeeping) each guard a handful of fields with one private gate. Small, no I/O
inside, no nesting.

### 8. Cancellation

Page unload cancels conversation restore; tokens flow through view
model loading into SQLite reads and writes. `ImageCacheMaintenanceService` holds its own so an app
closed seconds after launch does not leave a cache sweep running against a torn-down host.

## Shutdown: the one transition that matters

**Closing the window is not the same as exiting.** Clicking the title bar's X raises `WM_CLOSE`,
which `MainWindow.Tray.cs` intercepts to show a "Minimize to tray / Exit" dialog. Choosing
minimize means the window is *hidden*, not closed: `Closed` never fires, nothing is torn down,
and every timer stays armed on purpose because the app is still running.

That narrows the teardown race to a single funnel. Every real exit — the dialog's Exit button, the
tray menu's Exit, File → Exit — reaches `MainWindow.ExitApplication`, which runs in a fixed order:

```
_isExiting = true → DisposeTrayBehavior() → DetachWindowServices()
                  → await App.ShutdownAsync()   ← dispatcher pumps for all of this (~150–220 ms)
                  → Application.Current.Exit()
```

`DetachWindowServices` runs **before** the await, and is idempotent (`Interlocked.Exchange`). It is
therefore the one correct place to disarm anything that could otherwise fire during that window —
which is why the app needs no global "is shutting down" flag threaded through every component.

### What gets disarmed there

| Owner | Disarmed by |
|---|---|
| Zoom popup idle timer | `DetachViewMenu()` |
| Toast auto-dismiss timers + exit animations | `InAppNotificationHost.Shutdown()` (also guards `Show`/`Remove`/the animation continuation with `_shutdown`) |
| Window event hooks (theme, sessions, presence, shortcuts) | unsubscribed inline |
| **The current page's timers** | `Views.IShutdownDisarmable.DisarmForShutdown()` |

`IShutdownDisarmable` exists because pages disarm in `Unloaded`, and **`Unloaded` is not
guaranteed to fire on window close**. `ChatPage` implements it to stop the find-debounce timer,
stop the view model's thinking ticker, and call `ChatComposer.Dispose()` (synchronous and
idempotent — its own `Unloaded` handler is `async void` and would resume on a dispatcher that may
no longer have a tree). It deliberately does **not** call `OnUnloaded`, which also persists scroll
position and reports viewing state — service calls started at the moment services are being torn
down.

Implementations must be synchronous, idempotent, and disarm-only.

### Reading a shutdown in the log

The teardown is instrumented end to end, because its failure mode is invisible: skipping a disarm
does not throw, it leaves a timer armed, and the access violation that eventually follows carries
no managed stack and no link back to the step that caused it. A clean exit looks like this
(in `<data root>\logs`, i.e. `%AppData%\ConnectOnion\logs` unpackaged):

```
[INF] Exit requested; tearing down window
[INF] Window teardown: page HomePage has nothing to disarm
[INF] Application shutdown started
[INF] Run manager shutdown completed in 29 ms
[INF] Generic Host shutdown completed in 2 ms
[INF] Application shutdown completed in 37 ms
```

What each line is for:

- **`Exit requested`** — the `ExitApplication` funnel was entered. A log that stops without this
  line is a crash, not a clean exit; that distinction is otherwise unrecoverable after the fact.
- **`Window teardown: …`** — proof the page-disarm step ran, and which branch it took: `disarmed
  page ChatPage`, `page X has nothing to disarm`, or a `Warning` if a page threw. This line is
  also the only practical way to confirm `IShutdownDisarmable` dispatch works at all, since the
  race it prevents cannot be reproduced on demand.
- **`Window hidden to tray; app still running`** — the *other* branch of the close dialog.
  Without it, "minimized" and "exited" are indistinguishable in a log (the process just stops
  writing), which is exactly the ambiguity behind a "the app closed itself" report.

Note the ordering guarantee this exposes: window teardown completes **before** `Application
shutdown started`, i.e. before the ~30–200 ms of async host shutdown during which the dispatcher
is still pumping. That is the whole point of the arrangement.

## Sharp edges

- **The dispatcher keeps pumping after `Window.Closed`.** A still-armed `DispatcherTimer` tick or
  an `async void` continuation can run against a visual tree being torn down. It surfaces as an
  access violation (`0xC0000005`) **inside native `Microsoft.UI.Xaml.dll`** — no managed `catch`
  sees it, and the log shows a clean shutdown moments earlier. It is a race: it reproduces
  intermittently and only when something was genuinely in flight, which is why it is guarded by
  construction (above) rather than by testing. A new window-owned timer or animation belongs in
  `DetachWindowServices`; a new *page*-owned one means that page should implement
  `IShutdownDisarmable`.
- **Fire-and-forget is used deliberately in several places** (resume probe, cache sweep, reconnect).
  Each swallows its own exceptions on purpose; check the comment before "fixing" one into an await.
- **Serilog writes with `shared: true`** — the log file is shared across processes.
- A run's snapshot subscription can deliver **after** the page switched conversations and disposed
  it; `DrainPendingSnapshot` re-checks rather than trusting that disposal already stopped delivery.

## Rules of thumb this codebase follows

1. Hold a lock only over in-memory state changes — never I/O, never a subscriber callback.
2. Publish immutable snapshots with a monotonic sequence; let readers replay from their own cursor.
3. Terminal transitions happen exactly once, guarded inside the lock that owns the state.
4. `Lazy<T>` inside `GetOrAdd` whenever the value owns an unmanaged/disposable resource.
5. Marshal to the UI thread at the *subscriber*, not the publisher, and use `TryEnqueue`.
6. Persist before publishing a terminal state, so a reader that arrives late reads the truth.
