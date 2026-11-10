namespace Tikai.Core.Attention;

/// <summary>
/// Builds payloads from what an agent sends (trimmed, empty entries dropped) and checks answers from people.
/// Each check returns null when valid, otherwise a message for the agent or person.
/// </summary>
/// <remarks>
/// The limits keep what people read in the inbox short (headline first, details on demand). An agent that sends too
/// much gets an error naming the field, the limit and its length, so it can shorten the text in one try.
/// </remarks>
public static class AttentionRules
{
    public const int MaxQuestionLength = 200;
    public const int MaxContextLength = 2_000;
    public const int MinOptions = 2;
    public const int MaxOptions = 4;
    public const int MaxOptionLength = 80;

    public const int MaxPlanSummaryLength = 160;
    public const int MaxSteps = 6;
    public const int MaxStepLength = 120;
    public const int MaxRisks = 3;
    public const int MaxRiskLength = 160;
    public const int MaxFiles = 200;
    public const int MaxFileLength = 500;

    public const int MaxReviewSummaryLength = 400;
    public const int MaxNoteLength = 200;
    public const int MaxConcerns = 3;
    public const int MaxConcernLength = 200;

    public const int MaxAnswerLength = 20_000;

    /// <param name="recommended">One of the options: the one the agent would choose.</param>
    public static (QuestionPayload? Payload, string? Error) Question(string? question, string? context, IEnumerable<string?>? options, string? recommended)
    {
        var text = question?.Trim() ?? "";
        var background = Empty(context);
        var list = Clean(options);
        var choice = recommended?.Trim() ?? "";
        var error =
            Length("question", text, MaxQuestionLength, "Ask one short question; put the background into context.")
            ?? (background?.Length > MaxContextLength
                ? $"context: at most {MaxContextLength} characters (yours has {background.Length}). Keep only what the human needs to decide."
                : null)
            ?? (list.Count is < MinOptions or > MaxOptions
                ? $"options: give {MinOptions} to {MaxOptions} possible answers (you gave {list.Count}). The human can still answer in their own words."
                : null)
            ?? Items("options", list, MaxOptionLength)
            ?? (list.Distinct(StringComparer.Ordinal).Count() != list.Count ? "options: each option must be different." : null)
            ?? (choice.Length == 0 ? "recommended: required. Name the option you would choose." : null)
            ?? (!list.Contains(choice, StringComparer.Ordinal) ? "recommended: must be exactly one of the options." : null);
        return error is null ? (new QuestionPayload(text, background, list, choice), null) : (null, error);
    }

    /// <param name="summary">The plan in one sentence: the headline people read first.</param>
    public static (PlanPayload? Payload, string? Error) Plan(
        string? summary, IEnumerable<string?>? steps, IEnumerable<string?>? risks, IEnumerable<string?>? filesExpected)
    {
        var text = summary?.Trim() ?? "";
        var stepList = Clean(steps);
        var riskList = Clean(risks);
        var files = Clean(filesExpected);
        var error =
            Length("summary", text, MaxPlanSummaryLength, "Say in one sentence what you will do.")
            ?? (stepList.Count is 0 or > MaxSteps
                ? $"steps: give 1 to {MaxSteps} steps (you gave {stepList.Count}). Merge small steps into larger ones."
                : null)
            ?? Items("steps", stepList, MaxStepLength)
            ?? (riskList.Count > MaxRisks
                ? $"risks: at most {MaxRisks} (you gave {riskList.Count}). Keep the ones a human should know before approving."
                : null)
            ?? Items("risks", riskList, MaxRiskLength)
            ?? (files.Count > MaxFiles ? $"files_expected: at most {MaxFiles} files (you gave {files.Count})." : null)
            ?? Items("files_expected", files, MaxFileLength);
        return error is null ? (new PlanPayload(stepList, riskList, files, text), null) : (null, error);
    }

    /// <param name="criterionIds">The ticket's criteria; the mapping may only name these.</param>
    public static (ReviewPayload? Payload, string? Error) Review(
        string? summary, IEnumerable<CriterionMapping>? mapping, IEnumerable<string?>? openConcerns, IReadOnlySet<Guid> criterionIds)
    {
        var text = summary?.Trim() ?? "";
        var mappings = (mapping ?? []).Select(m => m with { Note = Empty(m.Note) }).ToList();
        var concerns = Clean(openConcerns);
        var unknown = mappings.Where(m => !criterionIds.Contains(m.CriterionId)).Select(m => m.CriterionId).ToList();
        var longNote = mappings.FirstOrDefault(m => m.Note?.Length > MaxNoteLength);
        var error =
            Length("summary", text, MaxReviewSummaryLength, "At most 3 sentences: what changed and why; details belong in the commits.")
            ?? (unknown.Count > 0 ? $"These criterion ids are not this ticket's: {string.Join(", ", unknown)}. Use the ids from get_ticket." : null)
            ?? (mappings.DistinctBy(m => m.CriterionId).Count() != mappings.Count ? "Map each criterion at most once." : null)
            ?? (longNote is not null
                ? $"criteria_mapping: each note at most {MaxNoteLength} characters (the one for {longNote.CriterionId} has {longNote.Note!.Length}). Say where or why in a few words."
                : null)
            ?? (concerns.Count > MaxConcerns
                ? $"open_concerns: at most {MaxConcerns} (you gave {concerns.Count}). Keep the ones a reviewer must know."
                : null)
            ?? Items("open_concerns", concerns, MaxConcernLength);
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

    private static string? Length(string field, string text, int max, string hint) =>
        text.Length == 0 ? $"{field}: required. {hint}"
        : text.Length > max ? $"{field}: at most {max} characters (yours has {text.Length}). {hint}"
        : null;

    private static string? Items(string field, List<string> items, int max)
    {
        var index = items.FindIndex(i => i.Length > max);
        return index < 0 ? null : $"{field}: each entry at most {max} characters (entry {index + 1} has {items[index].Length}).";
    }

    private static List<string> Clean(IEnumerable<string?>? values) =>
        [.. (values ?? []).Select(v => v?.Trim()).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!)];

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
