using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tikai.Api.Attention;
using Tikai.Api.Runs;
using Tikai.Api.Tickets;
using Tikai.Core.Attention;
using Tikai.Core.Runs;
using Tikai.Data;
using Tikai.IntegrationTests.Auth;
using Tikai.IntegrationTests.Mcp;
using Tikai.Mcp;

namespace Tikai.IntegrationTests.Attention;

// Runs fail at once in their start job (the agent image does not exist), which gives failure items without containers.
public sealed class InboxTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TikaiApiFactory _factory = null!;
    private BrowserClient _browser = null!;
    private readonly List<ModelContextProtocol.Client.McpClient> _agents = [];

    public async ValueTask InitializeAsync()
    {
        _factory = await TikaiApiFactory.CreateAsync(postgres, new Dictionary<string, string>
        {
            ["Tikai:Agents:ClaudeCodeImage"] = "tikai/agent-does-not-exist:test",
            ["Tikai:Agents:RunsVolume"] = "tikai-test-unused",
        });
        _browser = new BrowserClient(_factory.CreateClient(new() { HandleCookies = true }));
        await _browser.RefreshAntiforgeryAsync();
        await _browser.SignUpAdminAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var agent in _agents)
        {
            await agent.DisposeAsync();
        }
        _browser.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task A_failure_shows_the_reason_the_agent_and_how_the_run_ended_and_can_be_dismissed()
    {
        var (ticket, run) = await FailedRunAsync("fail", "FA");

        var item = Assert.Single(await OpenItemsAsync());
        Assert.Equal(AttentionKind.Failure, item.Kind);
        Assert.False(item.Blocking);
        Assert.Equal(ticket.Title, item.TicketTitle);
        Assert.Equal("FA-1", item.TicketKey);
        Assert.Equal(run.Id, item.RunId);
        Assert.Equal(AgentRunState.Failed, item.Run!.State);
        Assert.Equal("claude-haiku-5-5", item.Run.Model);
        Assert.Contains("is not on this Docker host", item.Failure!.Reason, StringComparison.Ordinal);
        Assert.Contains(item.Excerpt, e => e.Payload.GetRawText().Contains("is not on this Docker host", StringComparison.Ordinal));
        Assert.True(item.Excerpt.Select(e => e.Seq).SequenceEqual(item.Excerpt.Select(e => e.Seq).Order()), "excerpt oldest first");

        using var dismissed = await _browser.PostAsync($"/api/attention-items/{item.Id}/dismiss");
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
        Assert.Equal(AttentionStatus.Dismissed, (await dismissed.Content.ReadFromJsonAsync<AttentionItemInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!.Status);
        Assert.Empty(await OpenItemsAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await _browser.PostAsync($"/api/attention-items/{item.Id}/dismiss")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _browser.PostAsync($"/api/attention-items/{item.Id}/retry")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _browser.PostAsync($"/api/attention-items/{Guid.NewGuid()}/dismiss")).StatusCode);
    }

    [Fact]
    public async Task Retry_starts_a_new_run_with_the_same_repository_and_provider_and_closes_the_failure()
    {
        var (ticket, failed) = await FailedRunAsync("retry", "RE");
        var failure = Assert.Single(await OpenItemsAsync());

        using var response = await _browser.PostAsync($"/api/attention-items/{failure.Id}/retry");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var retried = (await response.Content.ReadFromJsonAsync<RunInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;

        Assert.NotEqual(failed.Id, retried.Id);
        Assert.Equal(failed.RepositoryId, retried.RepositoryId);
        Assert.Equal(failed.LlmProviderId, retried.LlmProviderId);
        Assert.Equal(ticket.Number, retried.TicketNumber);
        Assert.Equal(AttentionStatus.Dismissed, (await _browser.GetJsonAsync<AttentionItemInfo>($"/api/attention-items/{failure.Id}")).Status);
        // The retried run fails the same way: only its own failure is open now.
        await WaitForStateAsync(retried.Id, AgentRunState.Failed);
        var open = Assert.Single(await McpTestKit.WaitForOpenItemsAsync(_browser));
        Assert.Equal(retried.Id, open.RunId);
    }

    [Fact]
    public async Task Assigning_the_ticket_again_closes_its_open_failures_and_a_busy_ticket_cannot_be_retried()
    {
        var (ticket, _) = await FailedRunAsync("again", "AG");
        var failure = Assert.Single(await OpenItemsAsync());
        await PauseStartJobsAsync();

        using var assigned = await _browser.PostAsync($"/api/projects/again/tickets/{ticket.Number}/runs");
        Assert.Equal(HttpStatusCode.Accepted, assigned.StatusCode);

        Assert.Equal(AttentionStatus.Dismissed, (await _browser.GetJsonAsync<AttentionItemInfo>($"/api/attention-items/{failure.Id}")).Status);
        Assert.Empty(await OpenItemsAsync());
        await ReopenAsync(failure.Id);
        using var busy = await _browser.PostAsync($"/api/attention-items/{failure.Id}/retry");
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Contains("A run is active", await busy.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Items_an_agent_waits_for_come_first_then_the_rest_newest_first()
    {
        var questionTicket = await McpTestKit.CreateTicketAsync(_browser, "ask", "AS", "Ask");
        var (_, askToken) = await McpTestKit.StartRunAsync(_factory, questionTicket.Id);
        var asker = await ConnectAsync(askToken);
        _ = asker.CallAsync("ask_human", new { question = "Which colour?", context = "The *logo*.", options = new[] { "red", "blue" } });
        var question = Assert.Single(await McpTestKit.WaitForOpenItemsAsync(_browser));

        var reviewTicket = await McpTestKit.CreateTicketAsync(_browser, "review", "RV", "Review");
        var (reviewRunId, _) = await McpTestKit.StartRunAsync(_factory, reviewTicket.Id);
        // A review item as runs from before change sets have it (a submission needs a real container, see ReviewTests).
        await RecordReviewAsync(reviewRunId, new ReviewPayload("Done.", [new CriterionMapping(reviewTicket.Criteria[0].Id, CriterionCoverage.Partial, "half")], ["Untested"]));
        await FailedRunAsync("fail", "FA");

        var items = await McpTestKit.WaitForOpenItemsAsync(_browser, 3);

        Assert.Equal([AttentionKind.Question, AttentionKind.Failure, AttentionKind.Review], items.Select(i => i.Kind));
        Assert.Equal([true, false, false], items.Select(i => i.Blocking));
        Assert.Equal(question.Id, items[0].Id);
        Assert.Equal(("Which colour?", "The *logo*."), (items[0].Question!.Question, items[0].Question!.Context));
        Assert.Equal(["red", "blue"], items[0].Question!.Options);
        var review = items[2];
        Assert.Equal("Done.", review.Review!.Summary);
        var coverage = Assert.Single(review.ReviewCoverage);
        Assert.Equal((reviewTicket.Criteria[0].Id, reviewTicket.Criteria[0].Text, CriterionCoverage.Partial, "half"), (coverage.CriterionId, coverage.Text, coverage.Status, coverage.Note));

        // A question is answered or its run cancelled, not dismissed.
        Assert.Equal(HttpStatusCode.Conflict, (await _browser.PostAsync($"/api/attention-items/{question.Id}/dismiss")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _browser.PostAsync($"/api/attention-items/{question.Id}/retry")).StatusCode);
        // A review is given a verdict on the review screen, not answered; one without a change set can be dismissed.
        Assert.Null(review.ChangeSetId);
        Assert.Equal(HttpStatusCode.Conflict, (await _browser.PostAsync($"/api/attention-items/{review.Id}/answer", new { text = "ok" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _browser.PostAsync($"/api/attention-items/{review.Id}/dismiss")).StatusCode);
    }

    private async Task RecordReviewAsync(Guid runId, ReviewPayload payload)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
        var run = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == runId, TestContext.Current.CancellationToken);
        var projectId = await db.Tickets.Where(t => t.Id == run.TicketId).Select(t => t.ProjectId).SingleAsync(TestContext.Current.CancellationToken);
        await scope.ServiceProvider.GetRequiredService<AttentionService>()
            .RecordAsync(new RunScope(runId, run.TicketId, projectId), AttentionKind.Review, payload, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_board_lists_runs_that_have_not_ended_and_those_that_ended_within_the_hour()
    {
        var waiting = await McpTestKit.CreateTicketAsync(_browser, "board", "BO", "Waiting");
        var (waitingRun, _) = await McpTestKit.StartRunAsync(_factory, waiting.Id);
        var (_, recent) = await FailedRunAsync("recent", "RC");
        var (_, old) = await FailedRunAsync("old", "OL");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
            await db.AgentRuns.Where(r => r.Id == old.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.EndedAt, DateTimeOffset.UtcNow.AddHours(-2)), TestContext.Current.CancellationToken);
        }

        var board = await _browser.GetJsonAsync<List<RunInfo>>("/api/runs");

        Assert.Equal([recent.Id, waitingRun], board.Select(r => r.Id));
        Assert.Contains(board, r => r.Id == waitingRun && r.State == AgentRunState.Queued && r.TicketTitle == "Waiting" && r.ProjectSlug == "board");
    }

    private async Task<ModelContextProtocol.Client.McpClient> ConnectAsync(string token)
    {
        var agent = await McpTestKit.ConnectAsync(_factory, token);
        _agents.Add(agent);
        return agent;
    }

    private Task<List<AttentionItemInfo>> OpenItemsAsync() => _browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items");

    /// <summary>A project with a repository and a provider whose ticket was assigned, and whose run has failed.</summary>
    private async Task<(TicketInfo Ticket, RunInfo Run)> FailedRunAsync(string slug, string key)
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, slug, key, $"Ticket {key}");
        (await _browser.PostAsync($"/api/projects/{slug}/llm-providers", new { name = "Team", defaultModel = "claude-haiku-5-5", apiKey = "team-key-0123456789" }))
            .EnsureSuccessStatusCode();
        (await _browser.PostAsync($"/api/projects/{slug}/repositories", new { name = "app" })).EnsureSuccessStatusCode();
        using var response = await _browser.PostAsync($"/api/projects/{slug}/tickets/{ticket.Number}/runs");
        response.EnsureSuccessStatusCode();
        var run = (await response.Content.ReadFromJsonAsync<RunInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
        return (ticket, await WaitForStateAsync(run.Id, AgentRunState.Failed));
    }

    private async Task<RunInfo> WaitForStateAsync(Guid runId, AgentRunState state)
    {
        var cancellationToken = McpTestKit.Timeout();
        while (true)
        {
            var run = await _browser.GetJsonAsync<RunInfo>($"/api/runs/{runId}");
            if (run.State == state)
            {
                return run;
            }
            await Task.Delay(50, cancellationToken);
        }
    }

    /// <summary>Start jobs wait far in the future, so a new run stays queued (active) for the rest of the test.</summary>
    private async Task PauseStartJobsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
        await db.Database.ExecuteSqlAsync($"CREATE OR REPLACE FUNCTION tikai_test_delay() RETURNS trigger AS $$ BEGIN NEW.run_at := now() + interval '1 day'; RETURN NEW; END $$ LANGUAGE plpgsql", TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlAsync($"CREATE TRIGGER tikai_test_delay BEFORE INSERT ON jobs FOR EACH ROW EXECUTE FUNCTION tikai_test_delay()", TestContext.Current.CancellationToken);
    }

    private async Task ReopenAsync(Guid itemId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TikaiDbContext>().AttentionItems.Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, AttentionStatus.Open), TestContext.Current.CancellationToken);
    }
}
