using Docker.DotNet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tikai.Api.Attention;
using Tikai.Api.Tickets;
using Tikai.Core.Attention;
using Tikai.Core.Runs;
using Tikai.Core.Tickets;
using Tikai.Data;
using Tikai.Orchestrator;

namespace Tikai.IntegrationTests.Runs;

// Real agent containers (the stub image instead of Claude Code), real Docker, a hub on a real port.
// The tests in this class run one after another (xunit runs a class's tests sequentially).
public sealed class AgentRunTests(PostgresFixture postgres, AgentHost agents) : IClassFixture<AgentHost>
{
    [Fact]
    public async Task Assigning_a_ticket_runs_the_agent_which_reads_the_ticket_reports_progress_commits_and_submits()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("ok");

        var started = await hub.AssignAsync(ticket);
        Assert.Equal(AgentRunState.Queued, started.State);
        Assert.Equal($"tikai/HUB-1-{started.Id.ToString("N")[^8..]}", started.Branch);
        Assert.Equal("claude-haiku-5-5", started.Model);
        Assert.Equal(TicketStatus.InProgress, (await TicketAsync(hub, ticket)).Status);

        var run = await hub.WaitForEndAsync(started.Id);

        Assert.Equal(AgentRunState.InReview, run.State);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(0.0123m, run.CostUsd);
        Assert.Equal(132, run.CostTokens);
        Assert.Equal("Writing hello.txt", run.ProgressStep);
        Assert.NotNull(run.StartedAt);
        Assert.Null(run.EndedAt); // in review is not the end
        Assert.Equal(TicketStatus.InReview, (await TicketAsync(hub, ticket)).Status);

        // The transcript: numbered without gaps, the hub's notes, the agent's start, its tool calls and its result.
        var events = await hub.EventsAsync(run.Id);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Seq));
        var init = events.Single(e => e.Kind == RunEventKind.System);
        Assert.Equal("connected", init.Payload.GetProperty("mcp_servers")[0].GetProperty("status").GetString());
        Assert.Contains(events, e => e.Kind == RunEventKind.ToolCall && e.Payload.GetProperty("name").GetString() == "mcp__tikai__get_ticket");
        Assert.Contains(events, e => e.Kind == RunEventKind.ToolResult && e.Payload.GetProperty("content").GetString()!.Contains("Say hello", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == RunEventKind.Progress && e.Payload.GetProperty("step").GetString() == "Writing hello.txt");
        Assert.Contains(events, e => e.Kind == RunEventKind.Message && e.Payload.TryGetProperty("thinking", out _));
        Assert.Equal("success", events.Single(e => e.Kind == RunEventKind.Result).Payload.GetProperty("subtype").GetString());
        Assert.Contains(events, e => IsHubNote(e, "Run clone prepared"));
        Assert.Contains(events, e => IsHubNote(e, "Agent exited"));

        // From inside the container: identity, clone, hook, the token in mcp.json, and what it may write.
        var facts = await hub.StubFactsAsync(run.Id);
        Assert.Equal(1000, facts.GetProperty("uid").GetInt32());
        Assert.Equal("/run/work", facts.GetProperty("cwd").GetString());
        Assert.Equal(run.Branch, facts.GetProperty("branch").GetString());
        Assert.Equal("", facts.GetProperty("remotes").GetString());
        // The base branch and the run branch, nothing else of the repository.
        Assert.Equal($"refs/heads/main\nrefs/heads/{run.Branch}", facts.GetProperty("branches").GetString());
        Assert.Equal("Claude Code (Tikai) <claude-code@agents.tikai.invalid>", facts.GetProperty("author").GetString());
        Assert.True(facts.GetProperty("hook").GetBoolean());
        Assert.Contains("/.tikai/", facts.GetProperty("excluded").GetString(), StringComparison.Ordinal);
        Assert.Equal("# HUB-1: Say hello", facts.GetProperty("ticket_md").GetString());
        Assert.Equal("", facts.GetProperty("git_status").GetString());
        Assert.True(facts.GetProperty("mcp_header").GetBoolean());
        Assert.Equal(18_000_000, facts.GetProperty("mcp_timeout").GetInt64());
        Assert.False(facts.GetProperty("can_write_ticket").GetBoolean());
        Assert.False(facts.GetProperty("can_delete_ticket").GetBoolean());
        Assert.False(facts.GetProperty("can_write_mcp_config").GetBoolean());
        Assert.False(facts.GetProperty("can_create_in_run_root").GetBoolean());
        Assert.False(facts.GetProperty("can_write_root_fs").GetBoolean());
        Assert.True(facts.GetProperty("can_write_tmp").GetBoolean());
        Assert.True(facts.GetProperty("can_write_home").GetBoolean());
        Assert.False(facts.GetProperty("sees_data").GetBoolean());
        Assert.Equal("/run/claude", facts.GetProperty("claude_config_dir").GetString());
        Assert.Contains("ANTHROPIC_API_KEY", facts.GetProperty("env_names").EnumerateArray().Select(e => e.GetString()));
        var argv = facts.GetProperty("argv").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["-p", "<prompt>", "--mcp-config", "/run/mcp.json", "--strict-mcp-config"], argv.Take(5));
        Assert.StartsWith("You are working on the Tikai ticket HUB-1: Say hello", facts.GetProperty("prompt_start").GetString(), StringComparison.Ordinal);
        var commit = facts.GetProperty("last_commit").GetString()!;
        Assert.StartsWith("Claude Code (Tikai) <claude-code@agents.tikai.invalid>", commit, StringComparison.Ordinal);
        Assert.Contains("Ticket: HUB-1", commit, StringComparison.Ordinal);
        Assert.Equal("hello.txt", facts.GetProperty("files_in_commit").GetString());

        // Stored: the session for a later resume, the review the agent submitted; the container is gone.
        await using var scope = hub.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
        var row = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(row.SessionId);
        Assert.Null(row.ContainerId);
        Assert.True(row.LogLinesRead > 0);
        Assert.Single(await db.AttentionItems.Where(i => i.RunId == run.Id && i.Kind == AttentionKind.Review).ToListAsync(TestContext.Current.CancellationToken));
        // The monitor records the end first and removes the container right after.
        using var removal = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        removal.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                await agents.Docker.Containers.InspectContainerAsync(DockerContainerRuntime.Name(SandboxRole.Agent, run.Id), removal.Token);
                await Task.Delay(200, removal.Token);
            }
            catch (DockerContainerNotFoundException)
            {
                break;
            }
        }
    }

    [Fact]
    public async Task Without_the_mcp_server_the_hub_stops_the_agent_at_once_and_the_run_fails()
    {
        // A free port nothing listens on.
        await using var hub = await agents.StartHubAsync(postgres, new Dictionary<string, string> { ["Tikai:Agents:McpUrl"] = AgentHost.McpUrl(AgentHost.FreePort()) });
        var ticket = await hub.CreateTicketAsync("ok");

        var started = DateTimeOffset.UtcNow;
        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);

        Assert.Equal(AgentRunState.Failed, run.State);
        Assert.Contains("could not connect to the Tikai MCP server", run.FailureReason, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(28), "The stub waits 30 s; the hub must stop it before.");
        Assert.NotNull(run.EndedAt);
        Assert.Equal(TicketStatus.Open, (await TicketAsync(hub, ticket)).Status);
        var failure = (await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items")).Single(i => i.Kind == AttentionKind.Failure);
        Assert.Equal(run.Id, failure.RunId);
    }

    [Fact]
    public async Task An_agent_that_ends_without_submitting_fails()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("no-submit");

        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);

        Assert.Equal(AgentRunState.Failed, run.State);
        Assert.Contains("submit_for_review", run.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public async Task An_error_result_fails_the_run_with_its_subtype()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("error");

        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);

        Assert.Equal(AgentRunState.Failed, run.State);
        Assert.Contains("error_during_execution", run.FailureReason, StringComparison.Ordinal);
        Assert.Equal(1, run.ExitCode);
    }

    [Fact]
    public async Task The_api_key_and_the_run_token_never_reach_the_events()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("leak");

        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);

        Assert.Equal(AgentRunState.InReview, run.State);
        var all = string.Join("\n", (await hub.EventsAsync(run.Id)).Select(e => e.Payload.GetRawText()));
        Assert.DoesNotContain(AgentHub.ApiKey, all, StringComparison.Ordinal);
        Assert.DoesNotContain("tikai_run_", all, StringComparison.Ordinal);
        Assert.Contains("key=[redacted] token=[redacted]", all, StringComparison.Ordinal);
        Assert.Contains("stderr key=[redacted]", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Big_output_is_cut_and_a_line_over_the_limit_is_skipped()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("big");

        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);

        var events = await hub.EventsAsync(run.Id);
        Assert.True(run.State == AgentRunState.InReview,
            $"{run.State}: {run.FailureReason}\n" + string.Join("\n", events.TakeLast(12).Select(e => $"{e.Seq} {e.Kind} {e.Payload.GetRawText()[..Math.Min(300, e.Payload.GetRawText().Length)]}")));
        var big =events.Single(e => e.Kind == RunEventKind.ToolResult && e.Payload.GetProperty("tool_use_id").GetString() == "toolu_big");
        Assert.EndsWith("… [96000 more characters]", big.Payload.GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains(events, e => e.Kind == RunEventKind.Log && e.Payload.GetProperty("text").GetString() == "A line of 1200000 bytes was too long to store.");
    }

    [Fact]
    public async Task Cancelling_stops_the_agent_and_cancels_the_run()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("hang");
        var started = await hub.AssignAsync(ticket);
        await hub.WaitForAsync(started.Id, r => r.State == AgentRunState.Running);
        await WaitForEventAsync(hub, started.Id, e => e.Kind == RunEventKind.ToolCall);

        using var response = await hub.Browser.PostAsync($"/api/runs/{started.Id}/cancel");
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        var run = await hub.WaitForEndAsync(started.Id, seconds: 60);

        Assert.Equal(AgentRunState.Cancelled, run.State);
        Assert.True(run.CancelRequested);
        Assert.Null(run.FailureReason);
        Assert.Equal(TicketStatus.Open, (await TicketAsync(hub, ticket)).Status);
        Assert.DoesNotContain(await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"), i => i.Kind == AttentionKind.Failure);
        using var again = await hub.Browser.PostAsync($"/api/runs/{started.Id}/cancel");
        Assert.Equal(System.Net.HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task An_agent_that_gives_up_waiting_for_an_answer_leaves_the_run_blocked()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("ask");

        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);

        Assert.Equal(AgentRunState.Blocked, run.State);
        Assert.Null(run.EndedAt);
        Assert.Equal(TicketStatus.InProgress, (await TicketAsync(hub, ticket)).Status);
        var question = (await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items")).Single(i => i.Kind == AttentionKind.Question);
        Assert.Equal(run.Id, question.RunId);
    }

    [Fact]
    public async Task After_a_hub_restart_the_agent_is_followed_again_without_duplicate_events()
    {
        var hub = await agents.StartHubAsync(postgres);
        try
        {
            var ticket = await hub.CreateTicketAsync("slow:6");
            var started = await hub.AssignAsync(ticket);
            await hub.WaitForAsync(started.Id, r => r.State == AgentRunState.Running);
            var before = await WaitForEventAsync(hub, started.Id, e => e.Kind == RunEventKind.Message && e.Payload.TryGetProperty("thinking", out _));

            hub = await hub.RestartAsync(); // the container keeps running meanwhile
            var run = await hub.WaitForEndAsync(started.Id);

            Assert.Equal(AgentRunState.InReview, run.State);
            var events = await hub.EventsAsync(run.Id);
            Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Seq));
            Assert.Single(events, e => e.Kind == RunEventKind.System);
            Assert.Single(events, e => e.Kind == RunEventKind.Result);
            Assert.Single(events, e => e.Kind == RunEventKind.ToolCall && e.Payload.GetProperty("name").GetString() == "mcp__tikai__get_ticket");
            Assert.True(events.Count > before.Count);
        }
        finally
        {
            await hub.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_missing_agent_image_fails_the_run_with_how_to_build_it()
    {
        await using var hub = await agents.StartHubAsync(postgres, new Dictionary<string, string> { ["Tikai:Agents:ClaudeCodeImage"] = "tikai/agent-does-not-exist:test" });
        var ticket = await hub.CreateTicketAsync("ok");

        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);

        Assert.Equal(AgentRunState.Failed, run.State);
        Assert.Contains("tikai/agent-does-not-exist:test is not on this Docker host", run.FailureReason, StringComparison.Ordinal);
    }

    private static bool IsHubNote(Api.Runs.RunEventInfo e, string text) =>
        e.Kind == RunEventKind.Log && e.Payload.TryGetProperty("source", out var source) && source.GetString() == "hub"
        && e.Payload.GetProperty("text").GetString() == text;

    private static Task<TicketInfo> TicketAsync(AgentHub hub, TicketInfo ticket) =>
        hub.Browser.GetJsonAsync<TicketInfo>($"/api/projects/{hub.Slugs[ticket.Id]}/tickets/{ticket.Number}");

    private static async Task<List<Api.Runs.RunEventInfo>> WaitForEventAsync(AgentHub hub, Guid runId, Func<Api.Runs.RunEventInfo, bool> condition)
    {
        for (var i = 0; i < 240; i++)
        {
            var events = await hub.EventsAsync(runId);
            if (events.Any(condition))
            {
                return events;
            }
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("The expected event did not come.");
    }
}
