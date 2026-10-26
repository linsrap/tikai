namespace Tikai.Core.Jobs;

/// <summary>
/// Durable job queue shared by several workers. A dequeued job is leased to one worker;
/// if the worker neither completes nor fails it before the lease ends, another worker gets it.
/// </summary>
public interface IJobQueue
{
    /// <summary>Adds a job. Joins the caller's open database transaction, if any.</summary>
    /// <param name="payload">JSON text; the handler for the queue deserializes it.</param>
    Task<Guid> EnqueueAsync(
        string queue,
        string payload,
        DateTimeOffset? runAt = null,
        int maxAttempts = 5,
        CancellationToken cancellationToken = default);

    /// <summary>Takes the next due job of the queue, or returns null when there is none.</summary>
    Task<JobLease?> DequeueAsync(string queue, string workerId, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Extends the lease. Returns false if the worker no longer holds the job.</summary>
    Task<bool> RenewAsync(JobLease job, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Removes a finished job. Returns false if the worker no longer holds the job.</summary>
    Task<bool> CompleteAsync(JobLease job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a failed job for a retry after a backoff, or marks it dead after its last attempt.
    /// Returns false if the worker no longer holds the job.
    /// </summary>
    Task<bool> FailAsync(JobLease job, string errorMessage, CancellationToken cancellationToken = default);
}
