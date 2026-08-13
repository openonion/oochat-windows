using System.Reflection;
using ConnectOnion.WinUIClient.Data;

namespace ConnectOnion.IntegrationTests.Database;

/// <summary>
/// <see cref="IdentityStore"/> blocks on <c>AppDatabase.OpenAsync().GetAwaiter().GetResult()</c>,
/// and it is called during startup on the UI thread — a thread that has a
/// <see cref="SynchronizationContext"/> and is, at that moment, blocked. Any await in the schema
/// initialization chain that genuinely yields would post its continuation back to that thread and
/// hang the app on first run, before a window ever appears.
///
/// <para>The comment on that call used to justify it as "AppDatabase.OpenAsync awaits with
/// ConfigureAwait(false) the whole way down". That was not accurate — the `await using`
/// declarations in the chain await <c>DisposeAsync</c> without it, and CA2007 flags 30 such sites
/// in <c>AppDatabase</c> alone. The real reason it is safe is narrower: Microsoft.Data.Sqlite's
/// async methods are synchronous internally (SQLite has no async I/O), so nothing in the chain
/// ever yields and no continuation is ever posted.</para>
///
/// <para>That distinction is why this test asserts on <b>behaviour</b> rather than on the presence
/// of <c>ConfigureAwait</c> in the source. Counting <c>ConfigureAwait</c> calls would pass against
/// a chain that deadlocks and fail against one that is perfectly safe; running the blocking call
/// on a thread that cannot pump is the only check that answers the actual question.</para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class IdentityStoreBlockingContractTests
{
    /// <summary>
    /// Generous, because it is a deadlock detector rather than a performance budget: first-run
    /// schema creation on a cold file is the slow case, and a timeout that fires on a slow machine
    /// would be read as the very deadlock it is meant to catch.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public void EnsureIdentity_OnABlockedThreadWithASynchronizationContext_DoesNotDeadlock()
    {
        ResetStaticState();

        Exception? failure = null;
        var completed = new ManualResetEventSlim(false);
        var context = new BlockedThreadContext();

        // A foreground thread of our own, not the pool: the pool has no SynchronizationContext, so
        // running there would make the test pass for the wrong reason — it would never exercise
        // the capture that causes the deadlock.
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                // The call under test. If anything in the chain yields, its continuation is posted
                // to `context` — which cannot run it, because this thread is inside
                // GetAwaiter().GetResult() — and the post is recorded for the assertions below.
                var identity = IdentityStore.EnsureIdentity();
                Assert.False(string.IsNullOrWhiteSpace(identity.Address));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = true,
            Name = nameof(EnsureIdentity_OnABlockedThreadWithASynchronizationContext_DoesNotDeadlock),
        };

        thread.Start();
        var finished = completed.Wait(Budget);

        // Asserted first: a posted continuation is the *cause*, and it is detectable even in runs
        // where the call happens to return anyway. Reporting the cause beats reporting the timeout.
        Assert.False(
            context.WasPostedTo,
            "A continuation was posted back to the blocked startup thread. The AppDatabase "
            + "initialization chain must not contain an await that actually yields — on the real UI "
            + "thread this is a first-run startup deadlock. Adding ConfigureAwait(false) will not "
            + "fix it; make IdentityStore's load path async.");

        Assert.True(
            finished,
            $"IdentityStore.EnsureIdentity did not return within {Budget.TotalSeconds:N0}s on a thread "
            + "with a SynchronizationContext. Something in the AppDatabase initialization chain now "
            + "yields, so its continuation is queued behind the blocking call that is waiting for it. "
            + "Adding ConfigureAwait(false) will not fix this — make IdentityStore's load path async.");

        Assert.True(
            failure is null,
            $"IdentityStore.EnsureIdentity failed on a synchronization-context thread: {failure}");
    }

    /// <summary>
    /// Stands in for a UI thread that is currently blocked. <c>Post</c> is what a captured context
    /// calls to resume a continuation, and here it can never succeed, because the thread that would
    /// run it is inside a blocking wait.
    ///
    /// <para>It <b>records</b> rather than throws. Post is invoked on whichever thread completed
    /// the awaited operation — not on the blocked thread and not inside the test's try/catch — so
    /// throwing produced an unhandled exception on a foreign thread, which xUnit reports as a
    /// "Catastrophic failure" and which can take the whole test host down with it. Recording keeps
    /// the diagnosis and lets the assertion below report it as an ordinary failure.</para>
    /// </summary>
    private sealed class BlockedThreadContext : SynchronizationContext
    {
        private int _violations;

        public bool WasPostedTo => Volatile.Read(ref _violations) > 0;

        public override void Post(SendOrPostCallback d, object? state)
            => Interlocked.Increment(ref _violations);

        public override void Send(SendOrPostCallback d, object? state)
            => Interlocked.Increment(ref _violations);
    }

    /// <summary>Mirrors <c>IdentityStoreTests.ResetStaticState</c>: the store caches its identity
    /// in a static, so a test that wants to exercise the load path has to clear it first.</summary>
    private static void ResetStaticState()
    {
        var type = typeof(IdentityStore);
        type.GetField("_cached", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        type.GetField("<WasReset>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
        type.GetField("<ResetReason>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        type.GetField("<NewlyCreatedMnemonic>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, null);
    }
}
