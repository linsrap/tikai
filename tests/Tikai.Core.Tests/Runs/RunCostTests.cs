using Tikai.Core.Agents;
using Tikai.Core.Runs;

namespace Tikai.Core.Tests.Runs;

// Numbers from the real Claude Code run of step 12: a container stopped while it waited (no result, 108 538 tokens),
// then --resume in a new container, which reported 0.163077 USD (both containers) and 88 597 tokens of its own.
public class RunCostTests
{
    private static AgentRun Run(decimal? usd = null, long? tokens = null, decimal? before = null) =>
        new() { TicketId = Guid.NewGuid(), TokenHash = [], CostUsd = usd, CostTokens = tokens, CostUsdBeforeSession = before };

    [Fact]
    public void A_container_stopped_before_its_report_adds_its_tokens_and_leaves_the_cost()
    {
        Assert.Equal((null, 108_538L), RunCost.After(Run(), new AgentCost(null, 108_538)));
    }

    [Fact]
    public void A_resumed_container_reports_the_whole_sessions_cost_and_adds_its_own_tokens()
    {
        Assert.Equal((0.163077m, 197_135L), RunCost.After(Run(tokens: 108_538), new AgentCost(0.163077m, 88_597)));
        // A later container of the same session reports the larger total; it replaces the earlier one.
        Assert.Equal((0.2m, 197_235L), RunCost.After(Run(0.163077m, 197_135), new AgentCost(0.2m, 100)));
    }

    [Fact]
    public void A_fresh_session_counts_on_top_of_the_earlier_sessions()
    {
        Assert.Equal((0.25m, 1_500L), RunCost.After(Run(0.2m, 1_000, before: 0.2m), new AgentCost(0.05m, 500)));
    }

    [Fact]
    public void Nothing_reported_changes_nothing()
    {
        Assert.Equal((0.1m, 10L), RunCost.After(Run(0.1m, 10), null));
    }
}
