using Tikai.Core.Jobs;

namespace Tikai.Data.Jobs;

public sealed class JobQueueOptions
{
    /// <summary>Delay before the second attempt; doubles with each further attempt.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = RetryPolicy.DefaultBase;

    public TimeSpan RetryMaxDelay { get; set; } = RetryPolicy.DefaultMax;
}
