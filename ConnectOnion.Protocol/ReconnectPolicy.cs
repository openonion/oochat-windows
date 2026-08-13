using System;

namespace ConnectOnion.Protocol;

/// <summary>
/// The retry schedule for a dropped agent socket: at most <see cref="MaxAttempts"/> attempts,
/// exponential backoff from <see cref="BaseDelay"/>, each delay carrying random jitter.
/// <para>Jitter is not decoration. Every open conversation reconnects off the same trigger —
/// a dropped wifi link, a host restart, a laptop waking — so an unjittered schedule has every
/// socket in the app retrying in lockstep, hitting the host in synchronized waves at 1s, 2s,
/// 4s… A host that dropped them because it was overloaded gets the same thundering herd back
/// on a fixed cadence, which is how a brief outage turns into a long one.</para>
/// <para>Pure and clock-free so the schedule can be asserted in tests without waiting out a
/// real backoff; the caller owns the delay and the attempt loop.</para>
/// </summary>
public sealed class ReconnectPolicy
{
    /// <summary>Give up after this many consecutive failures. The turn is then failed for real
    /// and the user gets a Retry button — an app that retries forever looks hung.</summary>
    public const int MaxAttempts = 5;

    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);

    /// <summary>Fraction of the nominal delay that jitter may add or subtract (±20%).
    /// Deliberately modest: enough to break lockstep between sockets, not so much that the
    /// schedule stops being recognizably 1/2/4/8/16 when read out of a log.</summary>
    private const double JitterFactor = 0.2;

    private readonly Random _random;

    /// <param name="seed">Fixed seed for deterministic tests. Omit in production so every
    /// process (and so every client) draws a different jitter sequence.</param>
    public ReconnectPolicy(int? seed = null)
        => _random = seed is { } s ? new Random(s) : new Random();

    /// <summary>Whether an <paramref name="attempt"/>th retry (1-based) is allowed at all.</summary>
    public static bool ShouldRetry(int attempt) => attempt >= 1 && attempt <= MaxAttempts;

    /// <summary>
    /// The delay before retry <paramref name="attempt"/> (1-based): 1s, 2s, 4s, 8s, 16s,
    /// each ±20%. Throws for an attempt outside the schedule — callers must gate on
    /// <see cref="ShouldRetry"/> first rather than relying on a clamped value, so a loop that
    /// forgets its bound fails loudly instead of retrying at 16s forever.
    /// </summary>
    public TimeSpan DelayFor(int attempt)
    {
        if (!ShouldRetry(attempt))
        {
            throw new ArgumentOutOfRangeException(
                nameof(attempt), attempt, $"Attempt must be between 1 and {MaxAttempts}.");
        }

        // 1 << (attempt - 1) rather than Math.Pow: the exponent is small and integral, and this
        // keeps the doubling exact instead of accumulating floating-point drift.
        var nominal = BaseDelay.TotalMilliseconds * (1 << (attempt - 1));
        // NextDouble() is [0,1) → the multiplier spans [0.8, 1.2).
        var jittered = nominal * (1.0 + ((_random.NextDouble() * 2.0 - 1.0) * JitterFactor));
        return TimeSpan.FromMilliseconds(jittered);
    }
}
