using System;

namespace ConnectOnion.Protocol;

/// <summary>
/// Sets how long to wait before retrying a dropped agent connection. Retries use
/// exponential backoff by default, or a configured interval, with a small random variation
/// so multiple connections do not all retry at once. The caller runs the retry loop.
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
    private readonly Func<TimeSpan>? _fixedDelayProvider;

    /// <param name="seed">Fixed seed for deterministic tests. Omit in production so every
    /// process (and so every client) draws a different jitter sequence.</param>
    /// <param name="fixedDelayProvider">Optional application setting for a fixed nominal delay
    /// between attempts. Without it, the protocol default remains exponential backoff.</param>
    public ReconnectPolicy(int? seed = null, Func<TimeSpan>? fixedDelayProvider = null)
    {
        _random = seed is { } s ? new Random(s) : new Random();
        _fixedDelayProvider = fixedDelayProvider;
    }

    /// <summary>Whether an <paramref name="attempt"/>th retry (1-based) is allowed at all.</summary>
    public static bool ShouldRetry(int attempt) => attempt >= 1 && attempt <= MaxAttempts;

    /// <summary>
    /// Returns the wait before a retry. By default, the wait doubles from 1 to 16 seconds.
    /// A configured interval is used for every retry both of these include up to 20% random variation.
    /// Throws if <paramref name="attempt"/> is outside the allowed range; check
    /// <see cref="ShouldRetry"/> before calling.
    /// </summary>
    public TimeSpan DelayFor(int attempt)
    {
        if (!ShouldRetry(attempt))
        {
            throw new ArgumentOutOfRangeException(
                nameof(attempt), attempt, $"Attempt must be between 1 and {MaxAttempts}.");
        }

        // 1 << (attempt - 1) rather than Math.Pow: the exponent is small and integral, and this
        // keeps the default doubling exact instead of accumulating floating-point drift.
        var nominal = _fixedDelayProvider?.Invoke().TotalMilliseconds
            ?? BaseDelay.TotalMilliseconds * (1 << (attempt - 1));
        if (nominal <= 0 || double.IsNaN(nominal) || double.IsInfinity(nominal))
            throw new InvalidOperationException("Reconnect delay must be a finite positive duration.");

        // NextDouble() is [0,1) → the multiplier spans [0.8, 1.2).
        var jittered = nominal * (1.0 + ((_random.NextDouble() * 2.0 - 1.0) * JitterFactor));
        return TimeSpan.FromMilliseconds(jittered);
    }
}
