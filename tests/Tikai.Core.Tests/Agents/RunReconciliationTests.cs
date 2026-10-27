using Tikai.Core.Runs;
using Tikai.Orchestrator;

namespace Tikai.Core.Tests.Agents;

public class RunReconciliationTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromHours(4);
    private static readonly Guid _runId = Guid.Parse("019a2b3c-4d5e-7f00-8a1b-2c3d4e5f6071");

    private static ActiveRun Run(AgentRunState state, string? container = null, bool liveJob = false, TimeSpan? waiting = null, bool cancel = false, string? lastError = null) =>
        new(_runId, state, container, liveJob, lastError, cancel, waiting, LogLinesRead: 7, LastEventSeq: 9, Secrets: ["key"]);

    private static RunContainer Container(string id, bool running = true, SandboxRole role = SandboxRole.Agent, Guid? runId = null) =>
        new(id, runId ?? _runId, role, running);

    private static IReadOnlyList<ReconcileAction> Plan(IReadOnlyList<RunContainer> containers, ActiveRun[] runs, params Guid[] watched) =>
        RunReconciliation.Plan(containers, runs, watched.ToHashSet(), _timeout);

    [Theory]
    [InlineData(AgentRunState.Queued)]
    [InlineData(AgentRunState.Preparing)]
    public void A_run_whose_start_job_is_gone_fails_with_the_jobs_last_error(AgentRunState state)
    {
        var action = Assert.Single(Plan([], [Run(state, lastError: "boom")]));

        Assert.Equal(new FailAction(_runId, state, null, "The start job gave up (boom). Start a new run."), action);
    }

    [Theory]
    [InlineData(AgentRunState.Queued)]
    [InlineData(AgentRunState.Preparing)]
    public void A_run_with_a_live_start_job_is_left_to_it(AgentRunState state)
    {
        Assert.Empty(Plan([Container("prep", role: SandboxRole.Prep)], [Run(state, liveJob: true)]));
    }

    [Theory]
    [InlineData(AgentRunState.Running)]
    [InlineData(AgentRunState.Blocked)]
    public void A_container_nobody_follows_is_followed_from_the_stored_log_position(AgentRunState state)
    {
        var action = Assert.IsType<WatchAction>(Assert.Single(Plan([Container("c1")], [Run(state, "c1", waiting: state == AgentRunState.Blocked ? TimeSpan.FromMinutes(1) : null)])));

        Assert.Equal(new WatchedRun(_runId, "c1", 7, 9, ["key"]).ToString(), action.Run.ToString());
    }

    [Fact]
    public void A_run_in_review_whose_container_is_still_finishing_is_followed_and_checked_like_a_running_one()
    {
        Assert.IsType<WatchAction>(Assert.Single(Plan([Container("c1")], [Run(AgentRunState.InReview, "c1")])));
        Assert.Equal([new ContainerGoneAction(_runId, AgentRunState.InReview, "c1")], Plan([], [Run(AgentRunState.InReview, "c1")]));
        Assert.Empty(Plan([], [Run(AgentRunState.InReview)]));
    }

    [Fact]
    public void A_bundle_container_belongs_to_a_submission_in_progress()
    {
        Assert.Empty(Plan([Container("c1"), Container("b1", running: false, role: SandboxRole.Bundle)], [Run(AgentRunState.Running, "c1")], _runId));
        // Once the run has ended nobody uses it any more.
        Assert.Equal([new RemoveAction(_runId, "b1")], Plan([Container("b1", running: false, role: SandboxRole.Bundle)], []));
    }

    [Fact]
    public void A_followed_container_needs_nothing()
    {
        Assert.Empty(Plan([Container("c1")], [Run(AgentRunState.Running, "c1")], _runId));
    }

    [Theory]
    [InlineData(AgentRunState.Running)]
    [InlineData(AgentRunState.Blocked)]
    public void A_container_that_is_not_listed_is_checked_again_before_the_run_is_changed(AgentRunState state)
    {
        Assert.Equal([new ContainerGoneAction(_runId, state, "c1")], Plan([], [Run(state, "c1")]));
    }

    [Fact]
    public void A_waiting_agent_past_the_block_timeout_is_stopped()
    {
        var actions = Plan([Container("c1")], [Run(AgentRunState.Blocked, "c1", waiting: _timeout + TimeSpan.FromSeconds(1))], _runId);

        Assert.Equal([new StopForTimeoutAction(_runId, "c1", _timeout + TimeSpan.FromSeconds(1))], actions);
    }

    [Fact]
    public void A_waiting_agent_within_the_timeout_or_already_stopped_or_being_cancelled_is_not_stopped()
    {
        Assert.Empty(Plan([Container("c1")], [Run(AgentRunState.Blocked, "c1", waiting: _timeout - TimeSpan.FromSeconds(1))], _runId));
        Assert.Empty(Plan([Container("c1", running: false)], [Run(AgentRunState.Blocked, "c1", waiting: _timeout * 2)], _runId));
        Assert.Empty(Plan([Container("c1")], [Run(AgentRunState.Blocked, "c1", waiting: _timeout * 2, cancel: true)], _runId));
    }

    [Fact]
    public void A_blocked_run_without_container_and_with_everything_answered_is_resumed()
    {
        Assert.Equal([new ResumeAction(_runId)], Plan([], [Run(AgentRunState.Blocked)]));
    }

    [Fact]
    public void A_blocked_run_that_still_waits_or_is_being_resumed_or_cancelled_is_left_alone()
    {
        Assert.Empty(Plan([], [Run(AgentRunState.Blocked, waiting: TimeSpan.FromMinutes(5))]));
        Assert.Empty(Plan([], [Run(AgentRunState.Blocked, liveJob: true)]));
        Assert.Empty(Plan([], [Run(AgentRunState.Blocked, cancel: true)]));
    }

    [Fact]
    public void A_running_run_without_container_fails_unless_a_job_is_bringing_one()
    {
        Assert.Equal([new FailAction(_runId, AgentRunState.Running, null, "The hub stopped while resuming this run. Start a new run.")], Plan([], [Run(AgentRunState.Running)]));
        Assert.Empty(Plan([], [Run(AgentRunState.Running, liveJob: true)]));
    }

    [Fact]
    public void Containers_no_active_run_claims_are_removed()
    {
        var other = Guid.Parse("019a2b3c-4d5e-7f00-8a1b-000000000001");

        var actions = Plan(
            [Container("c1"), Container("old"), Container("unknown", runId: other), Container("seed", role: SandboxRole.Seed)],
            [Run(AgentRunState.Running, "c1")],
            _runId);

        Assert.Equal([new RemoveAction(_runId, "old"), new RemoveAction(other, "unknown"), new RemoveAction(_runId, "seed")], actions);
    }

    [Fact]
    public void Containers_of_a_run_whose_job_is_working_on_it_are_kept()
    {
        Assert.Empty(Plan([Container("new"), Container("inspect", role: SandboxRole.Inspect)], [Run(AgentRunState.Blocked, liveJob: true)]));
    }

    [Fact]
    public void Runs_in_review_are_left_alone()
    {
        Assert.Empty(Plan([], [Run(AgentRunState.InReview)]));
        Assert.Empty(Plan([], [Run(AgentRunState.ChangesRequested)]));
    }
}
