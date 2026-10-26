using Tikai.Core.Attention;

namespace Tikai.Core.Tests.Attention;

public class AttentionRulesTests
{
    [Fact]
    public void A_question_is_trimmed_and_empty_options_are_dropped()
    {
        var (payload, error) = AttentionRules.Question("  Which one? ", "  ", [" a ", "", null, "b"]);

        Assert.Null(error);
        Assert.Equal("Which one?", payload!.Question);
        Assert.Null(payload.Context);
        Assert.Equal(["a", "b"], payload.Options);
    }

    [Fact]
    public void Invalid_questions_are_rejected()
    {
        Assert.NotNull(AttentionRules.Question(" ", null, null).Error);
        Assert.NotNull(AttentionRules.Question(new string('q', AttentionRules.MaxQuestionLength + 1), null, null).Error);
        Assert.NotNull(AttentionRules.Question("Q", new string('c', AttentionRules.MaxContextLength + 1), null).Error);
        Assert.NotNull(AttentionRules.Question("Q", null, Enumerable.Range(0, AttentionRules.MaxOptions + 1).Select(i => $"{i}")).Error);
    }

    [Fact]
    public void A_plan_needs_steps()
    {
        Assert.NotNull(AttentionRules.Plan(null, null, null).Error);
        Assert.NotNull(AttentionRules.Plan([" "], null, null).Error);
        var (plan, error) = AttentionRules.Plan(["one", " two "], null, ["a.cs"]);
        Assert.Null(error);
        Assert.Equal(["one", "two"], plan!.Steps);
        Assert.Empty(plan.Risks);
    }

    [Fact]
    public void A_review_may_only_map_the_tickets_criteria_once_each()
    {
        var mine = Guid.NewGuid();
        var ids = new HashSet<Guid> { mine };

        Assert.Null(AttentionRules.Review("Done", [new(mine, CriterionCoverage.Covered, " ")], null, ids).Error);
        Assert.Null(AttentionRules.Review("Done", [new(mine, CriterionCoverage.Covered, " ")], null, ids).Payload!.CriteriaMapping[0].Note);
        Assert.Contains("not this ticket's", AttentionRules.Review("Done", [new(Guid.NewGuid(), CriterionCoverage.Covered, null)], null, ids).Error, StringComparison.Ordinal);
        Assert.NotNull(AttentionRules.Review("Done", [new(mine, CriterionCoverage.Covered, null), new(mine, CriterionCoverage.Missing, null)], null, ids).Error);
        Assert.NotNull(AttentionRules.Review(" ", null, null, ids).Error);
    }

    [Theory]
    [InlineData(AttentionKind.Question, "yes", null, null)]
    [InlineData(AttentionKind.Question, null, null, "text")]
    [InlineData(AttentionKind.Question, "yes", PlanDecision.Approve, "decision")]
    [InlineData(AttentionKind.PlanApproval, null, PlanDecision.Approve, null)]
    [InlineData(AttentionKind.PlanApproval, "fine", PlanDecision.Approve, null)]
    [InlineData(AttentionKind.PlanApproval, "do X", PlanDecision.Revise, null)]
    [InlineData(AttentionKind.PlanApproval, " ", PlanDecision.Revise, "text")]
    [InlineData(AttentionKind.PlanApproval, "ok", null, "decision")]
    [InlineData(AttentionKind.Review, "ok", null, "kind")]
    public void Answers_fit_the_kind(AttentionKind kind, string? text, PlanDecision? decision, string? errorField)
    {
        var (answer, error) = AttentionRules.Answer(kind, text, decision);

        Assert.Equal(errorField, error?.Field);
        Assert.Equal(errorField is null, answer is not null);
    }

    [Fact]
    public void The_request_hash_tells_requests_apart_but_not_repeats()
    {
        var question = new QuestionPayload("Q", null, ["a"]);

        Assert.Equal(AttentionJson.RequestHash(AttentionKind.Question, question), AttentionJson.RequestHash(AttentionKind.Question, question with { Options = ["a"] }));
        Assert.NotEqual(AttentionJson.RequestHash(AttentionKind.Question, question), AttentionJson.RequestHash(AttentionKind.Question, question with { Options = ["b"] }));
        Assert.NotEqual(AttentionJson.RequestHash(AttentionKind.Question, question), AttentionJson.RequestHash(AttentionKind.Question, question with { Context = "c" }));
        Assert.NotEqual(AttentionJson.RequestHash(AttentionKind.Question, question), AttentionJson.RequestHash(AttentionKind.Permission, question));
        Assert.Equal(64, AttentionJson.RequestHash(AttentionKind.Question, question).Length);
    }

    [Fact]
    public void Payloads_are_stored_in_snake_case()
    {
        var json = AttentionJson.Serialize(new ReviewPayload("S", [new(Guid.Empty, CriterionCoverage.Partial, null)], []));

        Assert.Contains("\"criteria_mapping\"", json, StringComparison.Ordinal);
        Assert.Contains("\"criterion_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"partial\"", json, StringComparison.Ordinal);
        Assert.Equal(new AttentionAnswer("x", PlanDecision.Revise), AttentionJson.Deserialize<AttentionAnswer>(AttentionJson.Serialize(new AttentionAnswer("x", PlanDecision.Revise))));
    }
}
