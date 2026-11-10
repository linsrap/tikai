using Tikai.Core.Attention;

namespace Tikai.Core.Tests.Attention;

public class AttentionRulesTests
{
    [Fact]
    public void A_question_is_trimmed_and_empty_options_are_dropped()
    {
        var (payload, error) = AttentionRules.Question("  Which one? ", "  ", [" a ", "", null, "b"], " b ");

        Assert.Null(error);
        Assert.Equal("Which one?", payload!.Question);
        Assert.Null(payload.Context);
        Assert.Equal(["a", "b"], payload.Options);
        Assert.Equal("b", payload.Recommended);
    }

    [Theory]
    [InlineData(" ", null, new[] { "a", "b" }, "a", "question: required")]
    [InlineData("Q", null, new[] { "a" }, "a", "options: give 2 to 4 possible answers (you gave 1)")]
    [InlineData("Q", null, new[] { "a", "b", "c", "d", "e" }, "a", "options: give 2 to 4 possible answers (you gave 5)")]
    [InlineData("Q", null, new[] { "a", "a" }, "a", "options: each option must be different")]
    [InlineData("Q", null, new[] { "a", "b" }, null, "recommended: required")]
    [InlineData("Q", null, new[] { "a", "b" }, "c", "recommended: must be exactly one of the options")]
    public void Questions_need_options_and_a_recommendation(string question, string? context, string[] options, string? recommended, string error)
    {
        Assert.StartsWith(error, AttentionRules.Question(question, context, options, recommended).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_question_over_a_limit_says_which_and_how_long_it_is()
    {
        string[] ab = ["a", "b"];
        Assert.StartsWith("question: at most 200 characters (yours has 201)",
            AttentionRules.Question(new string('q', 201), null, ab, "a").Error, StringComparison.Ordinal);
        Assert.StartsWith("context: at most 2000 characters (yours has 2001)",
            AttentionRules.Question("Q", new string('c', 2001), ab, "a").Error, StringComparison.Ordinal);
        Assert.Equal("options: each entry at most 80 characters (entry 2 has 81).",
            AttentionRules.Question("Q", null, ["a", new string('o', 81)], "a").Error);
        Assert.Null(AttentionRules.Question(new string('q', 200), new string('c', 2000), ["a", new string('o', 80)], "a").Error);
    }

    [Fact]
    public void A_plan_needs_a_summary_and_steps()
    {
        Assert.StartsWith("summary: required", AttentionRules.Plan(" ", ["one"], null, null).Error, StringComparison.Ordinal);
        Assert.StartsWith("steps: give 1 to 6 steps (you gave 0)", AttentionRules.Plan("S", [" "], null, null).Error, StringComparison.Ordinal);
        var (plan, error) = AttentionRules.Plan(" Add a thing. ", ["one", " two "], null, ["a.cs"]);
        Assert.Null(error);
        Assert.Equal("Add a thing.", plan!.Summary);
        Assert.Equal(["one", "two"], plan.Steps);
        Assert.Empty(plan.Risks);
    }

    [Fact]
    public void A_plan_over_a_limit_says_which_and_how_long_it_is()
    {
        string[] one = ["one"];
        Assert.StartsWith("summary: at most 160 characters (yours has 161)",
            AttentionRules.Plan(new string('s', 161), one, null, null).Error, StringComparison.Ordinal);
        Assert.StartsWith("steps: give 1 to 6 steps (you gave 7)",
            AttentionRules.Plan("S", Enumerable.Range(1, 7).Select(i => $"{i}"), null, null).Error, StringComparison.Ordinal);
        Assert.Equal("steps: each entry at most 120 characters (entry 1 has 121).",
            AttentionRules.Plan("S", [new string('x', 121)], null, null).Error);
        Assert.StartsWith("risks: at most 3 (you gave 4)",
            AttentionRules.Plan("S", one, ["a", "b", "c", "d"], null).Error, StringComparison.Ordinal);
        Assert.Equal("risks: each entry at most 160 characters (entry 1 has 161).",
            AttentionRules.Plan("S", one, [new string('r', 161)], null).Error);
        Assert.Null(AttentionRules.Plan(new string('s', 160), Enumerable.Range(1, 6).Select(_ => new string('x', 120)), ["a", "b", new string('r', 160)], null).Error);
    }

    [Fact]
    public void A_self_report_is_short()
    {
        var mine = Guid.NewGuid();
        var ids = new HashSet<Guid> { mine };

        Assert.Null(AttentionRules.Review(new string('s', 400), [new(mine, CriterionCoverage.Covered, new string('n', 200))], ["a", "b", "c"], ids).Error);
        Assert.StartsWith("summary: at most 400 characters (yours has 401)",
            AttentionRules.Review(new string('s', 401), null, null, ids).Error, StringComparison.Ordinal);
        Assert.StartsWith($"criteria_mapping: each note at most 200 characters (the one for {mine} has 201)",
            AttentionRules.Review("S", [new(mine, CriterionCoverage.Partial, new string('n', 201))], null, ids).Error, StringComparison.Ordinal);
        Assert.StartsWith("open_concerns: at most 3 (you gave 4)",
            AttentionRules.Review("S", null, ["a", "b", "c", "d"], ids).Error, StringComparison.Ordinal);
        Assert.Equal("open_concerns: each entry at most 200 characters (entry 1 has 201).",
            AttentionRules.Review("S", null, [new string('c', 201)], ids).Error);
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
        var question = new QuestionPayload("Q", null, ["a", "b"], "a");

        Assert.Equal(AttentionJson.RequestHash(AttentionKind.Question, question), AttentionJson.RequestHash(AttentionKind.Question, question with { Options = ["a", "b"] }));
        Assert.NotEqual(AttentionJson.RequestHash(AttentionKind.Question, question), AttentionJson.RequestHash(AttentionKind.Question, question with { Recommended = "b" }));
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
