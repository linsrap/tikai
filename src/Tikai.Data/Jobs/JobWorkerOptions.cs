namespace Tikai.Data.Jobs;

public sealed class JobWorkerOptions
{
    /// <summary>Queues this process works on; filled by <c>AddJobHandler</c>.</summary>
    public ISet<string> Queues { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>How long a taken job stays reserved; renewed every third of it while the handler runs.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Wait between dequeue attempts when a queue is empty.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Identifies this process in the jobs table (locked_by).</summary>
    public string WorkerId { get; set; } = $"{Environment.MachineName}:{Environment.ProcessId}";
}
