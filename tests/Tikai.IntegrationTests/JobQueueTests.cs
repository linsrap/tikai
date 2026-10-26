using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Tikai.Core.Jobs;
using Tikai.Data.Jobs;

namespace Tikai.IntegrationTests;

public class JobQueueTests(PostgresFixture postgres)
{
    private static readonly TimeSpan _lease = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Two_workers_never_take_the_same_job()
    {
        const int jobCount = 200;
        const int workerCount = 8;
        var queue = PostgresFixture.UniqueName("q");
        await EnqueueManyAsync(queue, jobCount);

        var taken = new ConcurrentBag<(Guid JobId, string Worker)>();
        var workers = Enumerable.Range(1, workerCount).Select(n => Task.Run(async () =>
        {
            await using var db = postgres.CreateContext();
            var jobs = PostgresFixture.CreateQueue(db);
            while (await jobs.DequeueAsync(queue, $"worker-{n}", _lease, TestContext.Current.CancellationToken) is { } job)
            {
                taken.Add((job.Id, job.WorkerId));
                Assert.True(await jobs.CompleteAsync(job, TestContext.Current.CancellationToken));
            }
        }));
        await Task.WhenAll(workers);

        Assert.Equal(jobCount, taken.Count);
        Assert.Equal(jobCount, taken.Select(t => t.JobId).Distinct().Count());
        Assert.True(taken.Select(t => t.Worker).Distinct().Count() > 1, "expected the work to be spread over several workers");
        Assert.Equal(0, await CountAsync(queue));
    }

    [Fact]
    public async Task A_crashed_workers_job_is_retried_after_its_lease_ends()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = PostgresFixture.UniqueName("q");
        await using var db = postgres.CreateContext();
        var jobs = PostgresFixture.CreateQueue(db);
        var id = await jobs.EnqueueAsync(queue, """{"n":1}""", cancellationToken: ct);

        // Worker 1 takes the job with a short lease and then "crashes" (never completes it).
        var first = await jobs.DequeueAsync(queue, "worker-1", TimeSpan.FromSeconds(2), ct);
        Assert.NotNull(first);
        Assert.Equal(1, first.Attempts);

        // While the lease runs, nobody else gets it.
        Assert.Null(await jobs.DequeueAsync(queue, "worker-2", _lease, ct));

        var second = await PollAsync(() => jobs.DequeueAsync(queue, "worker-2", _lease, ct), TimeSpan.FromSeconds(10));
        Assert.Equal(id, second.Id);
        Assert.Equal(2, second.Attempts);
        Assert.Equal("worker-2", second.WorkerId);
        Assert.Equal("""{"n": 1}""", second.Payload); // jsonb normalizes whitespace

        // The crashed worker's late completion does not count; the new holder's does.
        Assert.False(await jobs.CompleteAsync(first, ct));
        Assert.True(await jobs.CompleteAsync(second, ct));
        Assert.Equal(0, await CountAsync(queue));
    }

    [Fact]
    public async Task A_failed_job_is_retried_after_the_backoff_and_dies_after_its_last_attempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = PostgresFixture.UniqueName("q");
        await using var db = postgres.CreateContext();
        var jobs = PostgresFixture.CreateQueue(db, new JobQueueOptions { RetryBaseDelay = TimeSpan.FromSeconds(1) });
        var id = await jobs.EnqueueAsync(queue, "{}", maxAttempts: 2, cancellationToken: ct);

        var first = await jobs.DequeueAsync(queue, "w", _lease, ct);
        Assert.NotNull(first);
        Assert.True(await jobs.FailAsync(first, "boom 1", ct));
        Assert.Null(await jobs.DequeueAsync(queue, "w", _lease, ct)); // still in backoff

        var second = await PollAsync(() => jobs.DequeueAsync(queue, "w", _lease, ct), TimeSpan.FromSeconds(10));
        Assert.Equal(2, second.Attempts);
        Assert.True(await jobs.FailAsync(second, "boom 2", ct));

        var row = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id, ct);
        Assert.NotNull(row.DeadAt);
        Assert.Equal("boom 2", row.LastError);
        Assert.Null(row.LockedBy);
        Assert.Null(await jobs.DequeueAsync(queue, "w", _lease, ct));
    }

    [Fact]
    public async Task A_job_whose_lease_ran_out_on_the_last_attempt_is_marked_dead()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = PostgresFixture.UniqueName("q");
        await using var db = postgres.CreateContext();
        var jobs = PostgresFixture.CreateQueue(db);
        var id = await jobs.EnqueueAsync(queue, "{}", maxAttempts: 1, cancellationToken: ct);

        Assert.NotNull(await jobs.DequeueAsync(queue, "crashing", TimeSpan.FromSeconds(1), ct));
        await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
        Assert.Null(await jobs.DequeueAsync(queue, "w", _lease, ct));

        var row = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id, ct);
        Assert.NotNull(row.DeadAt);
        Assert.Equal("Lease expired after the last attempt.", row.LastError);
    }

    [Fact]
    public async Task A_renewed_lease_keeps_other_workers_away()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = PostgresFixture.UniqueName("q");
        await using var db = postgres.CreateContext();
        var jobs = PostgresFixture.CreateQueue(db);
        await jobs.EnqueueAsync(queue, "{}", cancellationToken: ct);

        var job = await jobs.DequeueAsync(queue, "w1", TimeSpan.FromSeconds(1), ct);
        Assert.NotNull(job);
        Assert.True(await jobs.RenewAsync(job, _lease, ct));
        await Task.Delay(TimeSpan.FromSeconds(1.5), ct);

        Assert.Null(await jobs.DequeueAsync(queue, "w2", _lease, ct));
    }

    [Fact]
    public async Task Enqueue_joins_the_callers_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = PostgresFixture.UniqueName("q");
        await using var db = postgres.CreateContext();
        var jobs = PostgresFixture.CreateQueue(db);

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await jobs.EnqueueAsync(queue, "{}", cancellationToken: ct);
            await tx.RollbackAsync(ct);
        }
        Assert.Equal(0, await CountAsync(queue));

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await jobs.EnqueueAsync(queue, "{}", cancellationToken: ct);
            await tx.CommitAsync(ct);
        }
        Assert.Equal(1, await CountAsync(queue));
    }

    private async Task EnqueueManyAsync(string queue, int count)
    {
        await using var db = postgres.CreateContext();
        var jobs = PostgresFixture.CreateQueue(db);
        await using var tx = await db.Database.BeginTransactionAsync();
        for (var i = 0; i < count; i++)
        {
            await jobs.EnqueueAsync(queue, $$"""{"n":{{i}}}""");
        }
        await tx.CommitAsync();
    }

    private async Task<int> CountAsync(string queue)
    {
        await using var db = postgres.CreateContext();
        return await db.Jobs.CountAsync(j => j.Queue == queue);
    }

    private static async Task<JobLease> PollAsync(Func<Task<JobLease?>> dequeue, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await dequeue() is { } job)
            {
                return job;
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"No job became available within {timeout}.");
    }
}
