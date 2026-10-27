using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Tikai.Api.Attention;
using Tikai.Api.Hosting;
using Tikai.Api.Llm;
using Tikai.Api.Projects;
using Tikai.Api.Runs;
using Tikai.Core.Attention;
using Tikai.Core.Events;
using Tikai.Core.Git;
using Tikai.Core.Reviews;
using Tikai.Core.Runs;
using Tikai.Core.Tickets;
using Tikai.Data;
using Tikai.Mcp;
using Tikai.Orchestrator;

namespace Tikai.Api.Reviews;

/// <summary>One submission of a run at a time (keyed by run id).</summary>
internal sealed class SubmitLocks : KeyedLocks;

/// <summary>
/// The git part of submit_for_review, inside the agent's tool call: a bundle container packs the run branch, the hub
/// imports it into the mirror (prerequisites and every object checked), pins the round's head, and records the change
/// set and round while the run moves to in_review. Problems the agent can fix come back as its tool error.
/// </summary>
internal sealed partial class ReviewSubmissions(
    TikaiDbContext db,
    RunWorkCollector collector,
    IGitService git,
    MirrorPaths mirrors,
    MirrorLocks mirrorLocks,
    SubmitLocks submitLocks,
    AttentionService attention,
    LlmProviderService providers,
    IEventPublisher events,
    TimeProvider timeProvider,
    ILogger<ReviewSubmissions> logger)
{
    /// <summary>Uncommitted files named in the tool result; the round keeps all of them (up to the bundle's limit).</summary>
    private const int UncommittedInMessage = 10;

    private const int PreparingWaitSeconds = 30;

    public async Task<SubmitResult> SubmitAsync(RunScope scope, ReviewPayload report, CancellationToken cancellationToken)
    {
        // A second call of the same run (a retry after a lost response) waits and then finds the run in review.
        using var submitting = await submitLocks.AcquireAsync(scope.RunId, cancellationToken);
        var run = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == scope.RunId, cancellationToken);
        // The start job starts the container and then marks the run running: a quick agent can call in between.
        for (var wait = 0; run.State == AgentRunState.Preparing && wait < PreparingWaitSeconds; wait++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);
            run = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == scope.RunId, cancellationToken);
        }
        if (run.State == AgentRunState.InReview)
        {
            var latest = await LatestRoundAsync(run.Id, cancellationToken);
            return new SubmitResult(true, latest is null
                ? "Your work is already in review."
                : $"Your work is already in review (round {latest.RoundNo}, {Short(latest.HeadSha)}). Commits after that are not part of the review; stop here.");
        }
        if (run.State != AgentRunState.Running)
        {
            return Refused($"Your run is {System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(run.State.ToString())}; only a running agent can submit.");
        }
        var repository = run.RepositoryId is { } repositoryId
            ? await db.Repositories.AsNoTracking().SingleOrDefaultAsync(r => r.Id == repositoryId, cancellationToken)
            : null;
        if (repository is null || run.Branch is not { } branch)
        {
            return Refused("This run has no repository to submit to.");
        }
        var mirror = mirrors.For(repository.ProjectId, repository.Id);

        CollectedWork work;
        try
        {
            work = await collector.CollectAsync(run.Id, branch, run.BaseSha, await SecretsAsync(run, cancellationToken), cancellationToken);
        }
        catch (WorkCollectException ex)
        {
            return Refused(ex.Message);
        }

        try
        {
            var changeSet = await db.ChangeSets.AsNoTracking().SingleOrDefaultAsync(c => c.RunId == run.Id, cancellationToken);
            var changeSetId = changeSet?.Id ?? Guid.CreateVersion7(timeProvider.GetUtcNow());
            var roundNo = changeSet is null
                ? 1
                : await db.ReviewRounds.Where(r => r.ChangeSetId == changeSetId).MaxAsync(r => (int?)r.RoundNo, cancellationToken) + 1 ?? 1;

            string head;
            string? baseSha;
            using (await mirrorLocks.AcquireAsync(repository.Id, cancellationToken))
            {
                try
                {
                    // The run branch is the hub's own (the agent may have rewritten it); the round ref keeps this head.
                    head = await git.ImportBundleAsync(mirror, work.BundleFile, branch,
                        ["refs/heads/" + branch, ReviewRules.RoundRef(changeSetId, roundNo)], cancellationToken);
                }
                catch (GitException ex)
                {
                    LogImportFailed(run.Id, ex.Message);
                    return Refused("The hub could not take your branch: " + ex.Message.Replace(mirror, "<mirror>", StringComparison.Ordinal)
                        .Replace(Path.GetFullPath(work.BundleFile), "<bundle>", StringComparison.Ordinal));
                }
                // The fork point: commits the default branch got since the run started are not part of the change.
                baseSha = await git.MergeBaseAsync(mirror, "refs/heads/" + repository.DefaultBranch, head, cancellationToken);
            }
            if (baseSha is null)
            {
                return Refused($"Your branch {branch} shares no history with {repository.DefaultBranch}. Build on the clone you were given.");
            }

            if (!await RecordAsync(scope, run, changeSet, changeSetId, roundNo, baseSha, head, report, work.Uncommitted, cancellationToken))
            {
                return Refused("Your run was stopped while it submitted.");
            }
            LogSubmitted(run.Id, roundNo, head);
            var message = $"Submitted for review as round {roundNo}: {work.Commits} commit{(work.Commits == 1 ? "" : "s")}, head {Short(head)}.";
            if (work.Uncommitted.Count > 0)
            {
                message += $" {work.Uncommitted.Count} uncommitted file{(work.Uncommitted.Count == 1 ? " was" : "s were")} not submitted: "
                    + string.Join(", ", work.Uncommitted.Take(UncommittedInMessage))
                    + (work.Uncommitted.Count > UncommittedInMessage ? ", …" : "") + ". The reviewer sees this list.";
            }
            return new SubmitResult(true, message);
        }
        finally
        {
            File.Delete(work.BundleFile);
        }
    }

    /// <summary>Change set, round, run and ticket state and the review item, in one transaction with the run row locked.</summary>
    private async Task<bool> RecordAsync(
        RunScope scope,
        AgentRun run,
        ChangeSet? changeSet,
        Guid changeSetId,
        int roundNo,
        string baseSha,
        string head,
        ReviewPayload report,
        IReadOnlyList<string> uncommitted,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var locked = await RunResumes.LockAsync(db, run.Id, cancellationToken);
        if (locked is not { State: AgentRunState.Running, CancelRequestedAt: null })
        {
            return false;
        }
        if (changeSet is null)
        {
            db.ChangeSets.Add(new ChangeSet
            {
                Id = changeSetId,
                TicketId = run.TicketId,
                RunId = run.Id,
                RepositoryId = run.RepositoryId!.Value,
                BaseSha = baseSha,
                HeadSha = head,
                Status = ChangeSetStatus.Open,
            });
        }
        else
        {
            await db.ChangeSets.Where(c => c.Id == changeSetId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.BaseSha, baseSha)
                    .SetProperty(c => c.HeadSha, head)
                    .SetProperty(c => c.Status, ChangeSetStatus.Open)
                    .SetProperty(c => c.UpdatedAt, now), cancellationToken);
        }
        db.ReviewRounds.Add(new ReviewRound
        {
            Id = Guid.CreateVersion7(now),
            ChangeSetId = changeSetId,
            RoundNo = roundNo,
            BaseSha = baseSha,
            HeadSha = head,
            SelfReport = AttentionJson.Serialize(report),
            Uncommitted = [.. uncommitted],
        });
        await db.SaveChangesAsync(cancellationToken);

        // The container stays: it ends on its own a moment later, and that end keeps the run in review.
        await db.AgentRuns.Where(r => r.Id == run.Id && r.State == AgentRunState.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, AgentRunState.InReview).SetProperty(r => r.UpdatedAt, now), cancellationToken);
        await db.Tickets.Where(t => t.Id == run.TicketId && t.Status == TicketStatus.InProgress)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TicketStatus.InReview).SetProperty(t => t.UpdatedAt, now), cancellationToken);
        await attention.RecordAsync(scope, AttentionKind.Review, report, cancellationToken);
        await events.PublishAsync(ChangeSet.Topic, changeSetId, cancellationToken);
        await events.PublishAsync(AgentRun.Topic, run.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private Task<ReviewRound?> LatestRoundAsync(Guid runId, CancellationToken cancellationToken) =>
        (from c in db.ChangeSets
         join r in db.ReviewRounds on c.Id equals r.ChangeSetId
         where c.RunId == runId
         orderby r.RoundNo descending
         select r).AsNoTracking().FirstOrDefaultAsync(cancellationToken);

    private async Task<IReadOnlyList<string>> SecretsAsync(AgentRun run, CancellationToken cancellationToken)
    {
        var provider = run.LlmProviderId is { } id ? await db.LlmProviders.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken) : null;
        try
        {
            return provider is null ? [] : [providers.Connection(provider).ApiKey];
        }
        catch (CryptographicException)
        {
            return [];
        }
    }

    private static SubmitResult Refused(string message) => new(false, message);

    private static string Short(string sha) => sha[..Math.Min(8, sha.Length)];

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: round {RoundNo} submitted at {Head}")]
    private partial void LogSubmitted(Guid runId, int roundNo, string head);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: importing the submitted bundle failed: {Error}")]
    private partial void LogImportFailed(Guid runId, string error);
}
