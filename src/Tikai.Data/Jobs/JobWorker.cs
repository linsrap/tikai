using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tikai.Core.Jobs;

namespace Tikai.Data.Jobs;

/// <summary>
/// Runs one loop per configured queue: take a job, run its <see cref="IJobHandler"/>, complete or fail it.
/// The lease is renewed while the handler runs; if it is lost, the handler is cancelled.
/// On shutdown a running job is left leased, so another worker retries it after the lease ends.
/// </summary>
internal sealed partial class JobWorker(
    IServiceScopeFactory scopes,
    IOptions<JobWorkerOptions> options,
    TimeProvider timeProvider,
    ILogger<JobWorker> logger) : BackgroundService
{
    private readonly JobWorkerOptions _options = options.Value;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(_options.Queues.Select(queue => RunQueueAsync(queue, stoppingToken)));

    private async Task RunQueueAsync(string queue, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunNextAsync(queue, stoppingToken))
                {
                    await Task.Delay(_options.PollInterval, timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Database unavailable or similar: back off and try again.
                LogLoopError(queue, ex);
                await Task.Delay(_options.PollInterval * 5, timeProvider, stoppingToken);
            }
        }
    }

    /// <returns>False when the queue had no due job.</returns>
    private async Task<bool> RunNextAsync(string queue, CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        var job = await jobs.DequeueAsync(queue, _options.WorkerId, _options.Lease, stoppingToken);
        if (job is null)
        {
            return false;
        }

        var handler = scope.ServiceProvider.GetRequiredKeyedService<IJobHandler>(queue);
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewal = KeepLeaseAsync(job, jobCancellation);
        try
        {
            await handler.HandleAsync(job, jobCancellation.Token);
            if (!await jobs.CompleteAsync(job, stoppingToken))
            {
                LogLeaseLost(job.Id, queue);
            }
        }
        catch (OperationCanceledException) when (jobCancellation.IsCancellationRequested)
        {
            // Shutdown or lost lease: leave the job; its lease runs out and it is retried.
        }
        catch (Exception ex)
        {
            LogJobFailed(job.Id, queue, job.Attempts, ex);
            await jobs.FailAsync(job, ex.ToString(), stoppingToken);
        }
        finally
        {
            await jobCancellation.CancelAsync();
            await renewal;
        }
        return true;
    }

    private async Task KeepLeaseAsync(JobLease job, CancellationTokenSource jobCancellation)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_options.Lease / 3, timeProvider, jobCancellation.Token);
                await using var scope = scopes.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<IJobQueue>();
                if (!await jobs.RenewAsync(job, _options.Lease, jobCancellation.Token))
                {
                    LogLeaseLost(job.Id, job.Queue);
                    await jobCancellation.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The job finished or the worker is stopping.
        }
        catch (Exception ex)
        {
            LogLoopError(job.Queue, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Job worker loop for queue {Queue} failed")]
    private partial void LogLoopError(string queue, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} on queue {Queue} failed on attempt {Attempt}")]
    private partial void LogJobFailed(Guid jobId, string queue, int attempt, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost the lease on job {JobId} (queue {Queue}); another worker may run it")]
    private partial void LogLeaseLost(Guid jobId, string queue);
}
