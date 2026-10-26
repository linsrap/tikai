using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tikai.Core.Jobs;

namespace Tikai.Data.Jobs;

/// <summary>
/// Job queue on the jobs table. Several workers dequeue safely with FOR UPDATE SKIP LOCKED.
/// Lease and retry times use the database clock (now()), so all workers share one clock.
/// </summary>
internal sealed class PostgresJobQueue(TikaiDbContext db, IOptions<JobQueueOptions> options, TimeProvider timeProvider) : IJobQueue
{
    public async Task<Guid> EnqueueAsync(
        string queue,
        string payload,
        DateTimeOffset? runAt = null,
        int maxAttempts = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        var id = Guid.CreateVersion7(timeProvider.GetUtcNow());
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO jobs (id, queue, payload, run_at, attempts, max_attempts)
            VALUES ({id}, {queue}, {payload}::jsonb, COALESCE({runAt}::timestamptz, now()), 0, {maxAttempts})
            """,
            cancellationToken);
        return id;
    }

    public async Task<JobLease?> DequeueAsync(string queue, string workerId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        var seconds = lease.TotalSeconds;

        // The CTE first buries jobs whose lease ran out after their last attempt;
        // then one due job is claimed, skipping rows other workers have locked.
        var rows = await db.Database.SqlQuery<LeasedRow>(
            $"""
            WITH buried AS (
                UPDATE jobs
                SET dead_at = now(), locked_by = NULL, locked_until = NULL, updated_at = now(),
                    last_error = 'Lease expired after the last attempt.'
                WHERE queue = {queue} AND dead_at IS NULL AND locked_until < now() AND attempts >= max_attempts
            )
            UPDATE jobs
            SET locked_by = {workerId}, locked_until = now() + make_interval(secs => {seconds}),
                attempts = attempts + 1, updated_at = now()
            WHERE id = (
                SELECT id FROM jobs
                WHERE queue = {queue} AND dead_at IS NULL AND run_at <= now()
                  AND (locked_until IS NULL OR locked_until < now())
                  AND attempts < max_attempts
                ORDER BY run_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT 1)
            RETURNING id, queue, payload::text AS payload, attempts, max_attempts, locked_until
            """).ToListAsync(cancellationToken); // the snake_case convention also maps LeasedRow's columns

        return rows is [var row]
            ? new JobLease(row.Id, row.Queue, row.Payload, row.Attempts, row.MaxAttempts, workerId, row.LockedUntil)
            : null;
    }

    public async Task<bool> RenewAsync(JobLease job, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var seconds = lease.TotalSeconds;
        var updated = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE jobs SET locked_until = now() + make_interval(secs => {seconds}), updated_at = now()
            WHERE id = {job.Id} AND locked_by = {job.WorkerId} AND attempts = {job.Attempts} AND dead_at IS NULL
            """,
            cancellationToken);
        return updated == 1;
    }

    public async Task<bool> CompleteAsync(JobLease job, CancellationToken cancellationToken = default)
    {
        var deleted = await db.Database.ExecuteSqlAsync(
            $"DELETE FROM jobs WHERE id = {job.Id} AND locked_by = {job.WorkerId} AND attempts = {job.Attempts}",
            cancellationToken);
        return deleted == 1;
    }

    public async Task<bool> FailAsync(JobLease job, string errorMessage, CancellationToken cancellationToken = default)
    {
        var backoff = RetryPolicy.Backoff(job.Attempts, options.Value.RetryBaseDelay, options.Value.RetryMaxDelay).TotalSeconds;
        var updated = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE jobs
            SET locked_by = NULL, locked_until = NULL, last_error = {errorMessage}, updated_at = now(),
                run_at = CASE WHEN attempts >= max_attempts THEN run_at ELSE now() + make_interval(secs => {backoff}) END,
                dead_at = CASE WHEN attempts >= max_attempts THEN now() END
            WHERE id = {job.Id} AND locked_by = {job.WorkerId} AND attempts = {job.Attempts}
            """,
            cancellationToken);
        return updated == 1;
    }

    private sealed record LeasedRow(Guid Id, string Queue, string Payload, int Attempts, int MaxAttempts, DateTimeOffset LockedUntil);
}
