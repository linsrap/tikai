using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tikai.Api.Attention;
using Tikai.Api.Projects;
using Tikai.Api.Reviews;
using Tikai.Api.Tickets;
using Tikai.Core.Attention;
using Tikai.Core.Projects;
using Tikai.Core.Reviews;
using Tikai.Core.Runs;
using Tikai.Core.Tickets;
using Tikai.Data;
using Tikai.IntegrationTests.Auth;
using Tikai.IntegrationTests.Git;
using Tikai.IntegrationTests.Runs;

namespace Tikai.IntegrationTests.Reviews;

// Approve with the stub agent: the hub merges (squash or merge commit) or pushes the run branch, the run ends and its
// directory is removed; a conflict goes back to the agent for a rebase; a push that keeps failing comes back to the inbox.
public sealed class DeliveryTests(PostgresFixture postgres, AgentHost agents) : IClassFixture<AgentHost>, IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task An_approved_change_is_squashed_into_main_the_ticket_is_done_and_the_run_directory_removed()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("ok");
        var mirror = await hub.MirrorAsync(ticket);
        var before = await TestGit.TryResolveAsync(mirror, "refs/heads/main");
        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);
        var id = run.ChangeSetId!.Value;
        var head = (await ChangeSetAsync(hub, id)).Rounds[0].HeadSha;

        var approved = await VerdictAsync(hub, id, "approve");
        Assert.Contains(approved.Status, new[] { ChangeSetStatus.Delivering, ChangeSetStatus.Merged });
        Assert.Equal((DeliveryMode.Merge, MergeStyle.Squash, false), (approved.Delivery.Mode, approved.Delivery.MergeStyle, approved.Delivery.HasRemote));
        run = await hub.WaitForAsync(run.Id, r => r.State.IsEnded());
        Assert.Equal(AgentRunState.Merged, run.State);

        // One new commit on main with the round's tree, the agent as author, the approver as committer and co-author.
        var changeSet = await ChangeSetAsync(hub, id);
        Assert.Equal(ChangeSetStatus.Merged, changeSet.Status);
        var merged = changeSet.Delivery.Sha!;
        Assert.Equal(merged, await TestGit.TryResolveAsync(mirror, "refs/heads/main"));
        Assert.Equal(before, await TestGit.RunAsync(mirror, "rev-parse", merged + "^"));
        Assert.Equal(await TestGit.RunAsync(mirror, "rev-parse", head + "^{tree}"), await TestGit.RunAsync(mirror, "rev-parse", merged + "^{tree}"));
        Assert.Equal("Claude Code (Tikai) <claude-code@agents.tikai.invalid>|Ada <ada@example.org>",
            await TestGit.RunAsync(mirror, "log", "-1", "--format=%an <%ae>|%cn <%ce>", merged));
        Assert.Equal("HUB-1: Say hello\n\nAdded hello.txt.\n\nTicket: HUB-1\nCo-authored-by: Ada <ada@example.org>",
            await TestGit.RunAsync(mirror, "log", "-1", "--format=%B", merged));
        // The run branch is gone; the round ref keeps the agent's commits for the review history.
        Assert.Null(await TestGit.TryResolveAsync(mirror, "refs/heads/" + run.Branch));
        Assert.Equal(head, await TestGit.TryResolveAsync(mirror, ReviewRules.RoundRef(id, 1)));

        Assert.Equal(TicketStatus.Done, (await hub.Browser.GetJsonAsync<TicketInfo>($"/api/projects/{hub.Slugs[ticket.Id]}/tickets/{ticket.Number}")).Status);
        Assert.DoesNotContain(await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"), i => i.RunId == run.Id);
        Assert.Contains((await hub.EventsAsync(run.Id)).Select(e => e.Payload.TryGetProperty("text", out var t) ? t.GetString() : null), t => t == "Merged into main (squashed)");
        await ExpectAsync(HttpStatusCode.Conflict, hub.Browser.PostAsync($"/api/change-sets/{id}/verdict", new { verdict = "reject" }));
        await ExpectAsync(HttpStatusCode.Conflict, hub.Browser.PostAsync($"/api/runs/{run.Id}/cancel", new { }));
        await WaitForCleanupAsync(hub, run.Id);

        // The merged main as a bundle: cloned on "people's own computer".
        var repository = Assert.Single(await hub.Browser.GetJsonAsync<List<RepositoryInfo>>($"/api/projects/{hub.Slugs[ticket.Id]}/repositories"));
        using var response = await hub.Browser.GetAsync($"/api/projects/{hub.Slugs[ticket.Id]}/repositories/{repository.Id}/bundle");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("app.bundle", response.Content.Headers.ContentDisposition?.FileName);
        var bundle = _temp.Combine("app.bundle");
        await File.WriteAllBytesAsync(bundle, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var clone = _temp.Combine("clone");
        await TestGit.RunAsync(null, "clone", "--quiet", bundle, clone);
        Assert.Equal("hello\n", (await File.ReadAllTextAsync(Path.Combine(clone, "hello.txt"), TestContext.Current.CancellationToken)).ReplaceLineEndings("\n"));
        Assert.Equal(merged, await TestGit.RunAsync(Path.Combine(clone, ".git"), "rev-parse", "HEAD"));

        // A round as a bundle, fetched into a branch with the command the review screen shows; also after the merge.
        using var round = await hub.Browser.GetAsync($"/api/change-sets/{id}/rounds/1/bundle");
        Assert.Equal(HttpStatusCode.OK, round.StatusCode);
        Assert.Equal("HUB-1-round-1.bundle", round.Content.Headers.ContentDisposition?.FileName);
        var roundBundle = _temp.Combine("HUB-1-round-1.bundle");
        await File.WriteAllBytesAsync(roundBundle, await round.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal($"{head} {ReviewRules.RoundRef(id, 1)}", await TestGit.RunAsync(null, "bundle", "list-heads", roundBundle));
        await TestGit.RunAsync(Path.Combine(clone, ".git"), "fetch", "--quiet", roundBundle, $"+{ReviewRules.RoundRef(id, 1)}:refs/heads/review/HUB-1-r1");
        Assert.Equal(head, await TestGit.RunAsync(Path.Combine(clone, ".git"), "rev-parse", "review/HUB-1-r1"));
        await ExpectAsync(HttpStatusCode.NotFound, hub.Browser.GetAsync($"/api/change-sets/{id}/rounds/9/bundle"));
        await ExpectAsync(HttpStatusCode.NotFound, hub.Browser.GetAsync($"/api/change-sets/{Guid.NewGuid()}/rounds/1/bundle"));
    }

    [Fact]
    public async Task With_a_remote_a_merge_commit_is_pushed_and_a_push_branch_project_pushes_only_the_run_branch()
    {
        await using var hub = await agents.StartHubAsync(postgres);

        // Merge commits, onto a remote that gets the hub's history when it is connected.
        var ticket = await hub.CreateTicketAsync("ok");
        var remote = await TestGit.InitBareAsync(_temp.Combine("merge.git"));
        var connected = await ConnectAsync(hub, ticket, remote);
        Assert.True(connected.Pushed);
        Assert.Equal(TestGit.FileUrl(remote), connected.Repository.RemoteUrl);
        await SettingsAsync(hub, ticket, new { mode = "merge", mergeStyle = "merge_commit" });
        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);
        var id = run.ChangeSetId!.Value;
        // Someone pushes an unrelated change meanwhile: the merge goes onto it.
        var mirror = await hub.MirrorAsync(ticket);
        var start = (await TestGit.TryResolveAsync(remote, "refs/heads/main"))!;
        var other = await TestGit.CommitAsync(remote, "main", "Unrelated", ("other.txt", "other\n"));
        await VerdictAsync(hub, id, "approve");
        run = await hub.WaitForAsync(run.Id, r => r.State.IsEnded());
        Assert.Equal(AgentRunState.Merged, run.State);
        var merged = (await ChangeSetAsync(hub, id)).Delivery.Sha!;
        Assert.Equal(merged, await TestGit.TryResolveAsync(remote, "refs/heads/main"));
        Assert.Equal(merged, await TestGit.TryResolveAsync(mirror, "refs/heads/main"));
        Assert.Equal($"{other} {(await ChangeSetAsync(hub, id)).Rounds[0].HeadSha}", await TestGit.RunAsync(remote, "log", "-1", "--format=%P", merged));
        Assert.StartsWith("Merge HUB-1: Say hello", await TestGit.RunAsync(remote, "log", "-1", "--format=%s", merged), StringComparison.Ordinal);
        Assert.Equal(["hello.txt", "other.txt"], (await TestGit.RunAsync(remote, "ls-tree", "--name-only", merged)).Split('\n'));
        Assert.NotEqual(start, other);

        // Push the branch: the remote gets tikai/… as the agent committed it, main stays as it is.
        var pushTicket = await hub.CreateTicketAsync("ok", "WEB");
        var pushRemote = await TestGit.InitBareAsync(_temp.Combine("push.git"));
        await ConnectAsync(hub, pushTicket, pushRemote);
        var remoteMain = await TestGit.TryResolveAsync(pushRemote, "refs/heads/main");
        await SettingsAsync(hub, pushTicket, new { mode = "push_branch" });
        var pushRun = await hub.WaitForEndAsync((await hub.AssignAsync(pushTicket)).Id);
        var pushId = pushRun.ChangeSetId!.Value;
        var approved = await VerdictAsync(hub, pushId, "approve");
        Assert.Equal(DeliveryMode.PushBranch, approved.Delivery.Mode);
        pushRun = await hub.WaitForAsync(pushRun.Id, r => r.State.IsEnded());
        Assert.Equal(AgentRunState.Pushed, pushRun.State);
        var pushed = await ChangeSetAsync(hub, pushId);
        Assert.Equal((ChangeSetStatus.Pushed, pushed.Rounds[0].HeadSha), (pushed.Status, pushed.Delivery.Sha));
        Assert.Equal(pushed.Rounds[0].HeadSha, await TestGit.TryResolveAsync(pushRemote, "refs/heads/" + pushRun.Branch));
        Assert.Equal(remoteMain, await TestGit.TryResolveAsync(pushRemote, "refs/heads/main"));
        Assert.Equal(TicketStatus.Done, (await hub.Browser.GetJsonAsync<TicketInfo>($"/api/projects/{hub.Slugs[pushTicket.Id]}/tickets/{pushTicket.Number}")).Status);
        await WaitForCleanupAsync(hub, pushRun.Id);

        // A remote with its own history is refused, and the repository keeps its remote.
        var foreign = await TestGit.InitBareAsync(_temp.Combine("foreign.git"));
        await TestGit.CommitAsync(foreign, "main", "Initial commit", ("README.md", "# Foreign\n"));
        var repository = Assert.Single(await hub.Browser.GetJsonAsync<List<RepositoryInfo>>($"/api/projects/{hub.Slugs[pushTicket.Id]}/repositories"));
        using var refused = await hub.Browser.PutAsync($"/api/projects/{hub.Slugs[pushTicket.Id]}/repositories/{repository.Id}/remote", new { remoteUrl = TestGit.FileUrl(foreign) });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Connect an empty repository", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(TestGit.FileUrl(pushRemote), (await hub.Browser.GetJsonAsync<RepositoryInfo>($"/api/projects/{hub.Slugs[pushTicket.Id]}/repositories/{repository.Id}")).RemoteUrl);
    }

    [Fact]
    public async Task A_conflict_goes_back_to_the_reviewer_and_the_agent_rebases_onto_the_moved_default_branch()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("ok");
        var mirror = await hub.MirrorAsync(ticket);
        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);
        var id = run.ChangeSetId!.Value;
        // main moves with a hello.txt of its own: the agent's round no longer merges.
        var moved = await TestGit.CommitAsync(mirror, "main", "People were faster", ("hello.txt", "hi from people\n"));
        await ExpectAsync(HttpStatusCode.Conflict, hub.Browser.PostAsync($"/api/change-sets/{id}/rebase", new { }));

        await VerdictAsync(hub, id, "approve");
        var conflict = await WaitForAsync(hub, id, c => c.Delivery.Conflict is not null);
        Assert.Equal((ChangeSetStatus.Open, (ReviewVerdict?)null), (conflict.Status, conflict.Rounds[0].Verdict));
        Assert.Equal(["hello.txt"], conflict.Delivery.Conflict!.Files);
        Assert.Contains("conflicts with the current main in hello.txt", conflict.Delivery.Error, StringComparison.Ordinal);
        Assert.Equal(moved, await TestGit.TryResolveAsync(mirror, "refs/heads/main"));
        Assert.Equal(AgentRunState.InReview, (await hub.GetRunAsync(run.Id)).State);
        var item = Assert.Single(await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"), i => i.RunId == run.Id);
        Assert.Equal((AttentionKind.Conflict, id, 1, "main"), (item.Kind, item.ChangeSetId, item.Conflict!.RoundNo, item.Conflict.DefaultBranch));

        // Ask the agent to rebase: it gets main as a bundle, rebases, replies and submits round 2 on the new base.
        await ExpectAsync(HttpStatusCode.OK, hub.Browser.PostAsync($"/api/change-sets/{id}/rebase", new { }));
        Assert.DoesNotContain(await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"), i => i.RunId == run.Id && i.Kind == AttentionKind.Conflict);
        await hub.WaitForAsync(run.Id, r => r.State == AgentRunState.InReview && r.Attempt > 1, seconds: 180);
        run = await hub.WaitForEndAsync(run.Id);
        var stub = await StubAsync(hub, run.Id, "STUB_REBASE ");
        Assert.Equal(("resume", "1", "- hello.txt", "/run/prep/default.bundle", 1),
            (stub.GetProperty("mode").GetString(), stub.GetProperty("round").GetString(), stub.GetProperty("files").GetString(),
                stub.GetProperty("bundle").GetString(), stub.GetProperty("threads").GetInt32()));
        var rebased = await ChangeSetAsync(hub, id);
        Assert.Equal((2, moved), (rebased.Rounds.Count, rebased.Rounds[1].BaseSha));
        Assert.Equal(ReviewVerdict.RequestChanges, rebased.Rounds[0].Verdict);
        var thread = Assert.Single(await hub.Browser.GetJsonAsync<List<ReviewThreadInfo>>($"/api/change-sets/{id}/threads"));
        Assert.Contains("these files conflict:\n- hello.txt", thread.Comments[0].Body, StringComparison.Ordinal);
        Assert.Equal("Rebased onto the current main.", thread.Comments[1].Body);

        // Round 2 merges.
        await VerdictAsync(hub, id, "approve");
        run = await hub.WaitForAsync(run.Id, r => r.State.IsEnded());
        Assert.Equal(AgentRunState.Merged, run.State);
        var merged = (await ChangeSetAsync(hub, id)).Delivery.Sha!;
        Assert.Equal(moved, await TestGit.RunAsync(mirror, "rev-parse", merged + "^"));
        Assert.Equal("hello\n", await TestGit.RunAsync(mirror, "cat-file", "blob", merged + ":hello.txt") + "\n");
    }

    [Fact]
    public async Task A_rejected_conflict_leaves_no_item_and_a_change_already_on_main_adds_no_empty_commit()
    {
        await using var hub = await agents.StartHubAsync(postgres);

        // Conflict, then reject: the run ends and its conflict item goes with it.
        var ticket = await hub.CreateTicketAsync("ok");
        var mirror = await hub.MirrorAsync(ticket);
        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);
        await TestGit.CommitAsync(mirror, "main", "People were faster", ("hello.txt", "hi from people\n"));
        await VerdictAsync(hub, run.ChangeSetId!.Value, "approve");
        await WaitForAsync(hub, run.ChangeSetId!.Value, c => c.Delivery.Conflict is not null);
        await VerdictAsync(hub, run.ChangeSetId!.Value, "reject");
        await hub.WaitForAsync(run.Id, r => r.State == AgentRunState.Cancelled);
        Assert.DoesNotContain(await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"), i => i.RunId == run.Id);

        // Someone put the same hello.txt on main meanwhile: nothing is left to merge, and no empty commit is made.
        var same = await hub.CreateTicketAsync("ok", "WEB");
        var sameMirror = await hub.MirrorAsync(same);
        var sameRun = await hub.WaitForEndAsync((await hub.AssignAsync(same)).Id);
        var main = await TestGit.CommitAsync(sameMirror, "main", "By hand", ("hello.txt", "hello\n"));
        await VerdictAsync(hub, sameRun.ChangeSetId!.Value, "approve");
        sameRun = await hub.WaitForAsync(sameRun.Id, r => r.State.IsEnded());
        Assert.Equal(AgentRunState.Merged, sameRun.State);
        Assert.Equal(main, (await ChangeSetAsync(hub, sameRun.ChangeSetId!.Value)).Delivery.Sha);
        Assert.Equal(main, await TestGit.TryResolveAsync(sameMirror, "refs/heads/main"));
        Assert.Contains((await hub.EventsAsync(sameRun.Id)).Select(e => e.Payload.TryGetProperty("text", out var t) ? t.GetString() : null),
            t => t == "Already in main; nothing new to merge");
    }

    [Fact]
    public async Task A_push_that_keeps_failing_comes_back_to_the_inbox_with_the_error_and_merges_once_it_works()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("ok");
        var remote = await TestGit.InitBareAsync(_temp.Combine("guarded.git"));
        await ConnectAsync(hub, ticket, remote);
        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);
        var id = run.ChangeSetId!.Value;
        // The remote declines every push (like a protected branch).
        var hook = Path.Combine(remote, "hooks", "pre-receive");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\necho 'main is protected' >&2\nexit 1\n", TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var before = await TestGit.TryResolveAsync(await hub.MirrorAsync(ticket), "refs/heads/main");

        await VerdictAsync(hub, id, "approve");
        // Three attempts (the queue waits 5 s, then 10 s), then back to the reviewer.
        var failed = await WaitForAsync(hub, id, c => c.Status == ChangeSetStatus.Open, seconds: 90);
        Assert.Contains("pre-receive hook declined", failed.Delivery.Error, StringComparison.Ordinal);
        Assert.Null(failed.Rounds[0].Verdict);
        Assert.Equal(before, await TestGit.TryResolveAsync(await hub.MirrorAsync(ticket), "refs/heads/main"));
        var item = Assert.Single(await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"), i => i.RunId == run.Id);
        Assert.Equal(AttentionKind.Review, item.Kind);
        Assert.Contains("pre-receive hook declined", item.DeliveryError, StringComparison.Ordinal);
        Assert.Equal(AgentRunState.InReview, (await hub.GetRunAsync(run.Id)).State);

        File.Delete(hook);
        await VerdictAsync(hub, id, "approve");
        run = await hub.WaitForAsync(run.Id, r => r.State.IsEnded());
        Assert.Equal(AgentRunState.Merged, run.State);
        var done = await ChangeSetAsync(hub, id);
        Assert.Null(done.Delivery.Error);
        Assert.Equal(done.Delivery.Sha, await TestGit.TryResolveAsync(remote, "refs/heads/main"));
    }

    private static async Task<ConnectRemoteInfo> ConnectAsync(AgentHub hub, TicketInfo ticket, string remote)
    {
        var slug = hub.Slugs[ticket.Id];
        var repository = Assert.Single(await hub.Browser.GetJsonAsync<List<RepositoryInfo>>($"/api/projects/{slug}/repositories"));
        using var response = await hub.Browser.PutAsync($"/api/projects/{slug}/repositories/{repository.Id}/remote", new { remoteUrl = TestGit.FileUrl(remote) });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<ConnectRemoteInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
    }

    private static async Task SettingsAsync(AgentHub hub, TicketInfo ticket, object delivery)
    {
        using var response = await hub.Browser.PutAsync($"/api/projects/{hub.Slugs[ticket.Id]}/settings", new { analysisModels = new { }, delivery });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<ChangeSetInfo> VerdictAsync(AgentHub hub, Guid id, string verdict)
    {
        using var response = await hub.Browser.PostAsync($"/api/change-sets/{id}/verdict", new { verdict });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<ChangeSetInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
    }

    private static Task<ChangeSetInfo> ChangeSetAsync(AgentHub hub, Guid id) => hub.Browser.GetJsonAsync<ChangeSetInfo>($"/api/change-sets/{id}");

    private static async Task<ChangeSetInfo> WaitForAsync(AgentHub hub, Guid id, Func<ChangeSetInfo, bool> condition, int seconds = 60)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        while (true)
        {
            var changeSet = await ChangeSetAsync(hub, id);
            if (condition(changeSet))
            {
                return changeSet;
            }
            await Task.Delay(250, timeout.Token);
        }
    }

    /// <summary>The cleanup job removed the run's directory (the reconciler queues it again if it failed).</summary>
    private static async Task WaitForCleanupAsync(AgentHub hub, Guid runId)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            await using (var scope = hub.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
                if (await db.AgentRuns.AnyAsync(r => r.Id == runId && r.CleanedAt != null, timeout.Token))
                {
                    return;
                }
            }
            await Task.Delay(250, timeout.Token);
        }
    }

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
}
