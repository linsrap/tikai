using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tikai.Api.Attention;
using Tikai.Api.Previews;
using Tikai.Api.Projects;
using Tikai.Api.Runs;
using Tikai.Core.Agents;
using Tikai.Core.Attention;
using Tikai.Core.Events;
using Tikai.Core.Git;
using Tikai.Core.Jobs;
using Tikai.Core.Projects;
using Tikai.Core.Reviews;
using Tikai.Core.Runs;
using Tikai.Core.Tickets;
using Tikai.Data;
using Tikai.Data.Runs;
using Tikai.Mcp;

namespace Tikai.Api.Reviews;

/// <summary>
/// Delivers an approved change set (a <see cref="GitJob.Deliver"/> job, so it runs one at a time with the other writes
/// to mirrors, under the repository's mirror lock). In merge mode the hub merges the latest round into the default
/// branch with <c>git merge-tree</c> (no worktree) and, for a repository with a remote, pushes the commit first and
/// moves its own default branch only once the push got through; in push-branch mode it pushes the run branch as it is.
/// A conflict hands the change back to the reviewer with a conflict item; a failing push is retried by the queue and,
/// after the last attempt, comes back to the inbox as a review with the error.
/// </summary>
internal sealed partial class ChangeSetDelivery(
    TikaiDbContext db,
    IGitService git,
    MirrorPaths mirrors,
    RepositoryCredentials credentials,
    AttentionService attention,
    IJobQueue jobs,
    IEventPublisher events,
    TimeProvider timeProvider,
    ILogger<ChangeSetDelivery> logger)
{
    /// <summary>How often one attempt merges again after the remote's default branch moved during the push.</summary>
    public const int MaxRejectedPushes = 3;

    public async Task RunAsync(JobLease job, Guid changeSetId, CancellationToken cancellationToken)
    {
        var row = await (
            from c in db.ChangeSets
            join r in db.AgentRuns on c.RunId equals r.Id
            join t in db.Tickets on c.TicketId equals t.Id
            join p in db.Projects on t.ProjectId equals p.Id
            join repo in db.Repositories on c.RepositoryId equals repo.Id
            where c.Id == changeSetId
            select new Target(c, r, t, p, repo, ""))
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (row is not { ChangeSet.Status: ChangeSetStatus.Delivering, Run.State: AgentRunState.InReview })
        {
            return; // delivered already, or the run ended meanwhile
        }
        var target = row with { TicketKey = TicketKey.Format(row.Project.Key, row.Ticket.Number) };
        var round = await db.ReviewRounds.AsNoTracking().Where(r => r.ChangeSetId == changeSetId)
            .OrderByDescending(r => r.RoundNo).FirstAsync(cancellationToken);
        var approver = await db.Users.AsNoTracking().Where(u => u.Id == round.VerdictById)
            .Select(u => new GitIdentity(u.DisplayName, u.Email ?? "")).SingleOrDefaultAsync(cancellationToken)
            ?? new GitIdentity("Tikai user", "");
        var settings = ProjectSettings.Parse(row.Project.Settings).DeliveryOrDefault;
        var remote = row.Repository.RemoteUrl is { } url ? GitRemoteUrl.Parse(url) : null;

        GitCredential? credential;
        try
        {
            credential = credentials.Unprotect(row.Repository);
        }
        catch (CryptographicException)
        {
            await FailAsync(target, round, "The repository's credential cannot be decrypted (the hub's keys changed). Enter it again, then approve again.", cancellationToken);
            return;
        }

        Outcome outcome;
        try
        {
            outcome = settings.Mode == DeliveryMode.PushBranch && remote is not null
                ? await PushBranchAsync(target, round, remote, credential, cancellationToken)
                : await MergeAsync(target, round, settings.MergeStyle ?? MergeStyle.Squash, approver, remote, credential, cancellationToken);
        }
        catch (GitException ex)
        {
            var error = ex.Message.Replace(mirrors.For(row.Project.Id, row.Repository.Id), "<mirror>", StringComparison.Ordinal);
            LogDeliveryFailed(changeSetId, job.Attempts, error);
            if (job.Attempts < job.MaxAttempts)
            {
                // The queue tries again after a pause; people see why meanwhile.
                await NoteErrorAsync(changeSetId, $"Attempt {job.Attempts} of {job.MaxAttempts} failed, trying again: {error}", cancellationToken);
                throw;
            }
            await FailAsync(target, round, error, cancellationToken);
            return;
        }

        switch (outcome)
        {
            case Delivered delivered:
                await DeliveredAsync(target, round, delivered, cancellationToken);
                break;
            case Conflicted conflicted:
                await ConflictAsync(target, round, conflicted.Files, cancellationToken);
                break;
        }
    }

    private async Task<Outcome> MergeAsync(
        Target target, ReviewRound round, MergeStyle style, GitIdentity approver, GitRemoteUrl? remote, GitCredential? credential,
        CancellationToken cancellationToken)
    {
        var mirror = mirrors.For(target.Project.Id, target.Repository.Id);
        var branch = target.Repository.DefaultBranch;
        var local = "refs/heads/" + branch;
        var tracking = "refs/remotes/origin/" + branch;
        var summary = AttentionJson.Deserialize<ReviewPayload>(round.SelfReport).Summary;
        var message = MergeMessage.Build(target.TicketKey, target.Ticket.Title, summary, approver, style);
        for (var attempt = 1; ; attempt++)
        {
            // With a remote, merge onto what the remote has now: the hub's own branch never gets ahead of it.
            if (remote is not null)
            {
                await git.FetchAsync(mirror, remote, credential, branch, cancellationToken);
            }
            var onto = (remote is null ? null : await git.ResolveCommitAsync(mirror, tracking, cancellationToken))
                ?? await git.ResolveCommitAsync(mirror, local, cancellationToken)
                ?? throw new GitException($"The default branch \"{branch}\" has no commits.");

            var merged = await git.MergeTreeAsync(mirror, onto, round.HeadSha, cancellationToken);
            if (!merged.Clean)
            {
                return new Conflicted(merged.Conflicts);
            }
            if (merged.Tree == await git.ResolveTreeAsync(mirror, onto, cancellationToken))
            {
                // Nothing left to merge: an earlier attempt pushed it and the hub stopped before recording that (or
                // someone merged the same change by hand). No empty commit.
                return new Delivered(onto, false, $"Already in {branch}; nothing new to merge");
            }
            string[] parents = style == MergeStyle.MergeCommit ? [onto, round.HeadSha] : [onto];
            var commit = await git.CommitTreeAsync(mirror, merged.Tree, parents, message, GitIdentity.Agent, approver, cancellationToken);

            if (remote is not null)
            {
                if (await git.PushAsync(mirror, remote, credential, commit, local, cancellationToken) == PushResult.Rejected)
                {
                    if (attempt < MaxRejectedPushes)
                    {
                        continue;
                    }
                    throw new GitException($"The remote's \"{branch}\" kept moving while the hub pushed ({MaxRejectedPushes} tries).");
                }
                await git.UpdateRefAsync(mirror, tracking, commit, null, cancellationToken);
                await git.UpdateRefAsync(mirror, local, commit, null, cancellationToken);
            }
            else
            {
                // Only from the commit it was merged onto: nothing else writes here while the job holds the mirror lock.
                await git.UpdateRefAsync(mirror, local, commit, onto, cancellationToken);
            }
            if (target.Run.Branch is { } runBranch)
            {
                // The round refs keep the commits for the review history.
                await git.DeleteRefAsync(mirror, "refs/heads/" + runBranch, cancellationToken);
            }
            var how = style == MergeStyle.MergeCommit ? "a merge commit" : "squashed";
            return new Delivered(commit, false,
                remote is null ? $"Merged into {branch} ({how})" : $"Merged into {branch} ({how}) and pushed");
        }
    }

    private async Task<Outcome> PushBranchAsync(
        Target target, ReviewRound round, GitRemoteUrl remote, GitCredential? credential, CancellationToken cancellationToken)
    {
        var mirror = mirrors.For(target.Project.Id, target.Repository.Id);
        var branch = target.Run.Branch ?? throw new GitException("The run has no branch.");
        if (await git.PushAsync(mirror, remote, credential, round.HeadSha, "refs/heads/" + branch, cancellationToken) == PushResult.Rejected)
        {
            throw new GitException($"The remote already has a branch \"{branch}\" with other commits. Delete it there, then approve again.");
        }
        return new Delivered(round.HeadSha, true, $"Pushed the branch {branch}");
    }

    /// <summary>The run ends merged (or pushed), the change set records the commit, the ticket is done.</summary>
    private async Task DeliveredAsync(Target target, ReviewRound round, Delivered delivered, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var runId = target.Run.Id;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var run = await RunResumes.LockAsync(db, runId, cancellationToken);
        var next = RunStateMachine.Next(AgentRunState.InReview, delivered.Pushed ? RunTrigger.Push : RunTrigger.Merge)!.Value;
        if (run is not { State: AgentRunState.InReview })
        {
            return;
        }
        await db.AgentRuns.Where(r => r.Id == runId && r.State == AgentRunState.InReview)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, next).SetProperty(r => r.EndedAt, now).SetProperty(r => r.UpdatedAt, now), cancellationToken);
        await db.ChangeSets.Where(c => c.Id == target.ChangeSet.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, delivered.Pushed ? ChangeSetStatus.Pushed : ChangeSetStatus.Merged)
                .SetProperty(c => c.DeliveredSha, delivered.Sha)
                .SetProperty(c => c.DeliveryError, (string?)null)
                .SetProperty(c => c.UpdatedAt, now), cancellationToken);
        await db.Tickets.Where(t => t.Id == run.TicketId && (t.Status == TicketStatus.InReview || t.Status == TicketStatus.InProgress))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TicketStatus.Done).SetProperty(t => t.UpdatedAt, now), cancellationToken);
        var open = await db.AttentionItems.Where(i => i.RunId == runId && i.Status == AttentionStatus.Open).Select(i => i.Id).ToListAsync(cancellationToken);
        foreach (var item in open)
        {
            // Dismissed in the approver's name: the approval is what settled them.
            await attention.DismissAsync(item, round.VerdictById!.Value, cancellationToken);
        }
        await attention.DismissOpenBlockingAsync(runId, cancellationToken);
        await NoteAsync(runId, delivered.Note, new JsonObject { ["commit"] = delivered.Sha, ["round"] = round.RoundNo }, now, cancellationToken);
        await RunCleanups.EnqueueAsync(jobs, runId, cancellationToken);
        await PreviewStops.ForChangeSetAsync(db, jobs, events, target.ChangeSet.Id, PreviewStops.ChangeSetEnded, now, cancellationToken);
        await events.PublishAsync(AgentRun.Topic, runId, cancellationToken);
        await events.PublishAsync(ChangeSet.Topic, target.ChangeSet.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogDelivered(target.ChangeSet.Id, delivered.Sha, delivered.Note);
    }

    /// <summary>The change no longer merges: back to the reviewer, with a conflict item that offers a rebase.</summary>
    private async Task ConflictAsync(Target target, ReviewRound round, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var branch = target.Repository.DefaultBranch;
        var shown = files.Count <= 5 ? string.Join(", ", files) : string.Join(", ", files.Take(5)) + $" and {files.Count - 5} more";
        var error = $"Round {round.RoundNo} conflicts with the current {branch} in {shown}.";
        await ReopenAsync(target, round, error, async now =>
        {
            await attention.RecordAsync(new RunScope(target.Run.Id, target.Ticket.Id, target.Project.Id), AttentionKind.Conflict,
                new ConflictPayload(round.RoundNo, branch, files), cancellationToken);
            await NoteAsync(target.Run.Id, $"Not merged: conflicts with {branch}", new JsonObject { ["files"] = new JsonArray([.. files.Take(50).Select(f => (JsonNode)f)]) }, now, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>The last attempt failed too: back to the reviewer, as a review again with the error.</summary>
    private Task FailAsync(Target target, ReviewRound round, string error, CancellationToken cancellationToken) =>
        ReopenAsync(target, round, error, async now =>
        {
            await attention.RecordAsync(new RunScope(target.Run.Id, target.Ticket.Id, target.Project.Id), AttentionKind.Review,
                AttentionJson.Deserialize<ReviewPayload>(round.SelfReport), cancellationToken);
            await NoteAsync(target.Run.Id, "Delivery failed", new JsonObject { ["reason"] = error }, now, cancellationToken);
        }, cancellationToken);

    /// <summary>The change set is open for a verdict again (the approval did not take effect), with the reason shown.</summary>
    private async Task ReopenAsync(Target target, ReviewRound round, string error, Func<DateTimeOffset, Task> raise, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var run = await RunResumes.LockAsync(db, target.Run.Id, cancellationToken);
        var reopened = await db.ChangeSets.Where(c => c.Id == target.ChangeSet.Id && c.Status == ChangeSetStatus.Delivering)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, ChangeSetStatus.Open)
                .SetProperty(c => c.DeliveryError, ChangeSetRules.DeliveryError(error))
                .SetProperty(c => c.UpdatedAt, now), cancellationToken);
        if (reopened == 0 || run is not { State: AgentRunState.InReview })
        {
            return;
        }
        await db.ReviewRounds.Where(r => r.Id == round.Id && r.Verdict == ReviewVerdict.Approve)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Verdict, (ReviewVerdict?)null)
                .SetProperty(r => r.VerdictById, (Guid?)null)
                .SetProperty(r => r.VerdictAt, (DateTimeOffset?)null)
                .SetProperty(r => r.UpdatedAt, now), cancellationToken);
        await raise(now);
        await events.PublishAsync(AgentRun.Topic, target.Run.Id, cancellationToken);
        await events.PublishAsync(ChangeSet.Topic, target.ChangeSet.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task NoteErrorAsync(Guid changeSetId, string error, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await db.ChangeSets.Where(c => c.Id == changeSetId && c.Status == ChangeSetStatus.Delivering)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeliveryError, ChangeSetRules.DeliveryError(error)).SetProperty(c => c.UpdatedAt, now), cancellationToken);
        await events.PublishAsync(ChangeSet.Topic, changeSetId, cancellationToken);
    }

    private async Task NoteAsync(Guid runId, string text, JsonObject details, DateTimeOffset now, CancellationToken cancellationToken)
    {
        details["source"] = "hub";
        details["text"] = text;
        await db.AppendAsync(runId, [new ParsedEvent(RunEventKind.Log, details)], null, now, cancellationToken);
        await events.PublishAsync(RunEvent.Topic, runId, cancellationToken);
    }

    private sealed record Target(ChangeSet ChangeSet, AgentRun Run, Ticket Ticket, Project Project, Repository Repository, string TicketKey);

    private abstract record Outcome;

    /// <param name="Pushed">The run branch was pushed (push-branch mode), not merged.</param>
    private sealed record Delivered(string Sha, bool Pushed, string Note) : Outcome;

    private sealed record Conflicted(IReadOnlyList<string> Files) : Outcome;

    [LoggerMessage(Level = LogLevel.Information, Message = "Change set {ChangeSetId} delivered at {Sha}: {Note}")]
    private partial void LogDelivered(Guid changeSetId, string sha, string note);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivering change set {ChangeSetId} failed on attempt {Attempt}: {Error}")]
    private partial void LogDeliveryFailed(Guid changeSetId, int attempt, string error);
}
