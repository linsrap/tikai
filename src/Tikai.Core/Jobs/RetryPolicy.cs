namespace Tikai.Core.Jobs;

/// <summary>Exponential backoff between job attempts: base, 2×base, 4×base, … up to a cap.</summary>
public static class RetryPolicy
{
    public static readonly TimeSpan DefaultBase = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultMax = TimeSpan.FromMinutes(10);

    /// <param name="attempts">Attempts made so far (1 after the first failure).</param>
    public static TimeSpan Backoff(int attempts, TimeSpan? baseDelay = null, TimeSpan? max = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        var first = baseDelay ?? DefaultBase;
        var cap = max ?? DefaultMax;

        // Cap the exponent so the multiplication cannot overflow.
        var factor = Math.Pow(2, Math.Min(attempts - 1, 30));
        var delay = TimeSpan.FromTicks((long)Math.Min(first.Ticks * factor, cap.Ticks));
        return delay;
    }
}
