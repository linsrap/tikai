using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tikai.Core.Agents;
using Tikai.Core.Git;
using Tikai.Core.Jobs;
using Tikai.Core.Runs;

namespace Tikai.Orchestrator;

/// <summary>Job payload of the runs queue.</summary>
public sealed record RunJob(Guid RunId)
{
    public const string Queue = "runs";

    public string ToJson() => JsonSerializer.Serialize(this, RunJobJson.Options);
}

internal static class RunJobJson
{
    public static JsonSerializerOptions Options { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}

/// <summary>
/// Prepares a run and starts its agent container, then hands it to the <see cref="RunMonitor"/>: bundle the default
/// branch from the mirror, write the run directory through the archive API, clone in a prep container, write the
/// ticket, start the agent. Any failure fails the run with a reason people can act on.
/// </summary>
public sealed partial class RunStarter(
    IRunStore store,
    IContainerRuntime containers,
    IGitService git,
    IAgentAdapter adapter,
    RunMonitor monitor,
    IOptions<OrchestratorOptions> options,
    TimeProvider timeProvider,
    ILogger<RunStarter> logger) : IJobHandler
{
    private readonly OrchestratorOptions _options = options.Value;

    public string Queue => RunJob.Queue;

    public async Task HandleAsync(JobLease job, CancellationToken cancellationToken)
    {
        var runId = JsonSerializer.Deserialize<RunJob>(job.Payload, RunJobJson.Options)!.RunId;
        switch (await store.ClaimForPreparingAsync(runId, cancellationToken))
        {
            case PrepareClaim.NotQueued:
                return;
            case PrepareClaim.Interrupted:
                await RemoveContainersAsync(runId);
                await FailAsync(runId, "The hub stopped while preparing this run. Start a new run.", cancellationToken);
                return;
        }

        try
        {
            await StartAsync(runId, cancellationToken);
        }
        catch (Exception ex) when (ex is RunStartException or GitException)
        {
            await RemoveContainersAsync(runId);
            await FailAsync(runId, ex.Message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStartFailed(runId, ex);
            await RemoveContainersAsync(runId);
            await FailAsync(runId, $"Starting the agent failed: {ex.Message}", cancellationToken);
        }
    }

    private async Task StartAsync(Guid runId, CancellationToken cancellationToken)
    {
        var info = await store.GetStartInfoAsync(runId, cancellationToken);
        if (!await containers.ImageExistsAsync(adapter.Image, cancellationToken))
        {
            throw new RunStartException($"The agent image {adapter.Image} is not on this Docker host. Build it with \"docker compose --profile agents build\" (see docs/self-hosting.md).");
        }
        var token = await store.RenewTokenAsync(runId, cancellationToken)
            ?? throw new RunStartException("The run ended while it was being prepared.");
        var context = new AgentRunContext(runId, info.TicketKey, info.TicketTitle, info.Branch, info.Llm, _options.McpUrl, token, _options.ToolCallTimeout);

        // 1. The default branch, and nothing else of the repository, as a bundle (git runs only in the mirror).
        var bundle = Path.Combine(Path.GetTempPath(), $"tikai-run-{runId:N}.bundle");
        try
        {
            await git.CreateBundleAsync(info.MirrorPath, info.DefaultBranch, bundle, cancellationToken);

            // 2. The run directory: root-owned run root and hub files, agent-owned work/, home/ and the adapter's directories.
            var seed = await containers.CreateSeedAsync(runId, adapter.Image, cancellationToken);
            try
            {
                await using var skeleton = await RunArchive.BuildAsync(
                    runId.ToString("D"), RunPrep.Skeleton(info.TicketKey, bundle, adapter.RunFiles(context)), _options.AgentUid, timeProvider.GetUtcNow(), cancellationToken);
                await containers.UploadAsync(seed, "/runs", skeleton, cancellationToken);
            }
            finally
            {
                await containers.RemoveAsync(seed, CancellationToken.None);
            }
        }
        finally
        {
            File.Delete(bundle);
        }
        await NoteAsync(runId, "Run directory written", new JsonObject { ["branch"] = info.Branch }, cancellationToken);

        // 3. Clone, branch, identity and hook, as the agent uid without network; then the read-only ticket.
        await PrepareCloneAsync(runId, info, cancellationToken);

        // 4. The agent.
        var spec = adapter.BuildContainer(context);
        var containerId = await containers.CreateAsync(new SandboxSpec(runId, SandboxRole.Agent, spec, WithNetwork: true), cancellationToken);
        await store.SetContainerAsync(runId, containerId, cancellationToken);
        await containers.StartAsync(containerId, cancellationToken);
        if (!await store.MarkRunningAsync(runId, cancellationToken))
        {
            // Cancelled while it was being prepared.
            await containers.RemoveAsync(containerId, CancellationToken.None);
            await store.SetContainerAsync(runId, null, CancellationToken.None);
            return;
        }
        await NoteAsync(runId, "Agent started", new JsonObject
        {
            ["image"] = spec.Image,
            ["container_id"] = containerId,
            ["model"] = info.Llm.Model,
        }, cancellationToken);
        LogStarted(runId, containerId);
        monitor.Watch(new WatchedRun(runId, containerId, 0, 0, [info.Llm.ApiKey, token]));
    }

    private async Task PrepareCloneAsync(Guid runId, RunStartInfo info, CancellationToken cancellationToken)
    {
        var prep = await containers.CreateAsync(
            new SandboxSpec(runId, SandboxRole.Prep, RunPrep.Container(adapter.Image, info.DefaultBranch, info.Branch), WithNetwork: false),
            cancellationToken);
        try
        {
            await containers.StartAsync(prep, cancellationToken);
            long exitCode;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(_options.PrepareTimeout);
                try
                {
                    exitCode = await containers.WaitAsync(prep, timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new RunStartException($"Preparing the run clone took longer than {_options.PrepareTimeout}.");
                }
            }
            var output = new List<string>();
            await foreach (var line in containers.ReadLogAsync(prep, follow: false, cancellationToken))
            {
                output.Add(line.Text);
            }
            var text = RunEventPayload.Cut(string.Join("\n", output), 2_000);
            if (exitCode != 0)
            {
                throw new RunStartException($"Preparing the run clone failed (exit code {exitCode}): {text}");
            }
            await NoteAsync(runId, "Run clone prepared", new JsonObject { ["output"] = text }, cancellationToken);

            await using var ticket = await RunArchive.BuildAsync(null, RunPrep.TicketFiles(info.TicketDocument), _options.AgentUid, timeProvider.GetUtcNow(), cancellationToken);
            await containers.UploadAsync(prep, RunLayout.Work, ticket, cancellationToken);
        }
        finally
        {
            await containers.RemoveAsync(prep, CancellationToken.None);
        }
    }

    private Task NoteAsync(Guid runId, string text, JsonObject details, CancellationToken cancellationToken)
    {
        details["source"] = "hub";
        details["text"] = text;
        return store.AppendEventsAsync(runId, [new ParsedEvent(RunEventKind.Log, details)], null, cancellationToken);
    }

    private async Task FailAsync(Guid runId, string reason, CancellationToken cancellationToken)
    {
        await NoteAsync(runId, "Run failed", new JsonObject { ["reason"] = reason }, cancellationToken);
        await store.EndAsync(runId, new RunEnd(RunTrigger.Fail, reason), cancellationToken);
    }

    private async Task RemoveContainersAsync(Guid runId)
    {
        foreach (var role in Enum.GetValues<SandboxRole>())
        {
            await containers.RemoveAsync(DockerContainerRuntime.Name(role, runId), CancellationToken.None);
        }
        await store.SetContainerAsync(runId, null, CancellationToken.None);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: agent container {ContainerId} started")]
    private partial void LogStarted(Guid runId, string containerId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId}: starting the agent failed")]
    private partial void LogStartFailed(Guid runId, Exception exception);
}
