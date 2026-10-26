namespace Tikai.Core.Jobs;

/// <summary>A job taken from the queue by one worker until <see cref="LockedUntil"/>.</summary>
/// <param name="Attempts">Number of times the job has been taken, including this one.</param>
public sealed record JobLease(
    Guid Id,
    string Queue,
    string Payload,
    int Attempts,
    int MaxAttempts,
    string WorkerId,
    DateTimeOffset LockedUntil);
