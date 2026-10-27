using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tikai.Core.Agents;
using Tikai.Core.Git;
using Tikai.Core.Jobs;
using Tikai.Core.Runs;

namespace Tikai.Orchestrator;

/// <summary>Job payload of the runs queue.</summary>
public sealed record RunJob(Guid RunId, RunJobKind Kind = RunJobKind.Start)
{
    public const string Queue = "runs";

    public string ToJson() => JsonSerializer.Serialize(this, RunJobJson.Options);

    public static RunJob FromJson(string json) => JsonSerializer.Deserialize<RunJob>(json, RunJobJson.Options)!;
}

public enum RunJobKind
{
    /// <summary>Prepare the run clone and start the first container.</summary>
    Start,

    /// <summary>A new container continues the agent's session with the answers (or a fresh one when it is gone).</summary>
    Resume,

    /// <summary>A fresh session with a summary, after a resumed session failed.</summary>
    Summary,
}

internal static class RunJobJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}

/// <summary>
/// Prepares a run and starts its agent container, then hands it to the <see cref="RunMonitor"/>: bundle the default
/// branch from the mirror, write the run directory through the archive API, clone in a prep container, write the
/// ticket, start the agent. A resume skips the clone: a new token in a new MCP config, the clone's state from an
/// inspect container, then the agent's own session (--resume) or a fresh one with a summary. Any failure fails the run
/// with a reason people can act on.
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
        var payload = RunJob.FromJson(job.Payload);
        var runId = payload.RunId;
        if (payload.Kind != RunJobKind.Start)
        {
            await GuardAsync(runId, () => ResumeAsync(runId, payload.Kind, cancellationToken), cancellationToken);
            return;
        }
        switch (await store.ClaimForPreparingAsync(runId, cancellationToken))
        {
            case PrepareClaim.NotQueued:
                return;
            case PrepareClaim.Interrupted:
                await RemoveContainersAsync(runId);
                await FailAsync(runId, "The hub stopped while preparing this run. Start a new run.", cancellationToken);
                return;
        }
        await GuardAsync(runId, () => StartAsync(runId, cancellationToken), cancellationToken);
    }

    private async Task GuardAsync(Guid runId, Func<Task> start, CancellationToken cancellationToken)
    {
        try
        {
            await start();
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
        await CheckImageAsync(cancellationToken);
        var token = await store.RenewTokenAsync(runId, cancellationToken)
            ?? throw new RunStartException("The run ended while it was being prepared.");
        var context = Context(info, token);

        // 1. The default branch, and nothing else of the repository, as a bundle (git runs only in the mirror).
        var bundle = Path.Combine(Path.GetTempPath(), $"tikai-run-{runId:N}.bundle");
        try
        {
            var baseSha = await git.CreateBundleAsync(info.MirrorPath, info.DefaultBranch, bundle, cancellationToken);
            // Where the run starts: the submitted work is bundled from here.
            await store.SetBaseShaAsync(runId, baseSha, cancellationToken);

            // 2. The run directory: root-owned run root and hub files, agent-owned work/, home/ and the adapter's directories.
            await UploadIntoRunsAsync(runId, RunPrep.Skeleton(info.TicketKey, bundle, adapter.RunFiles(context)), cancellationToken);
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
        await store.AttachContainerAsync(runId, containerId, RunAttemptKind.Start, cancellationToken);
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

    private async Task ResumeAsync(Guid runId, RunJobKind kind, CancellationToken cancellationToken)
    {
        var resume = await store.GetResumeInfoAsync(runId, cancellationToken);
        if (resume is null || !resume.CanResume)
        {
            if (resume is { CancelRequested: true, State: AgentRunState.Blocked or AgentRunState.Running })
            {
                await store.EndAsync(runId, new RunEnd(RunTrigger.Cancel, null), cancellationToken);
            }
            return;
        }
        // Leftovers of an interrupted earlier attempt: the run has no container, so none of them is its agent.
        await RemoveContainersAsync(runId);

        var info = await store.GetStartInfoAsync(runId, cancellationToken);
        await CheckImageAsync(cancellationToken);
        var token = await store.RenewTokenAsync(runId, cancellationToken)
            ?? throw new RunStartException("The run ended while it was being resumed.");
        var context = Context(info, token);

        // 1. The new token in a new MCP config (root-owned, so the agent cannot change it); the old token stops working.
        await UploadIntoRunsAsync(runId,
            adapter.RunFiles(context).Where(f => f is { AgentOwned: false, Content: not null }).Select(f => (ArchiveEntry)f),
            cancellationToken);

        // 2. What is in the run clone now, and whether the session can be resumed.
        var session = resume.SessionId is { } id && SessionIdShape().IsMatch(id) ? id : null;
        var clone = await InspectAsync(runId, info, session is null ? null : adapter.SessionFile(session), cancellationToken);
        await NoteAsync(runId, "Run clone before resuming", new JsonObject
        {
            ["git_log"] = clone.GitLog,
            ["git_status"] = clone.GitStatus,
            ["session_found"] = clone.SessionExists,
        }, cancellationToken);

        // 3. The agent's own session when it is there, otherwise a fresh one that gets a summary.
        RunContinuation continuation = kind == RunJobKind.Resume && session is not null && clone.SessionExists
            ? new ResumeSession(session, [.. resume.ToDeliver.Select(a => a.Answer)])
            : new FreshSession(resume.ApprovedPlan, resume.Answered, clone.GitLog, clone.GitStatus);
        var attempt = continuation is ResumeSession ? RunAttemptKind.Resume : RunAttemptKind.Summary;
        var spec = adapter.BuildResumeContainer(context, continuation);
        var containerId = await containers.CreateAsync(new SandboxSpec(runId, SandboxRole.Agent, spec, WithNetwork: true), cancellationToken);
        await store.AttachContainerAsync(runId, containerId, attempt, cancellationToken);
        await containers.StartAsync(containerId, cancellationToken);
        if (!await store.MarkResumedAsync(runId, containerId, [.. resume.ToDeliver.Select(a => a.Id)], cancellationToken))
        {
            // Cancelled while it was being resumed.
            await containers.RemoveAsync(containerId, CancellationToken.None);
            await store.EndAsync(runId, new RunEnd(RunTrigger.Cancel, null, ContainerId: containerId), CancellationToken.None);
            return;
        }
        await NoteAsync(runId, attempt == RunAttemptKind.Resume ? "Agent resumed its session" : "Agent started a fresh session with a summary", new JsonObject
        {
            ["image"] = spec.Image,
            ["container_id"] = containerId,
            ["model"] = info.Llm.Model,
            ["answers"] = resume.ToDeliver.Count,
            ["reason"] = attempt == RunAttemptKind.Resume ? null
                : kind == RunJobKind.Summary ? "the resumed session failed"
                : session is null ? "no session was recorded"
                : "the session file is gone",
        }, cancellationToken);
        LogResumed(runId, containerId, attempt);
        monitor.Watch(new WatchedRun(runId, containerId, 0, 0, [info.Llm.ApiKey, token]));
    }

    private AgentRunContext Context(RunStartInfo info, string token) =>
        new(info.RunId, info.TicketKey, info.TicketTitle, info.Branch, info.Llm, _options.McpUrl, token, _options.ToolCallTimeout);

    private async Task CheckImageAsync(CancellationToken cancellationToken)
    {
        if (!await containers.ImageExistsAsync(adapter.Image, cancellationToken))
        {
            throw new RunStartException($"The agent image {adapter.Image} is not on this Docker host. Build it with \"docker compose --profile agents build\" (see docs/self-hosting.md).");
        }
    }

    /// <summary>Writes entries below the run's directory through a never-started container that has the runs volume.</summary>
    private async Task UploadIntoRunsAsync(Guid runId, IEnumerable<ArchiveEntry> entries, CancellationToken cancellationToken)
    {
        var seed = await containers.CreateSeedAsync(runId, adapter.Image, cancellationToken);
        try
        {
            await using var tar = await RunArchive.BuildAsync(runId.ToString("D"), entries, _options.AgentUid, timeProvider.GetUtcNow(), cancellationToken);
            await containers.UploadAsync(seed, "/runs", tar, cancellationToken);
        }
        finally
        {
            await containers.RemoveAsync(seed, CancellationToken.None);
        }
    }

    private async Task PrepareCloneAsync(Guid runId, RunStartInfo info, CancellationToken cancellationToken)
    {
        var prep = await containers.CreateAsync(
            new SandboxSpec(runId, SandboxRole.Prep, RunPrep.Container(adapter.Image, info.DefaultBranch, info.Branch), WithNetwork: false),
            cancellationToken);
        try
        {
            var output = await RunToEndAsync(prep, _options.PrepareTimeout, "Preparing the run clone", cancellationToken);
            await NoteAsync(runId, "Run clone prepared", new JsonObject { ["output"] = output.Text }, cancellationToken);

            await using var ticket = await RunArchive.BuildAsync(null, RunPrep.TicketFiles(info.TicketDocument), _options.AgentUid, timeProvider.GetUtcNow(), cancellationToken);
            await containers.UploadAsync(prep, RunLayout.Work, ticket, cancellationToken);
        }
        finally
        {
            await containers.RemoveAsync(prep, CancellationToken.None);
        }
    }

    private async Task<RunCloneState> InspectAsync(Guid runId, RunStartInfo info, string? sessionFile, CancellationToken cancellationToken)
    {
        var marker = RunInspect.NewMarker();
        var inspect = await containers.CreateAsync(
            new SandboxSpec(runId, SandboxRole.Inspect, RunInspect.Container(adapter.Image, sessionFile, info.DefaultBranch, marker), WithNetwork: false),
            cancellationToken);
        try
        {
            var output = await RunToEndAsync(inspect, _options.InspectTimeout, "Reading the run clone", cancellationToken);
            var redactor = new SecretRedactor([info.Llm.ApiKey]);
            return RunInspect.Parse([.. output.Lines.Select(redactor.Redact)], marker);
        }
        finally
        {
            await containers.RemoveAsync(inspect, CancellationToken.None);
        }
    }

    /// <summary>Starts a helper container, waits for it, and returns its output; a non-zero exit is an error for people.</summary>
    private async Task<HelperOutput> RunToEndAsync(string containerId, TimeSpan limit, string what, CancellationToken cancellationToken)
    {
        var output = await HelperContainer.RunToEndAsync(containers, containerId, limit, cancellationToken)
            ?? throw new RunStartException($"{what} took longer than {limit}.");
        if (output.ExitCode != 0)
        {
            throw new RunStartException($"{what} failed (exit code {output.ExitCode}): {output.Text}");
        }
        return output;
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

    /// <summary>A session id goes into a path and a command line: only the shape Claude Code uses (a UUID) is accepted.</summary>
    [GeneratedRegex("^[A-Za-z0-9-]{1,100}$")]
    private static partial Regex SessionIdShape();

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: agent container {ContainerId} started")]
    private partial void LogStarted(Guid runId, string containerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: agent container {ContainerId} continues the run ({Attempt})")]
    private partial void LogResumed(Guid runId, string containerId, RunAttemptKind attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId}: starting the agent failed")]
    private partial void LogStartFailed(Guid runId, Exception exception);
}
