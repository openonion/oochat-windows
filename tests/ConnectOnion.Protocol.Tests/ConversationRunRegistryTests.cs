using System.Collections.Concurrent;
using System.Diagnostics;
using ConnectOnion.Protocol.Runtime;

namespace ConnectOnion.Protocol.Tests;

/// <summary>
/// Headless coverage of the app-level run runtime — the ownership/lifecycle/
/// concurrency guarantees that let a turn survive its originating page and let a
/// re-opened conversation resume live streaming. Uses fake executors + an in-memory
/// persistence double; no sockets, no database, no UI.
/// </summary>
public sealed class ConversationRunRegistryTests
{
    private static TurnRequest NewRequest(string conversationId = "conv", string agentId = "agent") => new(
        RunId: Guid.NewGuid().ToString(),
        ConversationId: conversationId,
        AgentId: agentId,
        UserMessageId: Guid.NewGuid().ToString(),
        AssistantMessageId: Guid.NewGuid().ToString(),
        Prompt: "hello");

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10);
        }
    }

    private static AgentStreamEvent Event(string type) => new(type, type, Guid.NewGuid().ToString(), "{}");

    // ---- Scenario 1: switch away then re-enter mid-turn ----

    [Fact]
    public async Task NewSubscriber_MidTurn_ImmediatelyReceivesPartialAndBufferedEvents()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);
        var release = new TaskCompletionSource();

        var executor = new FakeExecutor(async (req, sink) =>
        {
            sink.SetRunning();
            sink.Publish(Event("thinking"));
            sink.UpdatePartial("Hello");
            await release.Task;
            return "Hello world";
        });

        var request = NewRequest();
        registry.StartRun(request, executor);

        // Simulate the originating page being destroyed, then a brand-new page
        // opening the same conversation while the turn is still running.
        await WaitUntilAsync(() => registry.GetActiveRun("conv")?.PartialContent == "Hello");

        var received = new List<ConversationRunSnapshot>();
        using var _ = registry.Subscribe("conv", s => received.Add(s));

        // The immediate snapshot resumes the exact in-progress state.
        Assert.NotEmpty(received);
        var first = received[0];
        Assert.Equal(ConversationRunStatus.Running, first.Status);
        Assert.Equal("Hello", first.PartialContent);
        Assert.Contains(first.Events, e => e.Type == "thinking");

        // Streaming continues to flow to the new subscriber, ending in one terminal.
        release.SetResult();
        await WaitUntilAsync(() => received.Any(s => s.IsTerminal));
        var terminal = received.Last();
        Assert.Equal(ConversationRunStatus.Completed, terminal.Status);
        Assert.Equal("Hello world", terminal.PartialContent);
        Assert.Equal(1, received.Count(s => s.IsTerminal));
    }

    // ---- Scenario 2: multiple conversations run in parallel & isolated ----

    [Fact]
    public async Task ParallelConversations_AreIsolated_AndOneFailureDoesNotAffectAnother()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);
        var releaseA = new TaskCompletionSource();

        var execA = new FakeExecutor(async (req, sink) =>
        {
            sink.SetRunning();
            sink.UpdatePartial("A-content");
            await releaseA.Task;
            return "A-done";
        });
        var execB = new FakeExecutor((req, sink) =>
        {
            sink.SetRunning();
            sink.UpdatePartial("B-content");
            throw new InvalidOperationException("B blew up");
        });

        registry.StartRun(NewRequest("A"), execA);
        registry.StartRun(NewRequest("B"), execB);

        await WaitUntilAsync(() => registry.GetActiveRun("B")?.Status == ConversationRunStatus.Failed);

        // A is untouched by B's failure and still streaming its own content.
        var a = registry.GetActiveRun("A")!;
        Assert.Equal(ConversationRunStatus.Running, a.Status);
        Assert.Equal("A-content", a.PartialContent);

        var b = registry.GetActiveRun("B")!;
        Assert.Equal(ConversationRunStatus.Failed, b.Status);
        Assert.Equal("B-content", b.PartialContent); // partial retained on failure
        Assert.Equal("B blew up", b.ErrorMessage);

        releaseA.SetResult();
        await WaitUntilAsync(() => registry.GetActiveRun("A")?.Status == ConversationRunStatus.Completed);
    }

    // ---- Scenario 3: multiple windows subscribe to the same conversation ----

    [Fact]
    public async Task MultipleSubscribers_ShareState_AndUnsubscribeDoesNotCancelRun()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);
        var release = new TaskCompletionSource();

        var executor = new FakeExecutor(async (req, sink) =>
        {
            sink.SetRunning();
            await release.Task;
            sink.UpdatePartial("final-ish");
            return "final";
        });
        registry.StartRun(NewRequest(), executor);

        var window1 = new List<ConversationRunSnapshot>();
        var window2 = new List<ConversationRunSnapshot>();
        var sub1 = registry.Subscribe("conv", s => window1.Add(s));
        var sub2 = registry.Subscribe("conv", s => window2.Add(s));

        Assert.NotEmpty(window1);
        Assert.NotEmpty(window2);

        // One window closes.
        sub1.Dispose();
        var window1CountAtClose = window1.Count;

        // The run keeps going; the other window keeps receiving updates.
        release.SetResult();
        await WaitUntilAsync(() => window2.Any(s => s.Status == ConversationRunStatus.Completed));

        Assert.Equal(window1CountAtClose, window1.Count); // detached window got nothing more
        Assert.Contains(window2, s => s.Status == ConversationRunStatus.Completed);
        Assert.Equal(1, persistence.CompletedCount); // run truly completed, not aborted
    }

    // ---- Scenario 4: persistence happens before Completed is published ----

    [Fact]
    public async Task Completion_PersistsBeforePublishingCompleted()
    {
        var persistedBeforeCompletedSignal = true;
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);

        registry.Subscribe("conv", s =>
        {
            if (s.Status == ConversationRunStatus.Completed && persistence.CompletedCount == 0)
                persistedBeforeCompletedSignal = false;
        });

        var executor = new FakeExecutor((req, sink) => Task.FromResult("done"));
        registry.StartRun(NewRequest(), executor);

        await WaitUntilAsync(() => registry.GetActiveRun("conv")?.Status == ConversationRunStatus.Completed);
        Assert.True(persistedBeforeCompletedSignal, "Completed was published before persistence committed.");
        Assert.Equal(1, persistence.CompletedCount);
    }

    [Fact]
    public async Task Completion_WhenPersistenceFails_MarksFailedNotCompleted()
    {
        var persistence = new FakePersistence
        {
            OnCompleted = (_, _) => throw new Exception("disk full"),
        };
        var registry = new ConversationRunRegistry(persistence);

        var terminalStatuses = new List<ConversationRunStatus>();
        registry.Subscribe("conv", s => { if (s.IsTerminal) terminalStatuses.Add(s.Status); });

        registry.StartRun(NewRequest(), new FakeExecutor((_, _) => Task.FromResult("done")));

        await WaitUntilAsync(() => terminalStatuses.Count > 0);
        Assert.Equal(ConversationRunStatus.Failed, terminalStatuses.Single());
        Assert.DoesNotContain(ConversationRunStatus.Completed, terminalStatuses);
    }

    // ---- Scenario 5/6: exactly one terminal, cancel/complete race ----

    [Fact]
    public async Task Run_ReachesTerminalExactlyOnce_AndCancelAfterCompleteIsNoOp()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);

        var terminals = new List<ConversationRunSnapshot>();
        registry.Subscribe("conv", s => { if (s.IsTerminal) terminals.Add(s); });

        var request = NewRequest();
        registry.StartRun(request, new FakeExecutor((_, _) => Task.FromResult("done")));
        await WaitUntilAsync(() => terminals.Count > 0);

        // Cancelling a run that already finished must not add a second terminal.
        await registry.CancelRunAsync("conv", request.RunId);
        await Task.Delay(50);

        Assert.Single(terminals);
        Assert.Equal(ConversationRunStatus.Completed, terminals[0].Status);
        Assert.Equal(1, persistence.CompletedCount);
    }

    [Fact]
    public async Task Cancel_MidTurn_ProducesSingleCancelledTerminal()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);
        var started = new TaskCompletionSource();

        var executor = new FakeExecutor(async (req, sink) =>
        {
            sink.SetRunning();
            started.SetResult();
            await Task.Delay(Timeout.Infinite, sink.CancellationToken); // pends until cancelled
            return "unreachable";
        });

        var request = NewRequest();
        var terminals = new List<ConversationRunSnapshot>();
        registry.Subscribe("conv", s => { if (s.IsTerminal) terminals.Add(s); });
        registry.StartRun(request, executor);

        await started.Task;
        await registry.CancelRunAsync("conv", request.RunId);

        Assert.Single(terminals);
        Assert.Equal(ConversationRunStatus.Cancelled, terminals[0].Status);
        Assert.Equal(0, persistence.CompletedCount);
    }

    // ---- Scenario 7: closing the page (dispose subscription) never cancels the run ----

    [Fact]
    public async Task DisposingSubscription_DoesNotCancelRun()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);
        var release = new TaskCompletionSource();

        var executor = new FakeExecutor(async (req, sink) =>
        {
            sink.SetRunning();
            await release.Task;
            return "done";
        });
        registry.StartRun(NewRequest(), executor);

        var sub = registry.Subscribe("conv", _ => { });
        sub.Dispose(); // page unloaded

        release.SetResult();
        await WaitUntilAsync(() => registry.GetActiveRun("conv")?.Status == ConversationRunStatus.Completed);
        Assert.Equal(1, persistence.CompletedCount);
    }

    // ---- Scenario 8: tray mode — no subscriber at all, run still completes & persists ----

    [Fact]
    public async Task RunWithNoSubscribers_StillCompletesAndPersists()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);

        registry.StartRun(NewRequest(), new FakeExecutor((_, _) => Task.FromResult("saved")));

        await WaitUntilAsync(() => persistence.CompletedCount == 1);
        Assert.Single(persistence.Completed);
        Assert.Equal("saved", persistence.Completed.Single().reply);
    }

    // ---- Scenario 10 (headless part): one active run per conversation ----

    [Fact]
    public void StartingASecondRunForTheSameConversation_IsRejected()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);
        var release = new TaskCompletionSource();

        registry.StartRun(NewRequest("dup"), new FakeExecutor(async (_, sink) =>
        {
            sink.SetRunning();
            await release.Task;
            return "x";
        }));

        Assert.Throws<InvalidOperationException>(() =>
            registry.StartRun(NewRequest("dup"), new FakeExecutor((_, _) => Task.FromResult("y"))));

        release.SetResult();
    }

    // ---- Retry semantics ----

    [Fact]
    public async Task RetryRun_AfterFailure_ReusesUserMessage_ButMintsNewIds()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);

        var request = NewRequest();
        registry.StartRun(request, new FakeExecutor((_, _) => throw new Exception("boom")));
        await WaitUntilAsync(() => registry.GetActiveRun("conv")?.Status == ConversationRunStatus.Failed);

        var retry = registry.RetryRun("conv", new FakeExecutor((_, _) => Task.FromResult("recovered")));
        Assert.NotNull(retry);
        Assert.Equal(request.UserMessageId, retry!.UserMessageId);        // same user message
        Assert.NotEqual(request.RunId, retry.RunId);                      // new run
        Assert.NotEqual(request.AssistantMessageId, retry.AssistantMessageId);

        await WaitUntilAsync(() => registry.GetActiveRun("conv")?.Status == ConversationRunStatus.Completed);
    }

    // ---- One subscriber throwing does not affect the others ----

    [Fact]
    public async Task ThrowingSubscriber_DoesNotBreakOtherSubscribersOrTheRun()
    {
        var errors = new List<Exception>();
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence, onError: errors.Add);

        var good = new List<ConversationRunSnapshot>();
        registry.Subscribe("conv", _ => throw new Exception("bad handler"));
        registry.Subscribe("conv", good.Add);

        registry.StartRun(NewRequest(), new FakeExecutor((_, _) => Task.FromResult("done")));
        await WaitUntilAsync(() => good.Any(s => s.Status == ConversationRunStatus.Completed));

        Assert.NotEmpty(errors);
        Assert.Equal(1, persistence.CompletedCount);
    }

    // ---- Shutdown cancels in-flight runs within a bounded time ----

    [Fact]
    public async Task Shutdown_CancelsInFlightRuns_AndDoesNotBlockForever()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);
        var started = new TaskCompletionSource();

        registry.StartRun(NewRequest(), new FakeExecutor(async (_, sink) =>
        {
            sink.SetRunning();
            started.SetResult();
            await Task.Delay(Timeout.Infinite, sink.CancellationToken);
            return "x";
        }));

        await started.Task;
        var sw = Stopwatch.StartNew();
        await registry.ShutdownAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Empty(registry.GetActiveRuns());
    }

    /// <summary>
    /// A turn interrupted by app shutdown must be distinguishable from one the user stopped.
    /// Nothing told the host to stop, so its agent is very likely still running the turn —
    /// possibly blocked on an approval — and persistence relies on this code to leave the turn
    /// resumable rather than settling it as finished.
    /// </summary>
    [Fact]
    public async Task Shutdown_MarksInterruptedRunsWithTheShutdownCode_NotCancelled()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);
        var started = new TaskCompletionSource();

        registry.StartRun(NewRequest(), new FakeExecutor(async (_, sink) =>
        {
            sink.SetRunning();
            started.SetResult();
            await Task.Delay(Timeout.Infinite, sink.CancellationToken);
            return "x";
        }));

        await started.Task;
        await registry.ShutdownAsync(TimeSpan.FromSeconds(5));

        var snapshot = registry.GetActiveRun("conv");
        Assert.NotNull(snapshot);
        Assert.Equal(ConversationRunStatus.Cancelled, snapshot!.Status);
        Assert.Equal(RunErrorCodes.Shutdown, snapshot.ErrorCode);
    }

    /// <summary>The counterpart: an ordinary cancel outside shutdown keeps the plain code, so a
    /// user pressing Stop never leaves a turn that the next app launch tries to rejoin.</summary>
    [Fact]
    public async Task CancelRun_OutsideShutdown_UsesTheCancelledCode()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);
        var started = new TaskCompletionSource();

        var run = registry.StartRun(NewRequest(), new FakeExecutor(async (_, sink) =>
        {
            sink.SetRunning();
            started.SetResult();
            await Task.Delay(Timeout.Infinite, sink.CancellationToken);
            return "x";
        }));

        await started.Task;
        await registry.CancelRunAsync("conv", run.RunId);

        var snapshot = registry.GetActiveRun("conv");
        Assert.Equal(ConversationRunStatus.Cancelled, snapshot!.Status);
        Assert.Equal(RunErrorCodes.Cancelled, snapshot.ErrorCode);
    }

    // ---- Memory: what a finished turn is allowed to leave behind ----

    [Fact]
    public async Task TerminalRun_IsPublishedWithItsEvents_ButRetainsNoneOfThem()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);

        var received = new List<ConversationRunSnapshot>();
        using var _ = registry.Subscribe("conv", s => received.Add(s));

        registry.StartRun(NewRequest(), new FakeExecutor((_, sink) =>
        {
            sink.SetRunning();
            sink.Publish(Event("llm_call"));
            sink.Publish(Event("tool_call"));
            return Task.FromResult("done");
        }));

        await WaitUntilAsync(() => received.Any(s => s.IsTerminal));

        // The live page still gets the whole tail to project on the terminal snapshot...
        var terminal = received.Last(s => s.IsTerminal);
        Assert.Equal(2, terminal.Events.Count);

        // ...but the run's raw stream (every LLM call's message history, every base64 image) is
        // not what the registry keeps around afterwards. It has been projected and persisted;
        // holding it would pin a turn's worth of JSON per conversation for the process's life.
        var retained = registry.GetActiveRun("conv");
        Assert.NotNull(retained);
        Assert.Equal(ConversationRunStatus.Completed, retained!.Status);
        Assert.Empty(retained.Events);
    }

    [Fact]
    public async Task TerminalSnapshots_AreCapacityBounded()
    {
        var registry = new ConversationRunRegistry(new FakePersistence());

        for (var i = 0; i < 70; i++)
        {
            var conversationId = $"bounded-{i}";
            registry.StartRun(
                NewRequest(conversationId),
                new FakeExecutor((_, _) => Task.FromResult($"reply-{i}")));
            await WaitUntilAsync(() => registry.GetActiveRun(conversationId)?.IsTerminal == true);
            await WaitUntilAsync(() => !registry.IsRunActive(conversationId));
        }

        Assert.Null(registry.GetActiveRun("bounded-0"));
        Assert.NotNull(registry.GetActiveRun("bounded-69"));
    }

    [Fact]
    public async Task IsRunActive_GoesFalseOnceTerminal_EvenThoughGetActiveRunStillAnswers()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);

        registry.StartRun(NewRequest(), new FakeExecutor((_, _) => Task.FromResult("done")));
        await WaitUntilAsync(() => registry.GetActiveRun("conv")?.IsTerminal == true);

        // The distinction the socket-reclamation path depends on: GetActiveRun deliberately keeps
        // answering with the last terminal snapshot, so it cannot be used to ask "busy right now?"
        // — doing so reported every conversation that had ever run as busy, and the idle-socket
        // trim silently reclaimed nothing for the life of the process.
        Assert.NotNull(registry.GetActiveRun("conv"));
        Assert.False(registry.IsRunActive("conv"));
        Assert.False(registry.IsRunActive("never-run"));
    }

    [Fact]
    public async Task Forget_DeletedConversation_LeavesNoSnapshotOrRetryableBehind()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);

        registry.StartRun(NewRequest(), new FakeExecutor((_, _) => throw new Exception("boom")));
        await WaitUntilAsync(() => registry.GetActiveRun("conv")?.Status == ConversationRunStatus.Failed);

        // Both maps are populated at this point: a terminal snapshot (holding the reply text) and
        // a retryable request (holding the turn's attachments).
        Assert.NotNull(registry.GetActiveRun("conv"));

        registry.Forget("conv");

        Assert.Null(registry.GetActiveRun("conv"));
        Assert.Null(registry.RetryRun("conv", new FakeExecutor((_, _) => Task.FromResult("x"))));
    }

    [Fact]
    public async Task Forget_DoesNotDisturbAnotherConversationsRun()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);

        registry.StartRun(NewRequest("keep"), new FakeExecutor((_, _) => Task.FromResult("kept")));
        await WaitUntilAsync(() => registry.GetActiveRun("keep")?.IsTerminal == true);

        registry.Forget("deleted");

        Assert.NotNull(registry.GetActiveRun("keep"));
    }

    [Fact]
    public async Task FailedRun_StopsBeingRetryable_OnceItsRequestHasExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence, clock: () => now);

        registry.StartRun(NewRequest(), new FakeExecutor((_, _) => throw new Exception("boom")));
        await WaitUntilAsync(() => registry.GetActiveRun("conv")?.Status == ConversationRunStatus.Failed);

        // A retry offered promptly still works...
        Assert.NotNull(registry.RetryRun("conv", new FakeExecutor((_, _) => Task.FromResult("recovered"))));
        await WaitUntilAsync(() => registry.GetActiveRun("conv")?.Status == ConversationRunStatus.Completed);
        // Terminal publication precedes removal from the active dictionary. Wait for both so the
        // next StartRun does not race that final cleanup on a busy test runner.
        await WaitUntilAsync(() => !registry.IsRunActive("conv"));

        registry.StartRun(NewRequest(), new FakeExecutor((_, _) => throw new Exception("boom")));
        await WaitUntilAsync(() => registry.GetActiveRun("conv")?.Status == ConversationRunStatus.Failed);
        await WaitUntilAsync(() => !registry.IsRunActive("conv"));

        // ...but a failed turn nobody ever retried must not pin its request — and a TurnRequest
        // carries the turn's images and files as base64, so that is a whole payload per abandoned
        // conversation held for the process's life.
        now = now.AddHours(1);
        Assert.Null(registry.RetryRun("conv", new FakeExecutor((_, _) => Task.FromResult("recovered"))));
    }

    [Fact]
    public async Task AppendingEvents_DoesNotMutateSnapshotsAlreadyHandedOut()
    {
        var persistence = new FakePersistence();
        var registry = new ConversationRunRegistry(persistence);
        var release = new TaskCompletionSource();

        // The second batch takes the append-only buffer past its capacity and grows it, which is
        // exactly where a snapshot that shares the backing array would go wrong.
        registry.StartRun(NewRequest(), new FakeExecutor(async (_, sink) =>
        {
            sink.SetRunning();
            for (var i = 0; i < 8; i++) sink.Publish(Event($"early{i}"));
            await release.Task;
            for (var i = 0; i < 24; i++) sink.Publish(Event($"late{i}"));
            return "done";
        }));

        await WaitUntilAsync(() => registry.GetActiveRun("conv")?.Events.Count == 8);
        var early = registry.GetActiveRun("conv")!;
        var earlyCount = early.Events.Count;
        var earlyLast = early.Events[^1].Type;

        release.SetResult();
        await WaitUntilAsync(() => registry.GetActiveRun("conv")!.IsTerminal);

        // A snapshot is a view of the buffer as it stood, not a window onto its future.
        Assert.Equal(earlyCount, early.Events.Count);
        Assert.Equal(earlyLast, early.Events[^1].Type);
    }

    // ---- fakes ----

    private sealed class FakeExecutor : ITurnExecutor
    {
        private readonly Func<TurnRequest, IRunSink, Task<string>> _body;
        public FakeExecutor(Func<TurnRequest, IRunSink, Task<string>> body) => _body = body;
        public Task<string> ExecuteAsync(TurnRequest request, IRunSink sink) => _body(request, sink);
    }

    private sealed class FakePersistence : IRunPersistence
    {
        public int CompletedCount;
        public int FailedCount;
        public readonly ConcurrentBag<(string assistantId, string reply)> Completed = new();
        public Func<ConversationRunSnapshot, string, Task>? OnCompleted;

        public Task PersistCompletedAsync(ConversationRunSnapshot snapshot, string finalReply)
        {
            if (OnCompleted is not null) return OnCompleted(snapshot, finalReply);
            Interlocked.Increment(ref CompletedCount);
            Completed.Add((snapshot.AssistantMessageId, finalReply));
            return Task.CompletedTask;
        }

        public Task PersistFailedAsync(ConversationRunSnapshot snapshot)
        {
            Interlocked.Increment(ref FailedCount);
            return Task.CompletedTask;
        }
    }
}
