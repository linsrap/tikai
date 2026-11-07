using Tikai.Core.Jobs;
using Tikai.Orchestrator;

namespace Tikai.Api.Runs;

/// <summary>An ended run's directory is removed by a cleanup job on the runs queue (see <see cref="RunCleanup"/>).</summary>
internal static class RunCleanups
{
    /// <summary>Joins the caller's transaction.</summary>
    public static Task EnqueueAsync(IJobQueue jobs, Guid runId, CancellationToken cancellationToken) =>
        jobs.EnqueueAsync(RunJob.Queue, new RunJob(runId, RunJobKind.Cleanup).ToJson(), maxAttempts: 3, cancellationToken: cancellationToken);
}
