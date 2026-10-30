using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tikai.Api.Llm;
using Tikai.Api.Projects;
using Tikai.Core.Attention;
using Tikai.Core.Events;
using Tikai.Core.Git;
using Tikai.Core.Jobs;
using Tikai.Core.Projects;
using Tikai.Core.Reviews;
using Tikai.Core.Tickets;
using Tikai.Data;
using Tikai.Llm.Reviews;
using Tikai.Review.Analysis;

namespace Tikai.Api.Reviews;

/// <summary>A job on the reviews queue: analyse one review round.</summary>
internal static class ReviewAnalysisJob
{
    public const string Queue = "reviews";

    /// <summary>Joins the caller's transaction; the round's status must be pending already.</summary>
    public static Task EnqueueAsync(IJobQueue jobs, Guid roundId, CancellationToken cancellationToken) =>
        jobs.EnqueueAsync(Queue, JsonSerializer.Serialize(new Payload(roundId), JsonSerializerOptions.Web), maxAttempts: 3, cancellationToken: cancellationToken);

    public sealed record Payload(Guid RoundId);
}

/// <summary>
/// Analyses a review round with the run's own provider: picks the tier from the diff, the model from the project's
/// settings, reads the patches within the tier's budget, asks the model and stores the cleaned answer on the round.
/// Transient provider errors are retried by the queue; everything else ends the round's analysis as failed, with a
/// message for people. A failed analysis never blocks the review.
/// </summary>
internal sealed partial class ReviewAnalysisJobHandler(
    TikaiDbContext db,
    IGitService git,
    MirrorPaths mirrors,
    LlmProviderService providers,
    ReviewAnalyzer analyzer,
    IEventPublisher events,
    TimeProvider timeProvider,
    ILogger<ReviewAnalysisJobHandler> logger) : IJobHandler
{
    public string Queue => ReviewAnalysisJob.Queue;

    public async Task HandleAsync(JobLease job, CancellationToken cancellationToken)
    {
        var roundId = JsonSerializer.Deserialize<ReviewAnalysisJob.Payload>(job.Payload, JsonSerializerOptions.Web)!.RoundId;
        try
        {
            await AnalyseAsync(job, roundId, cancellationToken);
        }
        catch (Exception ex) when (job.Attempts >= job.MaxAttempts && !cancellationToken.IsCancellationRequested)
        {
            // The last attempt: the round must not stay "pending" for good.
            await FailAsync(roundId, "The analysis failed unexpectedly: " + ex.Message, cancellationToken);
            throw;
        }
    }

    private async Task AnalyseAsync(JobLease job, Guid roundId, CancellationToken cancellationToken)
    {
        var row = await (
            from r in db.ReviewRounds
            join c in db.ChangeSets on r.ChangeSetId equals c.Id
            join run in db.AgentRuns on c.RunId equals run.Id
            join t in db.Tickets on c.TicketId equals t.Id
            join p in db.Projects on t.ProjectId equals p.Id
            join repo in db.Repositories on c.RepositoryId equals repo.Id
            where r.Id == roundId
            select new { Round = r, ChangeSet = c, run.LlmProviderId, Ticket = t, Project = p, repo.ProjectId, RepositoryId = repo.Id })
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (row is null || row.Round.AnalysisStatus != AnalysisStatus.Pending)
        {
            return; // gone, or done meanwhile
        }
        if (row.ChangeSet.Status != ChangeSetStatus.Open)
        {
            await FailAsync(roundId, "Not analysed: the change was closed first.", cancellationToken);
            return;
        }
        var provider = row.LlmProviderId is { } providerId
            ? await db.LlmProviders.AsNoTracking().SingleOrDefaultAsync(p => p.Id == providerId, cancellationToken)
            : null;
        if (provider is null)
        {
            await FailAsync(roundId, "The run's LLM provider was removed; add the key again and retry.", cancellationToken);
            return;
        }

        var mirror = mirrors.For(row.ProjectId, row.RepositoryId);
        var round = row.Round;
        GitDiff diff;
        IReadOnlyList<AnalysisFile> files;
        try
        {
            diff = await git.DiffAsync(mirror, round.BaseSha, round.HeadSha, ChangeSetEndpoints.MaxFiles, cancellationToken);
            files = await ChangeSetEndpoints.ClassifyAsync(git, mirror, round.HeadSha, diff, cancellationToken);
        }
        catch (GitException ex)
        {
            await FailAsync(roundId, "The diff could not be read: " + ex.Message.Replace(mirror, "<mirror>", StringComparison.Ordinal), cancellationToken);
            return;
        }
        var tier = AnalysisTiers.Choose(files);
        var model = ProjectSettings.Parse(row.Project.Settings).AnalysisModel(tier.Tier);

        // Patches in path order until the tier's budget is used; a file that does not fit is only listed.
        var patches = new Dictionary<string, string>(StringComparer.Ordinal);
        long budget = AnalysisInput.PatchBudget(tier.Tier);
        foreach (var file in AnalysisInput.PatchCandidates(files))
        {
            if (budget < 200)
            {
                break;
            }
            if (await git.PatchAsync(mirror, round.BaseSha, round.HeadSha, file.Path, file.OldPath, budget, cancellationToken) is { Length: > 0 } patch)
            {
                patches[file.Path] = patch;
                budget -= patch.Length;
            }
        }

        var criteria = await db.AcceptanceCriteria.AsNoTracking().Where(c => c.TicketId == row.Ticket.Id).OrderBy(c => c.Position)
            .Select(c => new CriterionText(c.Id, c.Text)).ToListAsync(cancellationToken);
        var context = new AnalysisContext(
            TicketKey.Format(row.Project.Key, row.Ticket.Number),
            row.Ticket.Title,
            row.Ticket.Description,
            criteria,
            AttentionJson.Deserialize<ReviewPayload>(round.SelfReport),
            files);
        var (message, omitted) = AnalysisInput.Build(context, patches);

        Core.Llm.LlmConnection connection;
        try
        {
            connection = providers.Connection(provider) with { Model = model };
        }
        catch (CryptographicException)
        {
            await FailAsync(roundId, $"The key of \"{provider.Name}\" cannot be decrypted (the hub's keys changed). Enter the key again.", cancellationToken);
            return;
        }
        var result = await analyzer.AnalyzeAsync(connection, message, tier.Tier, cancellationToken);
        if (result.Answer is not { } answer)
        {
            if (result.Transient && job.Attempts < job.MaxAttempts)
            {
                throw new AnalysisRetryException(result.Error ?? "The provider failed.");
            }
            await FailAsync(roundId, $"{result.Error} (model {model})", cancellationToken);
            return;
        }
        if (AnalysisCleaner.Clean(answer, files, [.. criteria.Select(c => c.Id)]) is not { } clean)
        {
            await FailAsync(roundId, $"The answer of {model} had nothing usable in it.", cancellationToken);
            return;
        }

        var analysis = new ReviewAnalysis(
            tier.Tier,
            tier.Reason,
            model,
            ReviewAnalyzer.PromptName,
            clean.Summary,
            clean.Coverage,
            clean.Groups,
            clean.Risks,
            omitted,
            result.InputTokens,
            result.OutputTokens,
            (int)result.Duration.TotalMilliseconds);
        var now = timeProvider.GetUtcNow();
        var stored = await db.ReviewRounds.Where(r => r.Id == roundId && r.AnalysisStatus == AnalysisStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.AnalysisStatus, AnalysisStatus.Done)
                .SetProperty(r => r.Analysis, AttentionJson.Serialize(analysis))
                .SetProperty(r => r.AnalysisError, (string?)null)
                .SetProperty(r => r.AnalysisAt, now)
                .SetProperty(r => r.UpdatedAt, now), cancellationToken);
        if (stored > 0)
        {
            LogAnalysed(roundId, tier.Tier, model, result.InputTokens, result.OutputTokens);
            await events.PublishAsync(ChangeSet.Topic, row.ChangeSet.Id, cancellationToken);
        }
    }

    private async Task FailAsync(Guid roundId, string error, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var changeSetId = await db.ReviewRounds.Where(r => r.Id == roundId).Select(r => r.ChangeSetId).SingleOrDefaultAsync(cancellationToken);
        var failed = await db.ReviewRounds.Where(r => r.Id == roundId && r.AnalysisStatus == AnalysisStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.AnalysisStatus, AnalysisStatus.Failed)
                .SetProperty(r => r.AnalysisError, error)
                .SetProperty(r => r.AnalysisAt, now)
                .SetProperty(r => r.UpdatedAt, now), cancellationToken);
        if (failed > 0)
        {
            LogAnalysisFailed(roundId, error);
            await events.PublishAsync(ChangeSet.Topic, changeSetId, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Review round {RoundId} analysed ({Tier}, {Model}, {InputTokens} input, {OutputTokens} output tokens)")]
    private partial void LogAnalysed(Guid roundId, AnalysisTier tier, string model, long? inputTokens, long? outputTokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review round {RoundId}: analysis failed: {Error}")]
    private partial void LogAnalysisFailed(Guid roundId, string error);
}

/// <summary>A transient provider error: the queue tries the job again after a backoff.</summary>
internal sealed class AnalysisRetryException(string message) : Exception(message);
