using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tikai.Api.Attention;
using Tikai.Core.Attention;
using Tikai.Data;
using Tikai.IntegrationTests.Auth;

namespace Tikai.IntegrationTests.Mcp;

public sealed class McpToolTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TikaiApiFactory _factory = null!;
    private BrowserClient _browser = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await TikaiApiFactory.CreateAsync(postgres);
        _browser = new BrowserClient(_factory.CreateClient(new() { HandleCookies = true }));
        await _browser.RefreshAntiforgeryAsync();
        await _browser.SignUpAdminAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _browser.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task The_agent_sees_the_tools_with_snake_case_arguments()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);

        var tools = await agent.ListToolsAsync(cancellationToken: McpTestKit.Timeout());

        Assert.Equal(
            ["ask_human", "get_review_comments", "get_ticket", "propose_plan", "reply_to_thread", "report_progress", "submit_for_review"],
            tools.Select(t => t.Name).Order());
        string[] Arguments(string tool) => [.. tools.Single(t => t.Name == tool).JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name)];
        Assert.Equal(["question", "context", "options"], Arguments("ask_human"));
        Assert.Equal(["steps", "risks", "files_expected"], Arguments("propose_plan"));
        Assert.Equal(["summary", "criteria_mapping", "open_concerns"], Arguments("submit_for_review"));
        Assert.Equal(["thread_id", "body"], Arguments("reply_to_thread"));
        Assert.Empty(Arguments("get_ticket"));
        var mapping = tools.Single(t => t.Name == "submit_for_review").JsonSchema.GetProperty("properties").GetProperty("criteria_mapping").ToString();
        Assert.Contains("criterion_id", mapping, StringComparison.Ordinal);
        Assert.Contains("\"covered\"", mapping, StringComparison.Ordinal);
        Assert.Contains("hours", tools.Single(t => t.Name == "ask_human").Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ticket_returns_the_spec_and_criteria_with_their_stable_ids()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);
        // An edit keeps the criteria's ids (step 07); the agent sees the current state.
        (await _browser.PutAsync("/api/projects/hub/tickets/1", new
        {
            title = "Ticket, edited",
            description = "New *text*",
            status = "in_progress",
            priority = "high",
            criteria = new object[] { new { id = ticket.Criteria[1].Id, text = "second, now first" }, new { text = "new one" } },
        })).EnsureSuccessStatusCode();

        var result = (await agent.CallAsync("get_ticket")).Json();

        Assert.Equal("HUB-1", result.GetProperty("key").GetString());
        Assert.Equal("Ticket, edited", result.GetProperty("title").GetString());
        Assert.Equal("New *text*", result.GetProperty("description").GetString());
        Assert.Equal("in_progress", result.GetProperty("status").GetString());
        Assert.Equal("high", result.GetProperty("priority").GetString());
        var criteria = result.GetProperty("criteria").EnumerateArray().ToList();
        Assert.Equal(2, criteria.Count);
        Assert.Equal(ticket.Criteria[1].Id, criteria[0].GetProperty("id").GetGuid());
        Assert.Equal("second, now first", criteria[0].GetProperty("text").GetString());
        Assert.Equal("new one", criteria[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Report_progress_updates_the_run()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (runId, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);

        Assert.Equal("Progress recorded.", (await agent.CallAsync("report_progress", new { step = " Writing tests ", note = "3 of 5" })).Text());
        Assert.Contains("step", (await agent.CallAsync("report_progress", new { step = "  " })).Error(), StringComparison.Ordinal);

        await using var scope = _factory.Services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<TikaiDbContext>().AgentRuns.AsNoTracking()
            .SingleAsync(r => r.Id == runId, TestContext.Current.CancellationToken);
        Assert.Equal("Writing tests", run.ProgressStep);
        Assert.Equal("3 of 5", run.ProgressNote);
        Assert.NotNull(run.ProgressAt);
    }

    [Fact]
    public async Task Submit_for_review_records_the_self_report_and_checks_the_criterion_ids()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var other = await McpTestKit.CreateTicketAsync(_browser, "web", "WEB", "Other");
        var (runId, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);

        var foreign = await agent.CallAsync("submit_for_review", new
        {
            summary = "Done",
            criteria_mapping = new[] { new { criterion_id = other.Criteria[0].Id, status = "covered" } },
        });
        Assert.Contains(other.Criteria[0].Id.ToString(), foreign.Error(), StringComparison.Ordinal);
        Assert.Contains("summary", (await agent.CallAsync("submit_for_review", new { summary = "" })).Error(), StringComparison.Ordinal);

        var submitted = await agent.CallAsync("submit_for_review", new
        {
            summary = "Added the table and the API.",
            criteria_mapping = new object[]
            {
                new { criterion_id = ticket.Criteria[0].Id, status = "covered", note = "TableTests" },
                new { criterion_id = ticket.Criteria[1].Id, status = "partial" },
            },
            open_concerns = new[] { "No paging yet" },
        });
        Assert.Equal("Submitted for review.", submitted.Text());

        var review = (await _browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items")).Single();
        Assert.Equal(AttentionKind.Review, review.Kind);
        Assert.Equal(runId, review.RunId);
        Assert.Equal("Added the table and the API.", review.Review!.Summary);
        Assert.Equal(
            [new CriterionMapping(ticket.Criteria[0].Id, CriterionCoverage.Covered, "TableTests"), new CriterionMapping(ticket.Criteria[1].Id, CriterionCoverage.Partial, null)],
            review.Review.CriteriaMapping.ToArray());
        Assert.Equal(["No paging yet"], review.Review.OpenConcerns);
        // A review is opened in the review screen (step 13), not answered.
        using var answer = await _browser.PostAsync($"/api/attention-items/{review.Id}/answer", new { text = "ok" });
        Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
    }

    [Fact]
    public async Task The_review_tools_are_stubs_until_review_threads_exist()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);

        Assert.Equal("There are no review comments for you.", (await agent.CallAsync("get_review_comments")).Text());
        Assert.Contains("get_review_comments", (await agent.CallAsync("reply_to_thread", new { thread_id = "t1", body = "Fixed." })).Error(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_blocking_requests_are_tool_errors_and_create_nothing()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);

        Assert.Contains("question", (await agent.CallAsync("ask_human", new { question = " " })).Error(), StringComparison.Ordinal);
        Assert.Contains("options", (await agent.CallAsync("ask_human", new { question = "Q", options = Enumerable.Range(0, 11).Select(i => $"o{i}") })).Error(), StringComparison.Ordinal);
        Assert.Contains("steps", (await agent.CallAsync("propose_plan", new { steps = Array.Empty<string>() })).Error(), StringComparison.Ordinal);
        Assert.Empty(await _browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"));
    }
}
