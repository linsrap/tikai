using Microsoft.Extensions.Options;
using Tikai.Core.Runs;

namespace Tikai.Orchestrator;

/// <summary>What people can do with a run besides starting it.</summary>
public sealed class RunControl(IRunStore store, IContainerRuntime containers, IOptions<OrchestratorOptions> options)
{
    /// <summary>
    /// Cancels the run. An agent container (running, or blocked and waiting) is stopped and its end is recorded as
    /// cancelled by the monitor; a run without a container (queued, preparing, blocked after its agent stopped) is
    /// cancelled at once. A resume that starts meanwhile sees the cancel request and does not take over the run.
    /// </summary>
    /// <returns>False when the run does not exist or has ended.</returns>
    public async Task<bool> CancelAsync(Guid runId, CancellationToken cancellationToken)
    {
        var target = await store.RequestCancelAsync(runId, cancellationToken);
        for (var attempt = 0; attempt < 3 && target is not null && !target.State.IsEnded(); attempt++)
        {
            if (target is { State: AgentRunState.Running or AgentRunState.Blocked or AgentRunState.InReview, ContainerId: { } containerId })
            {
                await containers.StopAsync(containerId, options.Value.StopTimeout, cancellationToken);
                return true;
            }
            // A preparing run's container is not the agent's yet: the start job sees the cancel and removes it.
            if (await store.EndAsync(runId, new RunEnd(RunTrigger.Cancel, null, ContainerId: target.ContainerId), cancellationToken))
            {
                if (target.ContainerId is { } leftover)
                {
                    await containers.RemoveAsync(leftover, CancellationToken.None);
                }
                return true;
            }
            // The run moved on meanwhile (e.g. a resume attached its container): look again.
            target = await store.RequestCancelAsync(runId, cancellationToken);
        }
        return target is not null && !target.State.IsEnded();
    }
}
