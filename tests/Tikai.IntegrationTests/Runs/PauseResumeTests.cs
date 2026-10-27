using System.Net;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Tikai.Api.Attention;
using Tikai.Api.Runs;
using Tikai.Core.Attention;
using Tikai.Core.Runs;
using Tikai.Data;
using Tikai.Orchestrator;

namespace Tikai.IntegrationTests.Runs;

// A blocked agent (the stub's "wait" scenarios), answered while it waits, after a hub restart, and after the block
// timeout stopped it; resume with its session and the summary fallback; reconciliation. Real containers, as in
// AgentRunTests. Every agent test ends with the agent continuing and its commit from before the question intact.
public sealed class PauseResumeTests(PostgresFixture postgres, AgentHost agents) : IClassFixture<AgentHost>
{
    /// <summary>The hub stops a waiting agent after 3 s; Claude Code's own limit stays far above it.</summary>
    private static readonly Dictionary<string, string> _shortBlockTimeout = new()
    {
        ["Tikai:Agents:BlockTimeout"] = "00:00:03",
        ["Tikai:Agents:ToolCallTimeout"] = "00:20:00",
    };

    [Fact]
    public async Task An_answer_while_the_call_is_open_unblocks_the_run_and_the_agent_continues()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("wait");
        var started = await hub.AssignAsync(ticket);

        var blocked = await WaitForQuestionAsync(hub, started.Id);
        Assert.False(blocked.AgentStopped);
        Assert.Equal(blocked.Waiting[0].CreatedAt, blocked.BlockedSince);
        Assert.Equal(blocked.BlockedSince + TimeSpan.FromHours(4), blocked.BlockTimeoutAt);
        Assert.Equal("Which word?", blocked.Waiting[0].Question!.Question);

        await hub.AnswerAsync(blocked.Waiting[0].Id, "falcon");
        var run = await hub.WaitForAsync(started.Id, r => r.State == AgentRunState.InReview);

        Assert.Equal(1, run.Attempt);
        Assert.Equal(RunAttemptKind.Start, run.AttemptKind);
        Assert.Empty(run.Waiting);
        var facts = await hub.StubFactsAsync(run.Id);
        Assert.Equal("start", facts.GetProperty("mode").GetString());
        Assert.Equal(1, facts.GetProperty("asked").GetInt32());
        Assert.Equal("falcon", facts.GetProperty("answer").GetString());
        await AssertEarlierCommitIntactAsync(hub, run.Id, facts);
        Assert.NotNull((await ItemAsync(hub, blocked.Waiting[0].Id)).DeliveredAt);
    }

    [Fact]
    public async Task After_a_hub_restart_the_agent_asks_again_and_gets_the_answer_through_the_new_hub()
    {
        var hub = await agents.StartHubAsync(postgres);
        try
        {
            var ticket = await hub.CreateTicketAsync("wait");
            var started = await hub.AssignAsync(ticket);
            var blocked = await WaitForQuestionAsync(hub, started.Id);

            hub = await hub.RestartAsync(); // the call drops; the stub asks again, and the new hub merges it
            await WaitForEventAsync(hub, started.Id, events => events.Count(IsAskHuman) >= 2);
            Assert.Equal(blocked.Waiting[0].Id, Assert.Single((await hub.GetRunAsync(started.Id)).Waiting).Id);
            await hub.AnswerAsync(blocked.Waiting[0].Id, "heron");
            var run = await hub.WaitForAsync(started.Id, r => r.State == AgentRunState.InReview);

            Assert.Equal(1, run.Attempt);
            var facts = await hub.StubFactsAsync(run.Id);
            Assert.Equal("start", facts.GetProperty("mode").GetString());
            Assert.True(facts.GetProperty("asked").GetInt32() >= 2);
            Assert.Equal("heron", facts.GetProperty("answer").GetString());
            Assert.Single(await QuestionsAsync(hub, run.Id));
            await AssertEarlierCommitIntactAsync(hub, run.Id, facts);
        }
        finally
        {
            await hub.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_agent_that_gave_up_while_the_hub_was_down_is_resumed_at_startup_with_the_answer_given_meanwhile()
    {
        var hub = await agents.StartHubAsync(postgres);
        var port = hub.Port;
        try
        {
            var ticket = await hub.CreateTicketAsync("wait");
            var started = await hub.AssignAsync(ticket);
            var blocked = await WaitForQuestionAsync(hub, started.Id);
            var session = (await RowAsync(hub, started.Id)).SessionId;

            // The hub stays down until the stub has given up (it asks 10 times, 1 s apart) and exited.
            var next = await hub.StopAsync();
            await WaitUntilAsync(async () => await agents.Docker.Containers.InspectContainerAsync(
                DockerContainerRuntime.Name(SandboxRole.Agent, started.Id), TestContext.Current.CancellationToken) is { State.Running: false }, seconds: 60);
            // Answered while no hub runs (in the inbox of a hub that is restarting, say).
            await using (var connection = new NpgsqlConnection(next.ConnectionString))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var update = new NpgsqlCommand(
                    "UPDATE attention_items SET status = 'answered', answer = @answer::jsonb, answered_at = now() WHERE id = @id", connection);
                update.Parameters.AddWithValue("answer", AttentionJson.Serialize(new AttentionAnswer("otter", null)));
                update.Parameters.AddWithValue("id", blocked.Waiting[0].Id);
                Assert.Equal(1, await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            }

            hub = await AgentHub.StartAgainAsync(next, port);
            var run = await hub.WaitForAsync(started.Id, r => r.State == AgentRunState.InReview, seconds: 180);

            Assert.Equal(2, run.Attempt);
            Assert.Equal(RunAttemptKind.Resume, run.AttemptKind);
            var facts = await hub.StubFactsAsync(run.Id);
            Assert.Equal("resume", facts.GetProperty("mode").GetString());
            Assert.Equal(session, facts.GetProperty("session_id").GetString());
            Assert.Equal("otter", facts.GetProperty("answer").GetString());
            Assert.Equal(0, facts.GetProperty("asked").GetInt32());
            Assert.Contains("asking", facts.GetProperty("earlier_steps").EnumerateArray().Select(e => e.GetString()));
            await AssertEarlierCommitIntactAsync(hub, run.Id, facts);
        }
        finally
        {
            await hub.DisposeAsync();
        }
    }

    [Fact]
    public async Task After_the_block_timeout_the_container_is_stopped_and_the_answer_resumes_the_session_in_a_new_one()
    {
        await using var hub = await agents.StartHubAsync(postgres, _shortBlockTimeout);
        var ticket = await hub.CreateTicketAsync("wait");
        var started = await hub.AssignAsync(ticket);
        var blocked = await WaitForQuestionAsync(hub, started.Id);
        var before = await RowAsync(hub, started.Id);

        var stopped = await hub.WaitForAsync(started.Id, r => r.AgentStopped, seconds: 60);
        Assert.Equal(AgentRunState.Blocked, stopped.State);
        Assert.Null(stopped.BlockTimeoutAt);
        Assert.Null(stopped.EndedAt);
        Assert.Contains(await hub.EventsAsync(started.Id), e => IsHubNote(e, "Stopped the agent after the block timeout"));
        Assert.DoesNotContain(await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"), i => i.Kind == AttentionKind.Failure);

        await hub.AnswerAsync(blocked.Waiting[0].Id, "lynx");
        var run = await hub.WaitForAsync(started.Id, r => r.State == AgentRunState.InReview);

        Assert.Equal(2, run.Attempt);
        Assert.Equal(RunAttemptKind.Resume, run.AttemptKind);
        var after = await RowAsync(hub, run.Id);
        Assert.NotEqual(before.TokenHash, after.TokenHash); // the new container got a new token in a new mcp.json
        Assert.Equal(before.SessionId, after.SessionId);
        var facts = await hub.StubFactsAsync(run.Id);
        Assert.Equal("resume", facts.GetProperty("mode").GetString());
        Assert.Equal(before.SessionId, facts.GetProperty("session_id").GetString());
        Assert.Equal("lynx", facts.GetProperty("answer").GetString());
        Assert.Equal(0, facts.GetProperty("asked").GetInt32());
        Assert.True(facts.GetProperty("mcp_header").GetBoolean());
        await AssertEarlierCommitIntactAsync(hub, run.Id, facts);
        var events = await hub.EventsAsync(run.Id);
        var clone = events.Single(e => IsHubNote(e, "Run clone before resuming"));
        Assert.Contains("Add before.txt", clone.Payload.GetProperty("git_log").GetString(), StringComparison.Ordinal);
        Assert.True(clone.Payload.GetProperty("session_found").GetBoolean());
        Assert.Contains(events, e => IsHubNote(e, "Agent resumed its session"));
        Assert.NotNull((await ItemAsync(hub, blocked.Waiting[0].Id)).DeliveredAt);
    }

    [Fact]
    public async Task A_resume_that_fails_is_retried_once_with_a_fresh_session_and_a_summary()
    {
        await using var hub = await agents.StartHubAsync(postgres, _shortBlockTimeout);
        var ticket = await hub.CreateTicketAsync("wait-badresume");
        var started = await hub.AssignAsync(ticket);
        var blocked = await WaitForQuestionAsync(hub, started.Id);
        var session = (await RowAsync(hub, started.Id)).SessionId;
        await hub.WaitForAsync(started.Id, r => r.AgentStopped, seconds: 60);

        await hub.AnswerAsync(blocked.Waiting[0].Id, "badger");
        var run = await hub.WaitForAsync(started.Id, r => r.State == AgentRunState.InReview);

        Assert.Equal(3, run.Attempt); // start, the failed resume, the summary
        Assert.Equal(RunAttemptKind.Summary, run.AttemptKind);
        Assert.Null(run.FailureReason);
        var events = await hub.EventsAsync(run.Id);
        var retry = events.Single(e => IsHubNote(e, "Resume failed; starting a fresh session with a summary"));
        Assert.Contains("error_during_execution", retry.Payload.GetProperty("reason").GetString(), StringComparison.Ordinal);
        var facts = await hub.StubFactsAsync(run.Id);
        Assert.Equal("summary", facts.GetProperty("mode").GetString());
        Assert.NotEqual(session, facts.GetProperty("session_id").GetString());
        Assert.Equal(facts.GetProperty("session_id").GetString(), (await RowAsync(hub, run.Id)).SessionId);
        Assert.Equal("badger", facts.GetProperty("answer").GetString());
        Assert.True(facts.GetProperty("summary_has_question").GetBoolean());
        Assert.Contains("Add before.txt", facts.GetProperty("summary_git_log").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, facts.GetProperty("asked").GetInt32());
        await AssertEarlierCommitIntactAsync(hub, run.Id, facts);
        Assert.DoesNotContain(await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"), i => i.Kind == AttentionKind.Failure);
    }

    [Fact]
    public async Task Without_its_session_file_the_run_continues_in_a_fresh_session_with_a_summary_right_away()
    {
        await using var hub = await agents.StartHubAsync(postgres, _shortBlockTimeout);
        var ticket = await hub.CreateTicketAsync("wait-nosession");
        var started = await hub.AssignAsync(ticket);
        var blocked = await WaitForQuestionAsync(hub, started.Id);
        await hub.WaitForAsync(started.Id, r => r.AgentStopped, seconds: 60);

        await hub.AnswerAsync(blocked.Waiting[0].Id, "marten");
        var run = await hub.WaitForAsync(started.Id, r => r.State == AgentRunState.InReview);

        Assert.Equal(2, run.Attempt);
        Assert.Equal(RunAttemptKind.Summary, run.AttemptKind);
        var events = await hub.EventsAsync(run.Id);
        Assert.False(events.Single(e => IsHubNote(e, "Run clone before resuming")).Payload.GetProperty("session_found").GetBoolean());
        Assert.Equal("the session file is gone",
            events.Single(e => IsHubNote(e, "Agent started a fresh session with a summary")).Payload.GetProperty("reason").GetString());
        var facts = await hub.StubFactsAsync(run.Id);
        Assert.Equal("summary", facts.GetProperty("mode").GetString());
        Assert.Equal("marten", facts.GetProperty("answer").GetString());
        await AssertEarlierCommitIntactAsync(hub, run.Id, facts);
    }

    [Fact]
    public async Task Cancelling_a_waiting_run_dismisses_its_question_and_an_answer_is_refused()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("wait");
        var started = await hub.AssignAsync(ticket);
        var blocked = await WaitForQuestionAsync(hub, started.Id);

        using (var cancel = await hub.Browser.PostAsync($"/api/runs/{started.Id}/cancel"))
        {
            Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        }
        var run = await hub.WaitForAsync(started.Id, r => r.State.IsEnded(), seconds: 60);

        Assert.Equal(AgentRunState.Cancelled, run.State);
        Assert.Equal(AttentionStatus.Dismissed, (await ItemAsync(hub, blocked.Waiting[0].Id)).Status);
        Assert.DoesNotContain(await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"), i => i.Id == blocked.Waiting[0].Id);
        using var answer = await hub.Browser.PostAsync($"/api/attention-items/{blocked.Waiting[0].Id}/answer", new { text = "too late" });
        Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
        Assert.Contains("The run has ended", await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reconciler_fails_runs_whose_start_job_is_gone_and_removes_containers_no_run_claims()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var queued = await hub.CreateTicketAsync("ok", "QU");
        var preparing = await hub.CreateTicketAsync("ok", "PR");
        Guid queuedRun, preparingRun;
        await using (var scope = hub.Factory.Services.CreateAsyncScope())
        {
            var runs = scope.ServiceProvider.GetRequiredService<RunService>();
            var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
            queuedRun = (await runs.CreateAsync(queued.Id, TestContext.Current.CancellationToken)).Run.Id; // a queued run without a job
            preparingRun = (await runs.CreateAsync(preparing.Id, TestContext.Current.CancellationToken)).Run.Id;
            await db.AgentRuns.Where(r => r.Id == preparingRun)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, AgentRunState.Preparing), TestContext.Current.CancellationToken);
            // Its start job died after its last attempt.
            var payload = new RunJob(preparingRun).ToJson();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO jobs (id, queue, payload, run_at, attempts, max_attempts, last_error, dead_at)
                VALUES ({Guid.CreateVersion7()}, 'runs', {payload}::jsonb, now(), 3, 3, 'boom', now())
                """, TestContext.Current.CancellationToken);
        }
        // A container of this installation whose run does not exist (left over from an old database, say).
        var orphan = await agents.Docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = AgentHost.StubImage,
            Entrypoint = ["sleep", "300"],
            Labels = new Dictionary<string, string> { [DockerContainerRuntime.RunIdLabel] = Guid.NewGuid().ToString("D"), [DockerContainerRuntime.RoleLabel] = "agent" },
            HostConfig = new HostConfig { Mounts = [new Mount { Type = "volume", Source = agents.RunsVolume, Target = "/runs" }], NetworkMode = "none" },
        }, TestContext.Current.CancellationToken);
        await agents.Docker.Containers.StartContainerAsync(orphan.ID, new ContainerStartParameters(), TestContext.Current.CancellationToken);

        var failedQueued = await hub.WaitForAsync(queuedRun, r => r.State == AgentRunState.Failed, seconds: 30);
        var failedPreparing = await hub.WaitForAsync(preparingRun, r => r.State == AgentRunState.Failed, seconds: 30);
        await WaitUntilAsync(async () =>
        {
            try
            {
                await agents.Docker.Containers.InspectContainerAsync(orphan.ID, TestContext.Current.CancellationToken);
                return false;
            }
            catch (DockerContainerNotFoundException)
            {
                return true;
            }
        }, seconds: 30);

        Assert.Equal("The start job gave up. Start a new run.", failedQueued.FailureReason);
        Assert.Equal("The start job gave up (boom). Start a new run.", failedPreparing.FailureReason);
    }

    private static async Task<RunInfo> WaitForQuestionAsync(AgentHub hub, Guid runId) =>
        await hub.WaitForAsync(runId, r => r is { State: AgentRunState.Blocked, Waiting.Count: 1 });

    /// <summary>The run branch has the commit the first container made before it asked, unchanged, and the one after.</summary>
    private static async Task AssertEarlierCommitIntactAsync(AgentHub hub, Guid runId, JsonElement facts)
    {
        var before = (await hub.MessagesAsync(runId)).First(m => m.StartsWith("STUB_COMMIT ", StringComparison.Ordinal))["STUB_COMMIT ".Length..];
        Assert.Equal(before, facts.GetProperty("before_sha").GetString());
        var log = facts.GetProperty("git_log").GetString()!.Split('\n');
        Assert.Equal(2, log.Length);
        Assert.EndsWith(" Add answer.txt", log[0], StringComparison.Ordinal);
        Assert.Equal($"{before} Add before.txt", log[1]);
        Assert.Equal("", facts.GetProperty("git_status").GetString());
    }

    private static bool IsAskHuman(RunEventInfo e) =>
        e.Kind == RunEventKind.ToolCall && e.Payload.GetProperty("name").GetString() == "mcp__tikai__ask_human";

    private static bool IsHubNote(RunEventInfo e, string text) =>
        e.Kind == RunEventKind.Log && e.Payload.TryGetProperty("source", out var source) && source.GetString() == "hub"
        && e.Payload.GetProperty("text").GetString() == text;

    private static async Task<AgentRun> RowAsync(AgentHub hub, Guid runId)
    {
        await using var scope = hub.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TikaiDbContext>().AgentRuns.AsNoTracking()
            .SingleAsync(r => r.Id == runId, TestContext.Current.CancellationToken);
    }

    private static async Task<AttentionItem> ItemAsync(AgentHub hub, Guid itemId)
    {
        await using var scope = hub.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TikaiDbContext>().AttentionItems.AsNoTracking()
            .SingleAsync(i => i.Id == itemId, TestContext.Current.CancellationToken);
    }

    private static async Task<List<AttentionItem>> QuestionsAsync(AgentHub hub, Guid runId)
    {
        await using var scope = hub.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TikaiDbContext>().AttentionItems.AsNoTracking()
            .Where(i => i.RunId == runId && i.Kind == AttentionKind.Question)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task WaitForEventAsync(AgentHub hub, Guid runId, Func<List<RunEventInfo>, bool> condition) =>
        await WaitUntilAsync(async () => condition(await hub.EventsAsync(runId)), seconds: 60);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int seconds)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > until)
            {
                throw new TimeoutException($"The condition did not hold within {seconds} s.");
            }
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }
}
