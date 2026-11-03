using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tikai.Core.Agents;

namespace Tikai.Orchestrator;

/// <summary>
/// Removes an ended run's containers and its directory in the runs volume (the run clone, the agent's session, the
/// bundles). The hub does not mount the runs volume, so a short-lived cleanup container does it. Queued when a run
/// ends, and by the reconciler for ended runs whose directory is still there.
/// </summary>
public sealed partial class RunCleanup(
    IRunStore store,
    IContainerRuntime containers,
    IAgentAdapter adapter,
    IOptions<OrchestratorOptions> options,
    ILogger<RunCleanup> logger)
{
    public async Task RunAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (!await store.NeedsCleanupAsync(runId, cancellationToken))
        {
            return;
        }
        foreach (var role in Enum.GetValues<SandboxRole>())
        {
            await containers.RemoveAsync(DockerContainerRuntime.Name(role, runId), CancellationToken.None);
        }
        if (!await containers.ImageExistsAsync(adapter.Image, cancellationToken))
        {
            throw new InvalidOperationException($"The agent image {adapter.Image} is missing; the run's directory is removed once it is back.");
        }
        var cleanup = await containers.CreateCleanupAsync(runId, adapter.Image, cancellationToken);
        try
        {
            var output = await HelperContainer.RunToEndAsync(containers, cleanup, options.Value.InspectTimeout, cancellationToken)
                ?? throw new InvalidOperationException($"Removing the run's directory took longer than {options.Value.InspectTimeout}.");
            if (output.ExitCode != 0)
            {
                throw new InvalidOperationException($"Removing the run's directory failed (exit code {output.ExitCode}): {output.Text}");
            }
        }
        finally
        {
            await containers.RemoveAsync(cleanup, CancellationToken.None);
        }
        await store.MarkCleanedAsync(runId, cancellationToken);
        LogCleaned(runId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: directory removed")]
    private partial void LogCleaned(Guid runId);
}
