using Tikai.Core.Runs;

namespace Tikai.Core.Tests.Runs;

public class RunStateMachineTests
{
    // Every allowed transition; everything else must be refused.
    private static readonly Dictionary<(AgentRunState, RunTrigger), AgentRunState> _allowed = new()
    {
        [(AgentRunState.Queued, RunTrigger.Prepare)] = AgentRunState.Preparing,
        [(AgentRunState.Preparing, RunTrigger.Start)] = AgentRunState.Running,
        [(AgentRunState.Running, RunTrigger.Block)] = AgentRunState.Blocked,
        [(AgentRunState.Blocked, RunTrigger.Unblock)] = AgentRunState.Running,
        [(AgentRunState.Running, RunTrigger.Submit)] = AgentRunState.InReview,
        [(AgentRunState.InReview, RunTrigger.RequestChanges)] = AgentRunState.ChangesRequested,
        [(AgentRunState.ChangesRequested, RunTrigger.Resume)] = AgentRunState.Running,
        [(AgentRunState.InReview, RunTrigger.Merge)] = AgentRunState.Merged,
    };

    public static TheoryData<AgentRunState, RunTrigger> AllPairs()
    {
        var data = new TheoryData<AgentRunState, RunTrigger>();
        foreach (var state in Enum.GetValues<AgentRunState>())
        {
            foreach (var trigger in Enum.GetValues<RunTrigger>())
            {
                data.Add(state, trigger);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Every_state_and_trigger_has_the_expected_outcome(AgentRunState state, RunTrigger trigger)
    {
        AgentRunState? expected = state.IsEnded() ? null
            : trigger == RunTrigger.Fail ? AgentRunState.Failed
            : trigger == RunTrigger.Cancel ? AgentRunState.Cancelled
            : _allowed.TryGetValue((state, trigger), out var next) ? next
            : null;

        Assert.Equal(expected, RunStateMachine.Next(state, trigger));
    }

    [Fact]
    public void The_happy_path_goes_from_queued_to_merged_with_a_fix_round_and_a_question()
    {
        RunTrigger[] path = [RunTrigger.Prepare, RunTrigger.Start, RunTrigger.Block, RunTrigger.Unblock, RunTrigger.Submit,
            RunTrigger.RequestChanges, RunTrigger.Resume, RunTrigger.Submit, RunTrigger.Merge];
        var state = AgentRunState.Queued;
        foreach (var trigger in path)
        {
            state = RunStateMachine.Next(state, trigger) ?? throw new InvalidOperationException($"{trigger} refused in {state}");
        }
        Assert.Equal(AgentRunState.Merged, state);
    }

    [Fact]
    public void Ended_states_accept_nothing()
    {
        Assert.All(AgentRunStates.Ended, state =>
            Assert.All(Enum.GetValues<RunTrigger>(), trigger => Assert.Null(RunStateMachine.Next(state, trigger))));
    }

    [Fact]
    public void From_lists_the_states_a_trigger_applies_to()
    {
        Assert.Equal([AgentRunState.Queued], RunStateMachine.From(RunTrigger.Prepare));
        Assert.Equal([AgentRunState.Preparing], RunStateMachine.From(RunTrigger.Start));
        Assert.Equal(
            [AgentRunState.Queued, AgentRunState.Preparing, AgentRunState.Running, AgentRunState.Blocked, AgentRunState.InReview, AgentRunState.ChangesRequested],
            RunStateMachine.From(RunTrigger.Fail));
    }
}
