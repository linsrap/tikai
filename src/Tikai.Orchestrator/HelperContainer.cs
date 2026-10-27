using Tikai.Core.Runs;

namespace Tikai.Orchestrator;

/// <summary>Runs a short-lived helper container (prep, inspect, bundle) to its end and collects its output.</summary>
internal static class HelperContainer
{
    /// <summary>Starts the container and waits for it; null when it did not end within <paramref name="limit"/>.</summary>
    public static async Task<HelperOutput?> RunToEndAsync(IContainerRuntime containers, string containerId, TimeSpan limit, CancellationToken cancellationToken)
    {
        await containers.StartAsync(containerId, cancellationToken);
        long exitCode;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(limit);
            try
            {
                exitCode = await containers.WaitAsync(containerId, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
        var lines = new List<string>();
        await foreach (var line in containers.ReadLogAsync(containerId, follow: false, cancellationToken))
        {
            lines.Add(line.Text);
        }
        return new HelperOutput(exitCode, lines, RunEventPayload.Cut(string.Join("\n", lines), 2_000));
    }
}

/// <param name="Text">The output joined and cut, for notes and error messages.</param>
internal sealed record HelperOutput(long ExitCode, IReadOnlyList<string> Lines, string Text);
