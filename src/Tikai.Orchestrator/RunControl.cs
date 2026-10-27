using Microsoft.Extensions.Options;
using Tikai.Core.Runs;

namespace Tikai.Orchestrator;

/// <summary>What people can do with a run besides starting it.</summary>
public sealed class RunControl(IRunStore store, IContainerRuntime containers, IOptions<OrchestratorOptions> options)
{
    /// <summary>
    /// Cancels the run. A running agent is stopped and its end is recorded as cancelled by the monitor; a run without a
    /// container (queued, preparing, blocked after its agent exited) is cancelled at once.
    /// </summary>
    /// <returns>False when the run does not exist or has ended.</returns>
    public async Task<bool> CancelAsync(Guid runId, CancellationToken cancellationToken)
    {
        var target = await store.RequestCancelAsync(runId, cancellationToken);
        if (target is null || target.State.IsEnded())
        {
            return false;
        }
        if (target is { State: AgentRunState.Running, ContainerId: { } containerId })
        {
            await containers.StopAsync(containerId, options.Value.StopTimeout, cancellationToken);
            return true;
        }
        await store.EndAsync(runId, new RunEnd(RunTrigger.Cancel, null), cancellationToken);
        if (target.ContainerId is { } leftover)
        {
            await containers.RemoveAsync(leftover, CancellationToken.None);
        }
        return true;
    }
}
