using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Tikai.Api.Attention;
using Tikai.Api.Reviews;
using Tikai.Api.Tickets;
using Tikai.Core.Attention;
using Tikai.Core.Git;
using Tikai.Core.Reviews;
using Tikai.Core.Runs;
using Tikai.Core.Tickets;
using Tikai.IntegrationTests.Auth;
using Tikai.IntegrationTests.Git;
using Tikai.IntegrationTests.Runs;
using Tikai.Llm.Reviews;
using Tikai.Review;

namespace Tikai.IntegrationTests.Reviews;

// Comments, request changes and the fix round with the stub agent: the agent reads the sent comments, replies,
// resubmits, and the reviewer sees the interdiff (with and without a changed base).
public sealed class FixRoundTests(PostgresFixture postgres, AgentHost agents) : IClassFixture<AgentHost>
{
    [Fact]
    public async Task Sent_comments_reach_the_resumed_agent_whose_next_round_shows_the_interdiff()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("fix");
        var mirror = await hub.MirrorAsync(ticket);
        await TestGit.CommitAsync(mirror, "main", "Seed", ("seed.txt", "seed\n"));
        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);
        Assert.Equal(AgentRunState.InReview, run.State);
        var id = run.ChangeSetId!.Value;

        // Nothing to send yet: request changes is refused.
        await ExpectAsync(HttpStatusCode.Conflict, hub.Browser.PostAsync($"/api/change-sets/{id}/verdict", new { verdict = "request_changes" }));

        // A comment for the agent with a note under it, a note-only thread, and one that is deleted again.
        var fix = await ThreadAsync(hub, id, new { roundNo = 1, path = "fix.txt", side = "new", lineStart = 1, body = "Say fixed.", action = "send_to_agent" });
        Assert.Equal((1, "fix.txt", DiffSide.New, 1, 1), (fix.RoundNo, fix.Path, fix.Side, fix.LineStart, fix.LineEnd));
        await ExpectAsync(HttpStatusCode.OK, hub.Browser.PostAsync($"/api/review-threads/{fix.Id}/comments", new { body = "(between us)", action = "note" }));
        var note = await ThreadAsync(hub, id, new { roundNo = 1, path = "keep.txt", side = "new", lineStart = 1, body = "Looks fine.", action = "note" });
        var gone = await ThreadAsync(hub, id, new { roundNo = 1, body = "Oops.", action = "send_to_agent" });
        await ExpectAsync(HttpStatusCode.NoContent, hub.Browser.DeleteAsync($"/api/review-comments/{gone.Comments[0].Id}"));

        // Anchors are checked against the round: lines inside the file, a side where it exists.
        await ExpectAsync(HttpStatusCode.BadRequest, hub.Browser.PostAsync($"/api/change-sets/{id}/threads", new { roundNo = 1, path = "fix.txt", side = "new", lineStart = 3, body = "x", action = "note" }));
        await ExpectAsync(HttpStatusCode.BadRequest, hub.Browser.PostAsync($"/api/change-sets/{id}/threads", new { roundNo = 1, path = "fix.txt", side = "old", lineStart = 1, body = "x", action = "note" }));
        await ExpectAsync(HttpStatusCode.BadRequest, hub.Browser.PostAsync($"/api/change-sets/{id}/threads", new { roundNo = 1, path = "fix.txt", lineStart = 1, body = "x", action = "note" }));
        await ExpectAsync(HttpStatusCode.NotFound, hub.Browser.PostAsync($"/api/change-sets/{id}/threads", new { roundNo = 2, body = "x", action = "note" }));
        // Own unsent comments can be edited.
        await ExpectAsync(HttpStatusCode.OK, hub.Browser.PatchAsync($"/api/review-comments/{fix.Comments[0].Id}", new { body = "Say fixed version.", action = "send_to_agent" }));

        await ExpectAsync(HttpStatusCode.OK, hub.Browser.PostAsync($"/api/change-sets/{id}/verdict", new { verdict = "request_changes", message = "Also keep it short." }));
        var requested = await hub.GetRunAsync(run.Id);
        Assert.True(requested.State is AgentRunState.ChangesRequested or AgentRunState.Running, requested.State.ToString());
        Assert.Equal(TicketStatus.InProgress, (await hub.Browser.GetJsonAsync<TicketInfo>($"/api/projects/{hub.Slugs[ticket.Id]}/tickets/{ticket.Number}")).Status);
        Assert.DoesNotContain(await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"), i => i.RunId == run.Id);

        // The agent resumed its session, read the sent comments (not the notes), replied and submitted round 2.
        await hub.WaitForAsync(run.Id, r => r.State == AgentRunState.InReview);
        run = await hub.WaitForEndAsync(run.Id);
        Assert.Equal(AgentRunState.InReview, run.State);
        var stub = await StubAsync(hub, run.Id, "STUB_FIX ");
        Assert.Equal(("resume", "1"), (stub.GetProperty("mode").GetString(), stub.GetProperty("round").GetString()));
        var seen = stub.GetProperty("threads").EnumerateArray().ToList();
        Assert.Equal(2, seen.Count);
        Assert.Equal(("fix.txt", "new", 1, 1), (seen[0].GetProperty("path").GetString(), seen[0].GetProperty("side").GetString(), seen[0].GetProperty("line_start").GetInt32(), seen[0].GetProperty("round").GetInt32()));
        Assert.Equal(["reviewer: Say fixed version."], seen[0].GetProperty("comments").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(JsonValueKind.Null, seen[1].GetProperty("path").ValueKind);
        Assert.Equal(["reviewer: Also keep it short."], seen[1].GetProperty("comments").EnumerateArray().Select(c => c.GetString()));
        Assert.Contains("There is no review thread", stub.GetProperty("unknown").GetString(), StringComparison.Ordinal);
        var resubmitted = (await StubAsync(hub, run.Id, "STUB_RESUBMITTED ")).GetProperty("submitted").GetString()!;
        Assert.StartsWith("Submitted for review as round 2: 2 commits", resubmitted, StringComparison.Ordinal);
        Assert.DoesNotContain("no reply", resubmitted, StringComparison.Ordinal);

        // The threads: sent comments are fixed now, the agent's replies are there, the note thread is untouched.
        var threads = await ThreadsAsync(hub, id);
        var fixThread = threads.Single(t => t.Id == fix.Id);
        Assert.Equal(
            [(CommentAuthorKind.Person, "Say fixed version.", true), (CommentAuthorKind.Person, "(between us)", false), (CommentAuthorKind.Agent, "Fixed: Say fixed version.", false)],
            fixThread.Comments.Select(c => (c.Author, c.Body, c.SentAt is not null)));
        Assert.Equal("Agent", fixThread.Comments[2].AuthorName);
        Assert.False(fixThread.Comments[0].CanEdit);
        Assert.Equal(["Looks fine."], threads.Single(t => t.Id == note.Id).Comments.Select(c => c.Body));
        Assert.DoesNotContain(threads, t => t.Id == gone.Id);
        await ExpectAsync(HttpStatusCode.Conflict, hub.Browser.PatchAsync($"/api/review-comments/{fix.Comments[0].Id}", new { body = "Changed later.", action = "send_to_agent" }));
        await ExpectAsync(HttpStatusCode.OK, hub.Browser.PostAsync($"/api/review-threads/{fix.Id}/resolve", new { }));
        var resolved = (await ThreadsAsync(hub, id)).Single(t => t.Id == fix.Id);
        Assert.Equal(ThreadStatus.Resolved, resolved.Status);
        Assert.NotNull(resolved.ResolvedBy);

        // Round 2: the interdiff is only what the fix changed; the full view still has the whole change.
        var changeSet = await hub.Browser.GetJsonAsync<ChangeSetInfo>($"/api/change-sets/{id}");
        Assert.Equal([ReviewVerdict.RequestChanges, (ReviewVerdict?)null], changeSet.Rounds.Select(r => r.Verdict));
        Assert.Equal(changeSet.Rounds[0].BaseSha, changeSet.Rounds[1].BaseSha);
        var interdiff = await hub.Browser.GetJsonAsync<ReviewFilesInfo>($"/api/change-sets/{id}/files");
        Assert.Equal((2, ReviewView.Interdiff, InterdiffKind.Heads, 1), (interdiff.RoundNo, interdiff.View, interdiff.Interdiff, interdiff.PreviousRound));
        Assert.Equal((changeSet.Rounds[0].HeadSha, changeSet.Rounds[1].HeadSha), (interdiff.FromSha, interdiff.ToSha));
        var changed = Assert.Single(interdiff.Files);
        Assert.Equal(("fix.txt", GitChangeKind.Modified, 1, 1), (changed.Path, changed.Kind, changed.Additions, changed.Deletions));
        var contents = await hub.Browser.GetJsonAsync<FileContentsInfo>($"/api/change-sets/{id}/file?path=fix.txt");
        Assert.Equal(("first version\nsecond line\n", "fixed version\nsecond line\n"), (contents.Old!.Text, contents.New!.Text));
        var full = await hub.Browser.GetJsonAsync<ReviewFilesInfo>($"/api/change-sets/{id}/files?full=true");
        Assert.Equal((ReviewView.Full, InterdiffKind.None), (full.View, full.Interdiff));
        Assert.Equal(["fix.txt", "keep.txt"], full.Files.Select(f => f.Path));
        Assert.Equal((GitChangeKind.Added, (FileSide?)null), (full.Files[0].Kind, (await hub.Browser.GetJsonAsync<FileContentsInfo>($"/api/change-sets/{id}/file?path=fix.txt&full=true")).Old));
        // The first round still shows its own change.
        Assert.Equal(["fix.txt", "keep.txt"], (await hub.Browser.GetJsonAsync<ReviewFilesInfo>($"/api/change-sets/{id}/files?round=1")).Files.Select(f => f.Path));
        // keep.txt is not in the interdiff; marking it viewed works in the full view.
        await ExpectAsync(HttpStatusCode.NotFound, hub.Browser.PutAsync($"/api/change-sets/{id}/viewed", new { path = "keep.txt", viewed = true }));
        await ExpectAsync(HttpStatusCode.OK, hub.Browser.PutAsync($"/api/change-sets/{id}/viewed", new { path = "keep.txt", viewed = true, full = true }));

        // Round 2's analysis read the interdiff, the earlier analysis and the threads.
        var analysis = await AnalysisAsync(hub, id, 2);
        Assert.Equal((AnalysisStatus.Done, ReviewAnalyzer.InterdiffPromptName, 1), (analysis.Status, analysis.Result!.PromptVersion, analysis.Result.InterdiffSince));
        Assert.StartsWith("interdiff, ", analysis.Result.TierReason, StringComparison.Ordinal);
        var message = agents.Llm.Requests.Select(r => r.Body.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString()!)
            .Last(m => m.Contains("<previous_analysis", StringComparison.Ordinal));
        Assert.Contains("<previous_analysis round=\"1\">", message, StringComparison.Ordinal);
        Assert.Contains("Headline: Stub headline.", message, StringComparison.Ordinal);
        Assert.Contains("reviewer: Say fixed version.", message, StringComparison.Ordinal);
        Assert.Contains("agent: Fixed: Say fixed version.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("(between us)", message, StringComparison.Ordinal);
        Assert.Contains("diff --git a/fix.txt b/fix.txt", message, StringComparison.Ordinal);
        Assert.DoesNotContain("diff --git a/keep.txt", message, StringComparison.Ordinal);
        Assert.Contains("A keep.txt (+1 -0)", message, StringComparison.Ordinal);

        // The review is back in the inbox and the ticket in review.
        var reviewItem = Assert.Single(await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"), i => i.RunId == run.Id && i.Kind == AttentionKind.Review);
        Assert.Equal(new RoundBrief(2, 2, 0), reviewItem.Round);
        Assert.Equal(TicketStatus.InReview, (await hub.Browser.GetJsonAsync<TicketInfo>($"/api/projects/{hub.Slugs[ticket.Id]}/tickets/{ticket.Number}")).Status);

        // Once rejected, nothing changes any more.
        await ExpectAsync(HttpStatusCode.OK, hub.Browser.PostAsync($"/api/change-sets/{id}/verdict", new { verdict = "reject" }));
        await hub.WaitForAsync(run.Id, r => r.State == AgentRunState.Cancelled);
        await ExpectAsync(HttpStatusCode.Conflict, hub.Browser.PostAsync($"/api/review-threads/{note.Id}/comments", new { body = "Late.", action = "note" }));
    }

    [Fact]
    public async Task A_rebased_fix_round_lists_the_files_whose_change_differs_and_an_agent_still_running_is_stopped_first()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("fix-rebase");
        var mirror = await hub.MirrorAsync(ticket);
        await TestGit.CommitAsync(mirror, "main", "Seed", ("seed.txt", "seed\n"));
        var run = await hub.WaitForAsync((await hub.AssignAsync(ticket)).Id, r => r.State == AgentRunState.InReview);
        var id = run.ChangeSetId!.Value;

        // The agent lingers after submitting: the request stops it, and its end starts the fix round.
        await ExpectAsync(HttpStatusCode.OK, hub.Browser.PostAsync($"/api/change-sets/{id}/verdict", new { verdict = "request_changes", message = "Fix it." }));
        await hub.WaitForAsync(run.Id, r => r.State == AgentRunState.InReview && r.ChangeSetId is not null, seconds: 180);
        run = await hub.WaitForEndAsync(run.Id);
        Assert.Contains(await hub.MessagesAsync(run.Id), m => m == "Lingering after the submission.");
        var stub = await StubAsync(hub, run.Id, "STUB_FIX ");
        Assert.Equal(["reviewer: Fix it."], stub.GetProperty("threads")[0].GetProperty("comments").EnumerateArray().Select(c => c.GetString()));

        // A new base: a diff of the heads would show seed.txt as removed; the per-file comparison shows only fix.txt.
        var changeSet = await hub.Browser.GetJsonAsync<ChangeSetInfo>($"/api/change-sets/{id}");
        Assert.Equal(2, changeSet.Rounds.Count);
        Assert.NotEqual(changeSet.Rounds[0].BaseSha, changeSet.Rounds[1].BaseSha);
        var interdiff = await hub.Browser.GetJsonAsync<ReviewFilesInfo>($"/api/change-sets/{id}/files");
        Assert.Equal((InterdiffKind.PerFile, changeSet.Rounds[0].BaseSha), (interdiff.Interdiff, interdiff.PreviousBaseSha));
        Assert.Equal(["fix.txt"], interdiff.Files.Select(f => f.Path));
        Assert.Equal(GitChangeKind.Added, interdiff.Files[0].Kind);
        Assert.Empty(interdiff.Dropped);
        Assert.Equal(changeSet.Rounds[1].BaseSha, interdiff.FromSha);
        Assert.Equal(["fix.txt", "keep.txt"], (await hub.Browser.GetJsonAsync<ReviewFilesInfo>($"/api/change-sets/{id}/files?full=true")).Files.Select(f => f.Path));

        // The analysis falls back to the full diff.
        var analysis = await AnalysisAsync(hub, id, 2);
        Assert.Equal((ReviewAnalyzer.PromptName, (int?)null), (analysis.Result!.PromptVersion, analysis.Result.InterdiffSince));
    }

    private static async Task<ReviewThreadInfo> ThreadAsync(AgentHub hub, Guid changeSetId, object request)
    {
        using var response = await hub.Browser.PostAsync($"/api/change-sets/{changeSetId}/threads", request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<ReviewThreadInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
    }

    private static Task<List<ReviewThreadInfo>> ThreadsAsync(AgentHub hub, Guid changeSetId) =>
        hub.Browser.GetJsonAsync<List<ReviewThreadInfo>>($"/api/change-sets/{changeSetId}/threads");

    private static async Task ExpectAsync(HttpStatusCode status, Task<HttpResponseMessage> call)
    {
        using var response = await call;
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
    }

    private static async Task<JsonElement> StubAsync(AgentHub hub, Guid runId, string prefix)
    {
        var text = (await hub.MessagesAsync(runId)).Last(t => t.StartsWith(prefix, StringComparison.Ordinal));
        return JsonDocument.Parse(text[prefix.Length..]).RootElement;
    }

    private static async Task<AnalysisInfo> AnalysisAsync(AgentHub hub, Guid changeSetId, int roundNo)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            var analysis = (await hub.Browser.GetJsonAsync<ChangeSetInfo>($"/api/change-sets/{changeSetId}")).Rounds[roundNo - 1].Analysis!;
            if (analysis.Status != AnalysisStatus.Pending)
            {
                return analysis;
            }
            await Task.Delay(250, timeout.Token);
        }
    }
}
