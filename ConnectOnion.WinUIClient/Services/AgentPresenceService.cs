using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>Online status the UI shows for an agent.</summary>
public enum AgentPresence
{
    /// <summary>Not probed yet this session.</summary>
    Unknown,
    /// <summary>A reachability probe is in flight.</summary>
    Checking,
    /// <summary>The agent answered the probe.</summary>
    Online,
    /// <summary>The probe failed or the agent is registered but offline.</summary>
    Offline,
}

/// <summary>
/// Process-wide cache of agent online status. Confirming presence is a lightweight
/// reachability probe (<see cref="ConnectionTester"/>): a <c>/health</c> hit for
/// Direct URL agents, a relay lookup for address agents. Results are cached per
/// agent for the app session, so navigating back to a page shows the green dot
/// without re-probing; a page can force a fresh probe via <see cref="RefreshAsync"/>.
///
/// <see cref="PresenceChanged"/> may fire off the UI thread (the probe completes on
/// a thread-pool continuation) — subscribers must marshal onto the UI thread before
/// touching bound state.
/// </summary>
public sealed class AgentPresenceService
{
    /// <summary>How long an <c>Online</c> answer is reused before the next sweep re-probes.
    /// <para>Five minutes rather than one. This is not a timer — a sweep happens when the sidebar
    /// refreshes, which is on every navigation — so a short TTL turns ordinary page-to-page
    /// movement into a steady stream of HTTP, one request per agent per minute, each carrying
    /// two Polly retries. An agent that goes down between sweeps is discovered the moment the
    /// user actually sends to it, which is the case that matters; a stale green dot for a few
    /// minutes is not.</para></summary>
    private static readonly TimeSpan OnlineTtl = TimeSpan.FromMinutes(5);

    /// <summary>Offline is re-probed far sooner: the user is likely waiting for the agent to
    /// come back, and that is the transition worth catching quickly.</summary>
    private static readonly TimeSpan OfflineTtl = TimeSpan.FromSeconds(30);

    /// <summary>Caps how many reachability probes are in flight at once.
    /// <para>The sidebar sweeps agents in a plain <c>foreach</c>, so without this every agent
    /// probes simultaneously — and each probe is an HTTP call with a retry pipeline behind it.
    /// On a sidebar with a dozen agents that is a burst of connections and thread-pool work on
    /// a path the user did not ask for. Four keeps a sweep prompt without the stampede.</para></summary>
    private const int MaxConcurrentProbes = 4;

    private static readonly SemaphoreSlim ProbeGate = new(MaxConcurrentProbes, MaxConcurrentProbes);

    private readonly object _gate = new();
    private readonly Dictionary<string, AgentPresence> _presence = new();
    private readonly Dictionary<string, string> _details = new();
    private readonly Dictionary<string, DateTimeOffset> _checkedAt = new();
    private readonly Dictionary<string, Task> _inflight = new();
    private readonly ConnectionTester _connectionTester;

    public AgentPresenceService(ConnectionTester connectionTester)
        => _connectionTester = connectionTester;

    /// <summary>Raised (possibly off the UI thread) when an agent's presence changes. Carries the agent id.</summary>
    public event Action<string>? PresenceChanged;

    public AgentPresence GetPresence(string agentId)
    {
        lock (_gate) return _presence.TryGetValue(agentId, out var p) ? p : AgentPresence.Unknown;
    }

    public string GetDetail(string agentId)
    {
        lock (_gate) return _details.TryGetValue(agentId, out var d) ? d : "";
    }

    /// <summary>Drops process-wide status for a deleted agent. If a probe is already in flight,
    /// remove its result once it completes as well so the late continuation cannot resurrect the
    /// deleted key.</summary>
    public void Forget(string agentId)
    {
        Task? pending;
        lock (_gate)
        {
            _presence.Remove(agentId);
            _details.Remove(agentId);
            _checkedAt.Remove(agentId);
            _inflight.TryGetValue(agentId, out pending);
        }

        if (pending is not null)
            _ = pending.ContinueWith(
                _ => Forget(agentId),
                System.Threading.CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    /// <summary>
    /// Confirms the agent is online if the cached answer is missing or stale.
    /// Recent Online and Offline answers are reused, and concurrent callers are
    /// coalesced onto a single probe.
    /// </summary>
    public Task EnsureCheckedAsync(AgentConfig agent)
        => EnsureCheckedAsync(agent.Id, agent.Address, agent.DirectUrl);

    /// <summary>
    /// The same, for callers that hold an agent's endpoint without holding an
    /// <see cref="AgentConfig"/>.
    ///
    /// <para>A probe needs exactly these three values, so requiring the full record forced callers
    /// to read every agent — and every agent's cached <c>/info</c> blob — just to ask whether one
    /// of them is reachable. The sidebar drives presence from its own row items, which already
    /// carry the endpoint.</para>
    /// </summary>
    public Task EnsureCheckedAsync(string agentId, string address, string? directUrl)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return Task.CompletedTask;

        lock (_gate)
        {
            if (_inflight.TryGetValue(agentId, out var running)) return running;
            if (!ShouldProbeLocked(agentId, DateTimeOffset.UtcNow)) return Task.CompletedTask;
            return StartProbeLocked(agentId, address, directUrl);
        }
    }

    /// <summary>Forces a fresh reachability probe, reusing any probe already running for this agent.</summary>
    public Task RefreshAsync(AgentConfig agent)
    {
        lock (_gate)
        {
            if (_inflight.TryGetValue(agent.Id, out var running)) return running;
            // A user-initiated refresh bypasses the EndpointResolver relay-lookup
            // cache (which holds an "offline" answer for ~30s) — otherwise clicking
            // Recheck right after the agent comes back up keeps reporting offline.
            return StartProbeLocked(agent.Id, agent.Address, agent.DirectUrl, forceRefresh: true);
        }
    }

    private Task StartProbeLocked(
        string agentId, string address, string? directUrl, bool forceRefresh = false)
    {
        var task = ProbeAsync(agentId, address, directUrl, forceRefresh);
        _inflight[agentId] = task;
        return task;
    }

    private bool ShouldProbeLocked(string agentId, DateTimeOffset now)
    {
        if (!_presence.TryGetValue(agentId, out var presence)) return true;
        if (!_checkedAt.TryGetValue(agentId, out var checkedAt)) return true;

        var ttl = presence switch
        {
            AgentPresence.Online => OnlineTtl,
            AgentPresence.Offline => OfflineTtl,
            AgentPresence.Checking => TimeSpan.FromSeconds(10),
            _ => TimeSpan.Zero,
        };
        return ttl == TimeSpan.Zero || now - checkedAt >= ttl;
    }

    private async Task ProbeAsync(
        string agentId, string address, string? directUrl, bool forceRefresh)
    {
        // Yield before touching shared state so RefreshAsync can release _gate and
        // register this task before Set() re-enters the lock / fires the event.
        await Task.Yield();

        // Checking is published before queueing, not after: the row should read "checking" while
        // it waits its turn, otherwise a large sweep leaves most agents looking untouched.
        Set(agentId, AgentPresence.Checking, "Checking if the agent is online…");
        var entered = false;
        try
        {
            await ProbeGate.WaitAsync().ConfigureAwait(false);
            entered = true;

            // A lean /health reachability probe. The detail page fetches and persists /info
            // independently, so list/status refreshes never download metadata.
            var result = await _connectionTester.TestAsync(
                address, directUrl, forceRefresh: forceRefresh);
            Set(agentId, result.Ok ? AgentPresence.Online : AgentPresence.Offline, result.Detail);
        }
        catch (Exception ex)
        {
            Set(agentId, AgentPresence.Offline, ex.Message);
        }
        finally
        {
            if (entered) ProbeGate.Release();
            lock (_gate) _inflight.Remove(agentId);
        }
    }

    private void Set(string agentId, AgentPresence presence, string detail)
    {
        lock (_gate)
        {
            _presence[agentId] = presence;
            _details[agentId] = detail;
            _checkedAt[agentId] = DateTimeOffset.UtcNow;
        }
        PresenceChanged?.Invoke(agentId);
    }
}
