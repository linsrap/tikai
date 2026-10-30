using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Tikai.Api.Attention;
using Tikai.Api.Projects;
using Tikai.Api.Reviews;
using Tikai.Api.Tickets;
using Tikai.Core.Attention;
using Tikai.Core.Git;
using Tikai.Core.Projects;
using Tikai.Core.Reviews;
using Tikai.Core.Runs;
using Tikai.Core.Tickets;
using Tikai.IntegrationTests.Git;
using Tikai.IntegrationTests.Llm;
using Tikai.IntegrationTests.Runs;

namespace Tikai.IntegrationTests.Reviews;

// submit_for_review with real containers (the stub agent) and real git: the work lands in the mirror as a change set
// whose files the review API shows.
public sealed class ReviewTests(PostgresFixture postgres, AgentHost agents) : IClassFixture<AgentHost>
{
    private const string OldName = "a file that moves\nwith a few lines\nso that git sees the rename\n";

    [Fact]
    public async Task A_submission_becomes_a_change_set_whose_files_can_be_read_marked_viewed_and_rejected()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("review");
        var mirror = await hub.MirrorAsync(ticket);
        var seeded = await TestGit.CommitAsync(mirror, "main", "Seed", ("change.txt", "first line\n"), ("gone.txt", "bye\n"), ("old-name.txt", OldName));

        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);

        // In review since the submit call; the container's end later kept it there and stored its exit and cost.
        Assert.Equal(AgentRunState.InReview, run.State);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(0.003m, run.CostUsd);
        Assert.NotNull(run.ChangeSetId);
        Assert.Equal(TicketStatus.InReview, (await hub.Browser.GetJsonAsync<TicketInfo>($"/api/projects/{hub.Slugs[ticket.Id]}/tickets/{ticket.Number}")).Status);

        // What the agent was told: the round, the uncommitted file, the second submit, the refused question.
        var stub = await StubReviewAsync(hub, run.Id);
        Assert.Matches(@"^Submitted for review as round 1: 1 commit, head [0-9a-f]{8}\. 1 uncommitted file was not submitted: \?\? notes\.txt\.", stub.GetProperty("submitted").GetString());
        Assert.StartsWith("Your work is already in review (round 1, ", stub.GetProperty("again").GetString(), StringComparison.Ordinal);
        Assert.Contains("Your work is in review", stub.GetProperty("asked").GetString(), StringComparison.Ordinal);

        // The change set: one round from the seeded commit to the run branch's head, which the mirror keeps twice.
        var changeSet = await hub.Browser.GetJsonAsync<ChangeSetInfo>($"/api/change-sets/{run.ChangeSetId}");
        Assert.Equal(ChangeSetStatus.Open, changeSet.Status);
        Assert.Equal(run.Branch, changeSet.Branch);
        Assert.Equal("main", changeSet.DefaultBranch);
        var round = Assert.Single(changeSet.Rounds);
        Assert.Equal(1, round.RoundNo);
        Assert.Equal(seeded, round.BaseSha);
        Assert.Equal(round.HeadSha, await TestGit.TryResolveAsync(mirror, "refs/heads/" + run.Branch));
        Assert.Equal(round.HeadSha, await TestGit.TryResolveAsync(mirror, ReviewRules.RoundRef(changeSet.Id, 1)));
        Assert.Equal(["?? notes.txt"], round.Uncommitted);
        Assert.Equal("Changed, removed, renamed and added files.", round.SelfReport.Summary);
        Assert.Equal(["big.txt is large"], round.SelfReport.OpenConcerns);
        Assert.Equal([CriterionCoverage.Covered, CriterionCoverage.Partial], round.Coverage.Select(c => c.Status));
        Assert.Equal(changeSet.Criteria.Select(c => c.Text), round.Coverage.Select(c => c.Text));
        Assert.Equal("", await TestGit.RunAsync(mirror, "for-each-ref", "refs/tikai/incoming/"));

        // The files with their kinds and flags.
        var files = await FilesAsync(hub, changeSet.Id);
        Assert.Equal(1, files.RoundNo);
        Assert.False(files.Truncated);
        var byPath = files.Files.ToDictionary(f => f.Path);
        Assert.Equal(["big.txt", "change.txt", "gone.txt", "image.png", "new-name.txt", "package-lock.json", "src/added.ts"], byPath.Keys.Order());
        Assert.Equal((GitChangeKind.Modified, 1, 0), (byPath["change.txt"].Kind, byPath["change.txt"].Additions, byPath["change.txt"].Deletions));
        Assert.Equal(GitChangeKind.Deleted, byPath["gone.txt"].Kind);
        Assert.Equal((GitChangeKind.Renamed, "old-name.txt"), (byPath["new-name.txt"].Kind, byPath["new-name.txt"].OldPath));
        Assert.Equal((GitChangeKind.Added, "typescript"), (byPath["src/added.ts"].Kind, byPath["src/added.ts"].Language));
        Assert.True(byPath["image.png"].Binary);
        Assert.Null(byPath["image.png"].Additions);
        Assert.True(byPath["package-lock.json"].Lock);
        Assert.True(byPath["big.txt"].Large);
        Assert.All(files.Files, f => Assert.False(f.Viewed));
        Assert.All(files.Files.Where(f => f.Path is not ("package-lock.json" or "big.txt" or "image.png")), f => Assert.False(f.Lock || f.Generated || f.Large || f.Binary));

        // Both sides of a file, as text, binary, too large, or missing.
        var change = await FileAsync(hub, changeSet.Id, "change.txt");
        Assert.Equal(("first line\n", "first line\none more line\n"), (change.Old!.Text, change.New!.Text));
        var image = await FileAsync(hub, changeSet.Id, "image.png");
        Assert.Null(image.Old);
        Assert.Equal((true, (string?)null), (image.New!.Binary, image.New.Text));
        var big = await FileAsync(hub, changeSet.Id, "big.txt");
        Assert.True(big.New!.TooLarge);
        Assert.True(big.New.Size > 2_000_000);
        var gone = await FileAsync(hub, changeSet.Id, "gone.txt");
        Assert.Equal(("bye\n", (FileSide?)null), (gone.Old!.Text, gone.New));
        Assert.Equal("old-name.txt", (await FileAsync(hub, changeSet.Id, "new-name.txt")).OldPath);
        using (var missing = await hub.Browser.GetAsync($"/api/change-sets/{changeSet.Id}/file?path=notes.txt"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        // Viewed: stored per user and file, read back by every later request (a reload), and undone again.
        await ViewedAsync(hub, changeSet.Id, "change.txt", true);
        await ViewedAsync(hub, changeSet.Id, "gone.txt", true);
        await ViewedAsync(hub, changeSet.Id, "change.txt", true); // twice is fine
        Assert.Equal(["change.txt", "gone.txt"], (await FilesAsync(hub, changeSet.Id)).Files.Where(f => f.Viewed).Select(f => f.Path).Order());
        await ViewedAsync(hub, changeSet.Id, "gone.txt", false);
        Assert.Equal(["change.txt"], (await FilesAsync(hub, changeSet.Id)).Files.Where(f => f.Viewed).Select(f => f.Path));
        using (var unknown = await hub.Browser.PutAsync($"/api/change-sets/{changeSet.Id}/viewed", new { path = "notes.txt", viewed = true }))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        // The inbox item opens the review and cannot be dismissed while the work waits for a verdict.
        var item = (await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items")).Single(i => i.RunId == run.Id && i.Kind == AttentionKind.Review);
        Assert.Equal(changeSet.Id, item.ChangeSetId);
        using (var dismiss = await hub.Browser.PostAsync($"/api/attention-items/{item.Id}/dismiss", new { }))
        {
            Assert.Equal(HttpStatusCode.Conflict, dismiss.StatusCode);
        }
        using (var answer = await hub.Browser.PostAsync($"/api/attention-items/{item.Id}/answer", new { text = "ok" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
        }
        Assert.Equal(("Changed, removed, renamed and added files.", 2), (item.Review!.Summary, item.ReviewCoverage.Count));

        // Verdicts: approve and request changes come later; reject ends the run and closes the change.
        foreach (var later in new[] { "approve", "request_changes" })
        {
            using var response = await hub.Browser.PostAsync($"/api/change-sets/{changeSet.Id}/verdict", new { verdict = later });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        using (var reject = await hub.Browser.PostAsync($"/api/change-sets/{changeSet.Id}/verdict", new { verdict = "reject" }))
        {
            Assert.True(reject.StatusCode == HttpStatusCode.OK, await reject.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        var rejected = await hub.WaitForAsync(run.Id, r => r.State == AgentRunState.Cancelled);
        Assert.NotNull(rejected.EndedAt);
        var closed = await hub.Browser.GetJsonAsync<ChangeSetInfo>($"/api/change-sets/{changeSet.Id}");
        Assert.Equal(ChangeSetStatus.Closed, closed.Status);
        Assert.Equal(ReviewVerdict.Reject, closed.Rounds.Single().Verdict);
        Assert.Equal(TicketStatus.Open, (await hub.Browser.GetJsonAsync<TicketInfo>($"/api/projects/{hub.Slugs[ticket.Id]}/tickets/{ticket.Number}")).Status);
        Assert.DoesNotContain(await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"), i => i.Id == item.Id);
        using (var again = await hub.Browser.PostAsync($"/api/change-sets/{changeSet.Id}/verdict", new { verdict = "reject" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        }
        // The work stays in the mirror.
        Assert.Equal(round.HeadSha, await TestGit.TryResolveAsync(mirror, ReviewRules.RoundRef(changeSet.Id, 1)));
    }

    [Fact]
    public async Task Submitting_without_a_commit_is_a_tool_error_the_agent_can_fix()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("no-commit");

        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);

        Assert.Equal(AgentRunState.InReview, run.State);
        Assert.Contains($"There are no commits on {run.Branch} since the run started", (await StubReviewAsync(hub, run.Id)).GetProperty("first").GetString(), StringComparison.Ordinal);
        var changeSet = await hub.Browser.GetJsonAsync<ChangeSetInfo>($"/api/change-sets/{run.ChangeSetId}");
        Assert.Single(changeSet.Rounds);
        var file = Assert.Single((await FilesAsync(hub, changeSet.Id)).Files);
        Assert.Equal(("hello.txt", GitChangeKind.Added), (file.Path, file.Kind));
        Assert.Empty(changeSet.Rounds[0].Uncommitted);
    }

    [Fact]
    public async Task Each_round_is_analysed_after_the_submission_with_the_model_of_its_tier_and_can_be_retried()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("review");
        var mirror = await hub.MirrorAsync(ticket);
        await TestGit.CommitAsync(mirror, "main", "Seed", ("change.txt", "first line\n"), ("gone.txt", "bye\n"), ("old-name.txt", OldName));
        var (first, second) = (ticket.Criteria[0].Id, ticket.Criteria[1].Id);
        var answer = JsonSerializer.Serialize(new
        {
            summary = "Changes text files and adds a script.",
            coverage = new object[]
            {
                new { criterion_id = second, status = "partial", note = "No test.", refs = Array.Empty<object>() },
                new { criterion_id = first, status = "covered", note = "added.ts", refs = new[] { new { path = "src/added.ts", line_start = 1, line_end = 1 } } },
            },
            groups = new object[]
            {
                new { title = "Script", layer = "ui", note = "The new script.", files = new[] { "src/added.ts" } },
                new { title = "Text files", layer = "docs", note = "Edits and a rename.", files = new[] { "change.txt", "old-name.txt", "nowhere.txt" } },
            },
            files = new object[] { new { path = "src/added.ts", risk = "high", reason = "Runs at startup." } },
        });
        agents.Llm.Body = FakeAnthropicServer.TextReply(answer, inputTokens: 4000, outputTokens: 600);
        var requestsBefore = agents.Llm.Requests.Count;
        try
        {
            var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);
            var analysis = await AnalysisAsync(hub, run.ChangeSetId!.Value);

            Assert.Equal(AnalysisStatus.Done, analysis.Status);
            var result = analysis.Result!;
            // .txt files are light, but src/added.ts is not: normal tier, the default model for it.
            Assert.Equal((AnalysisTier.Normal, "claude-sonnet-5-5", "review-analysis.v1"), (result.Tier, result.Model, result.PromptVersion));
            Assert.Equal("Changes text files and adds a script.", result.Summary);
            Assert.Equal([(first, CriterionCoverage.Covered), (second, CriterionCoverage.Partial)], result.Coverage.Select(c => (c.CriterionId, c.Status)));
            Assert.Equal([new CodeRef("src/added.ts", 1, 1)], result.Coverage[0].Refs);
            Assert.Equal(["Script", "Text files", "Other changes"], result.Groups.Select(g => g.Title));
            Assert.Equal(["change.txt", "new-name.txt"], result.Groups[1].Files);
            Assert.Equal(["big.txt", "gone.txt", "image.png"], result.Groups[2].Files);
            Assert.Equal([("src/added.ts", FileRisk.High)], result.Risks.Select(r => (r.Path, r.Risk)));
            Assert.Equal(["big.txt", "image.png"], result.PatchesOmitted);
            Assert.Equal((4000L, 600L), (result.InputTokens, result.OutputTokens));

            // What the model got: the run's provider key, patches of text files, the lock and large files only listed.
            var request = agents.Llm.Requests.Skip(requestsBefore).Single();
            Assert.Equal(("claude-sonnet-5-5", "Bearer " + AgentHub.ApiKey), (request.Model, request.Authorization));
            var message = request.Body.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.Contains("- id: " + first, message, StringComparison.Ordinal);
            Assert.Contains("diff --git a/src/added.ts b/src/added.ts", message, StringComparison.Ordinal);
            Assert.Contains("A package-lock.json (+", message, StringComparison.Ordinal);
            Assert.Contains("[lock file, not shown]", message, StringComparison.Ordinal);
            Assert.Contains("big.txt (+", message, StringComparison.Ordinal);
            Assert.DoesNotContain("diff --git a/big.txt", message, StringComparison.Ordinal);

            // The inbox card's line.
            var item = (await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items")).Single(i => i.RunId == run.Id);
            Assert.Equal(new AnalysisBrief(AnalysisStatus.Done, 1, 1, 0, 1, null), item.Analysis);

            // Another model for the tier, and a provider that refuses the key: the retry fails with the reason.
            using (var settings = await hub.Browser.PutAsync($"/api/projects/{hub.Slugs[ticket.Id]}/settings", new { analysisModels = new { normal = " claude-review-test " } }))
            {
                Assert.True(settings.StatusCode == HttpStatusCode.OK, await settings.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }
            var stored = await hub.Browser.GetJsonAsync<ProjectSettingsInfo>($"/api/projects/{hub.Slugs[ticket.Id]}/settings");
            Assert.Equal(new ReviewModels(Normal: "claude-review-test"), stored.AnalysisModels);
            Assert.Equal("claude-opus-5-5", stored.DefaultAnalysisModels.Strict);
            using (var invalid = await hub.Browser.PutAsync($"/api/projects/{hub.Slugs[ticket.Id]}/settings", new { analysisModels = new { light = "no spaces please" } }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            }
            agents.Llm.Status = StatusCodes.Status401Unauthorized;
            agents.Llm.Body = FakeAnthropicServer.Error("authentication_error", "invalid x-api-key");
            using (var retry = await hub.Browser.PostAsync($"/api/change-sets/{run.ChangeSetId}/rounds/1/analysis", new { }))
            {
                Assert.True(retry.StatusCode == HttpStatusCode.OK, await retry.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }
            var failed = await AnalysisAsync(hub, run.ChangeSetId.Value);
            Assert.Equal(AnalysisStatus.Failed, failed.Status);
            Assert.Null(failed.Result);
            Assert.Equal("The provider rejected the API key. Anthropic says: invalid x-api-key (model claude-review-test)", failed.Error);
            Assert.Equal("claude-review-test", agents.Llm.Requests.Last().Model);
            var unavailable = (await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items")).Single(i => i.RunId == run.Id).Analysis!;
            Assert.Equal((AnalysisStatus.Failed, failed.Error), (unavailable.Status, unavailable.Error));

            // The review itself still works.
            Assert.Equal(7, (await FilesAsync(hub, run.ChangeSetId.Value)).Files.Count);
            using (var missing = await hub.Browser.PostAsync($"/api/change-sets/{run.ChangeSetId}/rounds/9/analysis", new { }))
            {
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
        }
        finally
        {
            agents.Llm.Status = StatusCodes.Status200OK;
            agents.Llm.Body = AgentHost.StubAnalysis;
        }
    }

    /// <summary>The latest round's analysis once it is no longer pending.</summary>
    private static async Task<AnalysisInfo> AnalysisAsync(AgentHub hub, Guid changeSetId)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            var analysis = (await hub.Browser.GetJsonAsync<ChangeSetInfo>($"/api/change-sets/{changeSetId}")).Rounds[^1].Analysis!;
            if (analysis.Status != AnalysisStatus.Pending)
            {
                return analysis;
            }
            await Task.Delay(250, timeout.Token);
        }
    }

    private static Task<ReviewFilesInfo> FilesAsync(AgentHub hub, Guid changeSetId) =>
        hub.Browser.GetJsonAsync<ReviewFilesInfo>($"/api/change-sets/{changeSetId}/files");

    private static Task<FileContentsInfo> FileAsync(AgentHub hub, Guid changeSetId, string path) =>
        hub.Browser.GetJsonAsync<FileContentsInfo>($"/api/change-sets/{changeSetId}/file?path={Uri.EscapeDataString(path)}");

    private static async Task ViewedAsync(AgentHub hub, Guid changeSetId, string path, bool viewed)
    {
        using var response = await hub.Browser.PutAsync($"/api/change-sets/{changeSetId}/viewed", new { path, viewed });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<JsonElement> StubReviewAsync(AgentHub hub, Guid runId)
    {
        var text = (await hub.MessagesAsync(runId)).Single(t => t.StartsWith("STUB_REVIEW ", StringComparison.Ordinal));
        return JsonDocument.Parse(text["STUB_REVIEW ".Length..]).RootElement;
    }
}
