using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tikai.Api.Attention;
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
            ["ask_human", "check_preview_definition", "get_preview_spec", "get_review_comments", "get_ticket", "propose_plan", "reply_to_thread", "report_progress", "submit_for_review"],
            tools.Select(t => t.Name).Order());
        string[] Arguments(string tool) => [.. tools.Single(t => t.Name == tool).JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name)];
        Assert.Equal(["question", "options", "recommended", "context"], Arguments("ask_human"));
        Assert.Equal(["summary", "steps", "risks", "files_expected"], Arguments("propose_plan"));
        Assert.Equal(["summary", "criteria_mapping", "open_concerns"], Arguments("submit_for_review"));
        Assert.Equal(["thread_id", "body"], Arguments("reply_to_thread"));
        Assert.Empty(Arguments("get_ticket"));
        Assert.Empty(Arguments("get_preview_spec"));
        Assert.Equal(["content"], Arguments("check_preview_definition"));
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
    public async Task Submit_for_review_checks_the_self_report_and_takes_work_only_from_a_running_agent()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var other = await McpTestKit.CreateTicketAsync(_browser, "web", "WEB", "Other");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);

        var foreign = await agent.CallAsync("submit_for_review", new
        {
            summary = "Done",
            criteria_mapping = new[] { new { criterion_id = other.Criteria[0].Id, status = "covered" } },
        });
        Assert.Contains(other.Criteria[0].Id.ToString(), foreign.Error(), StringComparison.Ordinal);
        Assert.Contains("summary", (await agent.CallAsync("submit_for_review", new { summary = "" })).Error(), StringComparison.Ordinal);

        // A valid self-report from a run without an agent container (queued here): there is no clone to take work from.
        // Submissions with real containers are in ReviewTests.
        var queued = await agent.CallAsync("submit_for_review", new
        {
            summary = "Added the table and the API.",
            criteria_mapping = new[] { new { criterion_id = ticket.Criteria[0].Id, status = "covered", note = "TableTests" } },
        });
        Assert.Contains("only a running agent can submit", queued.Error(), StringComparison.Ordinal);
        Assert.Empty(await _browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"));
    }

    [Fact]
    public async Task Without_comments_sent_to_the_agent_the_review_tools_have_nothing_for_it()
    {
        // Fix rounds with real threads are in FixRoundTests.
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);

        var comments = System.Text.Json.JsonDocument.Parse((await agent.CallAsync("get_review_comments")).Text()).RootElement;
        Assert.Equal("There are no review comments for you.", comments.GetProperty("note").GetString());
        Assert.Equal(0, comments.GetProperty("threads").GetArrayLength());
        Assert.Contains("get_review_comments", (await agent.CallAsync("reply_to_thread", new { thread_id = "t1", body = "Fixed." })).Error(), StringComparison.Ordinal);
        Assert.Contains("get_review_comments", (await agent.CallAsync("reply_to_thread", new { thread_id = Guid.NewGuid(), body = "Fixed." })).Error(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_preview_spec_returns_the_page_people_read_and_its_examples_pass_the_check()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);

        var spec = (await agent.CallAsync("get_preview_spec")).Text();

        Assert.StartsWith("# tikai-preview.yml", spec, StringComparison.Ordinal);
        // Format, rules and the agent's own part.
        Assert.Contains("| `idle_minutes` | no | 5 to 240, default 30.", spec, StringComparison.Ordinal);
        Assert.Contains("Unknown keys are errors", spec, StringComparison.Ordinal);
        Assert.Contains("check_preview_definition", spec, StringComparison.Ordinal);
        // The page cannot drift from the parser: every example on it is a file the hub accepts.
        var examples = Regex.Matches(spec, "```yaml\n(.*?)```", RegexOptions.Singleline);
        Assert.Equal(3, examples.Count);
        foreach (Match example in examples)
        {
            var check = (await agent.CallAsync("check_preview_definition", new { content = example.Groups[1].Value })).Json();
            Assert.True(check.GetProperty("result").GetString() == "valid", $"{check}\n{example.Groups[1].Value}");
            Assert.Equal(0, check.GetProperty("errors").GetArrayLength());
        }
    }

    [Fact]
    public async Task Check_preview_definition_returns_the_parser_s_errors_with_key_and_line()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);

        var check = (await agent.CallAsync("check_preview_definition", new
        {
            content = "app:\n  image: node:24\n  command: node server.js\n  ports: [8000]\nservices:\n  db:\n    image: postgres:17\n    volumes: [data]\n",
        })).Json();

        // An invalid file is the check's answer, not a failed call.
        Assert.Equal("invalid", check.GetProperty("result").GetString());
        Assert.Equal(
            [("app.ports", 4), ("app.port", 1), ("services.db.volumes", 8)],
            check.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("key").GetString()!, e.GetProperty("line").GetInt32())));
        Assert.Equal("required", check.GetProperty("errors")[1].GetProperty("message").GetString());
        Assert.StartsWith("not allowed", check.GetProperty("errors")[0].GetProperty("message").GetString(), StringComparison.Ordinal);

        // A problem with the whole file has no key.
        var broken = (await agent.CallAsync("check_preview_definition", new { content = "app: { image: a" })).Json();
        Assert.Equal("invalid", broken.GetProperty("result").GetString());
        var error = Assert.Single(broken.GetProperty("errors").EnumerateArray());
        Assert.False(error.TryGetProperty("key", out var key) && key.ValueKind != JsonValueKind.Null);
        Assert.StartsWith($"not valid YAML (line {error.GetProperty("line").GetInt32()},", error.GetProperty("message").GetString(), StringComparison.Ordinal);

        Assert.True((await agent.CallAsync("check_preview_definition")).IsError);
    }

    [Fact]
    public async Task Invalid_blocking_requests_are_tool_errors_and_create_nothing()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);

        Assert.Contains("question: required", (await agent.CallAsync("ask_human", new { question = " ", options = new[] { "yes", "no" }, recommended = "yes" })).Error(), StringComparison.Ordinal);
        Assert.Contains("options: give 2 to 4", (await agent.CallAsync("ask_human", new { question = "Q", options = Enumerable.Range(0, 5).Select(i => $"o{i}"), recommended = "o0" })).Error(), StringComparison.Ordinal);
        Assert.Contains("recommended: must be", (await agent.CallAsync("ask_human", new { question = "Q", options = new[] { "a", "b" }, recommended = "c" })).Error(), StringComparison.Ordinal);
        Assert.Contains("summary: at most 160 characters (yours has 161)", (await agent.CallAsync("propose_plan", new { summary = new string('s', 161), steps = new[] { "One" } })).Error(), StringComparison.Ordinal);
        Assert.Contains("steps: give 1 to 6", (await agent.CallAsync("propose_plan", new { summary = "S", steps = Array.Empty<string>() })).Error(), StringComparison.Ordinal);
        Assert.Empty(await _browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"));
    }
}
