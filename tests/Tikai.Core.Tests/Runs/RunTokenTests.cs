using Tikai.Core.Runs;

namespace Tikai.Core.Tests.Runs;

public class RunTokenTests
{
    [Fact]
    public void New_tokens_are_well_formed_and_differ()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => RunToken.New()).ToList();

        Assert.All(tokens, t => Assert.True(RunToken.IsWellFormed(t), t));
        Assert.All(tokens, t => Assert.StartsWith("tikai_run_", t, StringComparison.Ordinal));
        Assert.Equal(100, tokens.Distinct().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tikai_run_")]
    [InlineData("tikai_run_abc")]
    [InlineData("other_run_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("tikai_run_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("tikai_run_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Other_values_are_not_tokens(string? value)
    {
        Assert.False(RunToken.IsWellFormed(value));
    }

    [Fact]
    public void The_hash_is_sha256_and_depends_on_the_whole_token()
    {
        var token = RunToken.New();

        Assert.Equal(32, RunToken.Hash(token).Length);
        Assert.Equal(RunToken.Hash(token), RunToken.Hash(token));
        Assert.NotEqual(RunToken.Hash(token), RunToken.Hash(RunToken.New()));
    }

    [Fact]
    public void Ended_states_are_merged_failed_and_cancelled()
    {
        Assert.Equal([AgentRunState.Merged, AgentRunState.Failed, AgentRunState.Cancelled], AgentRunStates.Ended);
        Assert.False(AgentRunState.Blocked.IsEnded());
    }
}
