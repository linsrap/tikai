namespace Tikai.Core.Jobs;

/// <summary>Processes the jobs of one queue. Throwing fails the job; returning completes it.</summary>
public interface IJobHandler
{
    string Queue { get; }

    Task HandleAsync(JobLease job, CancellationToken cancellationToken);
}
