using Tikai.Core.Runs;

namespace Tikai.Core.Tests.Runs;

public class RunExitTests
{
    private static readonly AgentResult _success = new(IsError: false, "success");
    private static readonly ExitFacts _submitted = new(
        CancelRequested: false, HubStopReason: null, OomKilled: false, ExitCode: 0, _success, HasOpenBlockingItem: false, Submitted: true);

    [Fact]
    public void A_clean_exit_after_submitting_goes_to_review()
    {
        Assert.Equal(new ExitDecision(RunTrigger.Submit, null), RunExit.Map(_submitted));
    }

    [Fact]
    public void A_clean_exit_without_submitting_fails()
    {
        var decision = RunExit.Map(_submitted with { Submitted = false });

        Assert.Equal(RunTrigger.Fail, decision.Trigger);
        Assert.Contains("submit_for_review", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancel_wins_over_everything()
    {
        var facts = _submitted with { CancelRequested = true, HubStopReason = "x", OomKilled = true, HasOpenBlockingItem = true, ExitCode = 137 };

        Assert.Equal(RunTrigger.Cancel, RunExit.Map(facts).Trigger);
    }

    [Fact]
    public void A_stop_by_the_hub_fails_with_its_reason_even_after_exit_0()
    {
        var decision = RunExit.Map(_submitted with { HubStopReason = "MCP not connected" });

        Assert.Equal(new ExitDecision(RunTrigger.Fail, "MCP not connected"), decision);
    }

    [Fact]
    public void Out_of_memory_fails()
    {
        var decision = RunExit.Map(_submitted with { OomKilled = true, ExitCode = 137, Result = null });

        Assert.Equal(RunTrigger.Fail, decision.Trigger);
        Assert.Contains("memory", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(143)]
    public void An_open_question_blocks_the_run_whatever_the_exit_code(long exitCode)
    {
        var decision = RunExit.Map(_submitted with { HasOpenBlockingItem = true, Submitted = false, ExitCode = exitCode });

        Assert.Equal(new ExitDecision(RunTrigger.Block, null), decision);
    }

    [Fact]
    public void An_error_result_fails_with_its_subtype()
    {
        var decision = RunExit.Map(_submitted with { Result = new AgentResult(true, "error_max_turns"), ExitCode = 1 });

        Assert.Equal(RunTrigger.Fail, decision.Trigger);
        Assert.Contains("error_max_turns", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("exit code 1", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void No_result_fails_with_the_exit_code(long exitCode)
    {
        var decision = RunExit.Map(_submitted with { Result = null, ExitCode = exitCode });

        Assert.Equal(RunTrigger.Fail, decision.Trigger);
        Assert.Contains($"code {exitCode} without a result", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_success_result_with_a_failing_exit_code_fails()
    {
        Assert.Equal(RunTrigger.Fail, RunExit.Map(_submitted with { ExitCode = 1 }).Trigger);
    }
}
