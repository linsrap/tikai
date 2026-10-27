using Microsoft.Extensions.Options;
using Tikai.Core.Agents;
using Tikai.Core.Runs;

namespace Tikai.Orchestrator;

/// <summary>The run branch as a bundle file on the hub (delete it when done), and what was left out.</summary>
/// <param name="Commits">Commits since the run's start.</param>
/// <param name="Uncommitted">git status lines of files that are not part of the submission.</param>
public sealed record CollectedWork(string BundleFile, int Commits, IReadOnlyList<string> Uncommitted);

/// <summary>The work cannot be collected, for a reason the agent can fix; the message is for the agent.</summary>
public sealed class WorkCollectException(string message) : Exception(message);

/// <summary>
/// Gets a run's committed work out of its clone when the agent submits it: a bundle container packs the run branch
/// in the run directory, and the hub reads the bundle back through the archive API of that (stopped) container. The
/// hub never mounts the runs volume and never runs git in a run clone; the bundle is checked when it is imported.
/// </summary>
public sealed class RunWorkCollector(IContainerRuntime containers, IAgentAdapter adapter, IOptions<OrchestratorOptions> options)
{
    private readonly OrchestratorOptions _options = options.Value;

    /// <param name="baseSha">The run's start commit; null for runs that did not record one (the whole branch is bundled).</param>
    /// <param name="secrets">Removed from the uncommitted file list.</param>
    /// <exception cref="WorkCollectException">Nothing to submit, or the bundle cannot be taken.</exception>
    public async Task<CollectedWork> CollectAsync(Guid runId, string branch, string? baseSha, IReadOnlyList<string> secrets, CancellationToken cancellationToken)
    {
        var name = DockerContainerRuntime.Name(SandboxRole.Bundle, runId);
        // Left over from an attempt the hub did not finish.
        await containers.RemoveAsync(name, CancellationToken.None);
        var marker = RunInspect.NewMarker();
        var containerId = await containers.CreateAsync(
            new SandboxSpec(runId, SandboxRole.Bundle, RunBundle.Container(adapter.Image, branch, baseSha, marker), WithNetwork: false), cancellationToken);
        var bundleFile = Path.Combine(Path.GetTempPath(), $"tikai-review-{runId:N}-{Guid.NewGuid():N}.bundle");
        try
        {
            var output = await HelperContainer.RunToEndAsync(containers, containerId, _options.BundleTimeout, cancellationToken)
                ?? throw new WorkCollectException($"Bundling your branch took longer than {_options.BundleTimeout}.");
            var redactor = new SecretRedactor(secrets);
            var report = RunBundle.Parse([.. output.Lines.Select(redactor.Redact)], marker);
            if (output.ExitCode != 0)
            {
                throw new WorkCollectException($"Bundling your branch failed (exit code {output.ExitCode}): {redactor.Redact(output.Text)}");
            }
            if (!report.BranchExists)
            {
                throw new WorkCollectException($"Your branch {branch} is gone. Commit your work on {branch}, then submit again.");
            }
            if (report.Commits == 0 || !report.Bundled)
            {
                throw new WorkCollectException($"There are no commits on {branch} since the run started. Commit your work first, then submit again.");
            }

            await using (var tar = await containers.DownloadAsync(containerId, RunBundle.BundlePath, cancellationToken)
                ?? throw new WorkCollectException("The bundle of your branch was not found in the run directory. Submit again."))
            {
                await RunBundle.ExtractAsync(tar, bundleFile, _options.MaxBundleBytes, cancellationToken);
            }
            return new CollectedWork(bundleFile, report.Commits, report.Uncommitted);
        }
        catch
        {
            File.Delete(bundleFile);
            throw;
        }
        finally
        {
            await containers.RemoveAsync(containerId, CancellationToken.None);
        }
    }
}
