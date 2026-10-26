namespace Tikai.Core.Attention;

/// <summary>
/// Builds payloads from what an agent sends (trimmed, empty entries dropped) and checks answers from people.
/// Each check returns null when valid, otherwise a message for the agent or person.
/// </summary>
public static class AttentionRules
{
    public const int MaxQuestionLength = 4_000;
    public const int MaxContextLength = 20_000;
    public const int MaxOptions = 10;
    public const int MaxOptionLength = 500;
    public const int MaxListItems = 50;
    public const int MaxFiles = 200;
    public const int MaxItemLength = 2_000;
    public const int MaxSummaryLength = 20_000;
    public const int MaxAnswerLength = 20_000;

    public static (QuestionPayload? Payload, string? Error) Question(string? question, string? context, IEnumerable<string?>? options)
    {
        var text = question?.Trim() ?? "";
        var list = Clean(options);
        var error =
            text.Length is 0 or > MaxQuestionLength ? $"Ask a question of 1 to {MaxQuestionLength} characters."
            : context?.Length > MaxContextLength ? $"The context can have at most {MaxContextLength} characters."
            : list.Count > MaxOptions || list.Any(o => o.Length > MaxOptionLength) ? $"Give at most {MaxOptions} options of at most {MaxOptionLength} characters each."
            : null;
        return error is null ? (new QuestionPayload(text, Empty(context), list), null) : (null, error);
    }

    public static (PlanPayload? Payload, string? Error) Plan(IEnumerable<string?>? steps, IEnumerable<string?>? risks, IEnumerable<string?>? filesExpected)
    {
        var stepList = Clean(steps);
        var riskList = Clean(risks);
        var files = Clean(filesExpected);
        var error =
            stepList.Count is 0 or > MaxListItems ? $"Propose 1 to {MaxListItems} steps."
            : riskList.Count > MaxListItems ? $"List at most {MaxListItems} risks."
            : files.Count > MaxFiles ? $"List at most {MaxFiles} expected files."
            : stepList.Concat(riskList).Concat(files).Any(s => s.Length > MaxItemLength) ? $"Each step, risk and file can have at most {MaxItemLength} characters."
            : null;
        return error is null ? (new PlanPayload(stepList, riskList, files), null) : (null, error);
    }

    /// <param name="criterionIds">The ticket's criteria; the mapping may only name these.</param>
    public static (ReviewPayload? Payload, string? Error) Review(
        string? summary, IEnumerable<CriterionMapping>? mapping, IEnumerable<string?>? openConcerns, IReadOnlySet<Guid> criterionIds)
    {
        var text = summary?.Trim() ?? "";
        var mappings = (mapping ?? []).Select(m => m with { Note = Empty(m.Note) }).ToList();
        var concerns = Clean(openConcerns);
        var unknown = mappings.Where(m => !criterionIds.Contains(m.CriterionId)).Select(m => m.CriterionId).ToList();
        var error =
            text.Length is 0 or > MaxSummaryLength ? $"Write a summary of 1 to {MaxSummaryLength} characters."
            : unknown.Count > 0 ? $"These criterion ids are not this ticket's: {string.Join(", ", unknown)}. Use the ids from get_ticket."
            : mappings.DistinctBy(m => m.CriterionId).Count() != mappings.Count ? "Map each criterion at most once."
            : mappings.Any(m => m.Note?.Length > MaxItemLength) ? $"Each note can have at most {MaxItemLength} characters."
            : concerns.Count > MaxListItems || concerns.Any(c => c.Length > MaxItemLength) ? $"List at most {MaxListItems} open concerns of at most {MaxItemLength} characters each."
            : null;
        return error is null ? (new ReviewPayload(text, mappings, concerns), null) : (null, error);
    }

    /// <summary>A question needs a text; a plan needs a decision, and notes (the text) when it is sent back.</summary>
    /// <returns>The answer, or the field ("text" or "decision") that is wrong and why.</returns>
    public static (AttentionAnswer? Answer, (string Field, string Message)? Error) Answer(AttentionKind kind, string? text, PlanDecision? decision)
    {
        var trimmed = Empty(text);
        (string, string)? error = kind switch
        {
            _ when trimmed?.Length > MaxAnswerLength => ("text", $"The answer can have at most {MaxAnswerLength} characters."),
            AttentionKind.Question when trimmed is null => ("text", "Enter an answer."),
            AttentionKind.Question when decision is not null => ("decision", "A question takes a text answer, not a decision."),
            AttentionKind.PlanApproval when decision is null => ("decision", "Approve the plan or send it back for revision."),
            AttentionKind.PlanApproval when decision == PlanDecision.Revise && trimmed is null => ("text", "Say what to change in the plan."),
            AttentionKind.Question or AttentionKind.PlanApproval => null,
            _ => ("kind", $"A {kind} item is not answered here."),
        };
        return error is null ? (new AttentionAnswer(trimmed, decision), null) : (null, error);
    }

    private static List<string> Clean(IEnumerable<string?>? values) =>
        [.. (values ?? []).Select(v => v?.Trim()).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!)];

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
