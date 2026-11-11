using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Tikai.Core.Llm;
using Tikai.Core.Tickets;

namespace Tikai.Llm.Refine;

/// <summary>How big a ticket is; the size sets hard limits on the proposal.</summary>
public enum SpecSize
{
    Small,
    Medium,
    Large,
}

/// <summary>A ticket as it is on screen (maybe unsaved), to be refined.</summary>
/// <param name="MinSize">"More detail": the proposal is at least this size.</param>
public sealed record SpecDraft(string ProjectName, string Title, string Description, IReadOnlyList<string> Criteria, SpecSize? MinSize = null);

/// <summary>A new criterion, or a better wording of one of the draft's.</summary>
/// <param name="Replaces">0-based place of the draft criterion it rewrites; null for a new one.</param>
/// <param name="Was">The draft criterion's text, for a rewrite.</param>
public sealed record CriterionChange(int? Replaces, string? Was, string Text);

/// <summary>A question whose answer changes the implementation, with 2–3 options to click.</summary>
/// <param name="Recommended">One of the options, or null.</param>
public sealed record SpecQuestion(string Question, IReadOnlyList<string> Options, string? Recommended);

/// <summary>
/// What the model proposes, only where it differs from the draft. Every part is cleaned to fit the ticket rules and the
/// size's limits; a null title or description means there is no change to that field.
/// </summary>
/// <param name="PromptVersion">The prompt file that produced it, e.g. "refine-spec.v2".</param>
public sealed record SpecProposal(
    SpecSize Size,
    string? Title,
    string? Description,
    IReadOnlyList<CriterionChange> Criteria,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<SpecQuestion> Questions,
    string PromptVersion);

/// <summary>Either a proposal or, for people, why there is none.</summary>
public sealed record RefineResult(SpecProposal? Proposal, string? Error, TimeSpan Duration);

/// <summary>"Refine spec": sends a ticket draft to a provider with the versioned prompt and maps the structured answer.</summary>
public sealed partial class SpecRefiner(IChatClientFactory factory, ILogger<SpecRefiner> logger)
{
    public const string PromptName = "refine-spec.v2";
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);
    internal const int MaxOutputTokens = 8_000;
    public const int MaxQuestionLength = 200;
    public const int MaxOptionLength = 80;
    public const int MaxAssumptionLength = 160;
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The hard limits per size. Criteria: how many the ticket has after taking everything over.</summary>
    public static (int Criteria, int Questions, int Assumptions, bool Description) Limits(SpecSize size) => size switch
    {
        SpecSize.Small => (3, 1, 3, false),
        SpecSize.Medium => (5, 2, 5, true),
        _ => (TicketRules.MaxCriteria, 3, 8, true),
    };

    // Written out, not inferred from RefinedSpec: the Anthropic SDK silently leaves out a schema without a "required"
    // list (no output_config is sent then), and inferred schemas of settable properties have none.
    private static readonly JsonElement _schema = JsonElement.Parse("""
        {
          "type": "object",
          "properties": {
            "size": { "type": "string", "enum": ["small", "medium", "large"], "description": "Decided first; it sets the limits for everything else." },
            "title": { "type": "string", "description": "A better title, one line in the imperative, at most 80 characters. Empty when the title is fine." },
            "description": { "type": "string", "description": "The whole new description in markdown. Empty for a small ticket and when the description is fine." },
            "criteria": {
              "type": "array",
              "description": "Only new criteria and better wordings of existing ones; leave out criteria that are fine.",
              "items": {
                "type": "object",
                "properties": {
                  "replaces": { "type": "integer", "description": "The number of the criterion this rewrites, 0 for a new one." },
                  "text": { "type": "string", "description": "One checkable statement, one line, no numbering." }
                },
                "required": ["replaces", "text"],
                "additionalProperties": false
              }
            },
            "assumptions": { "type": "array", "items": { "type": "string" }, "description": "What you assumed where the ticket is silent, one line each; they count as accepted unless the human changes them." },
            "questions": {
              "type": "array",
              "description": "Only questions whose answer changes the implementation. Usually empty.",
              "items": {
                "type": "object",
                "properties": {
                  "question": { "type": "string", "description": "One line, at most 200 characters." },
                  "options": { "type": "array", "items": { "type": "string" }, "description": "2 or 3 answers to click, each at most 80 characters." },
                  "recommended": { "type": "string", "description": "The option you recommend, exactly as written in options." }
                },
                "required": ["question", "options", "recommended"],
                "additionalProperties": false
              }
            }
          },
          "required": ["size", "title", "description", "criteria", "assumptions", "questions"],
          "additionalProperties": false
        }
        """);

    public async Task<RefineResult> RefineAsync(LlmConnection connection, SpecDraft draft, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Limit);
        try
        {
            using var chat = factory.Create(connection);
            // The schema goes to the provider as structured output (Anthropic: output_config.format), so the answer is JSON.
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.System, Prompts.Load(PromptName)), new ChatMessage(ChatRole.User, DraftMessage(draft))],
                new ChatOptions
                {
                    MaxOutputTokens = MaxOutputTokens,
                    ResponseFormat = ChatResponseFormat.ForJsonSchema(_schema, "refined_spec", "The size of the ticket and what to change in it."),
                },
                timeout.Token);
            LogRefined(PromptName, connection.Model, stopwatch.ElapsedMilliseconds, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);
            if (Parse(response.Text) is not { } answer)
            {
                return new RefineResult(null, response.FinishReason == ChatFinishReason.Length
                    ? "The answer was cut off at the output limit. Try again with a shorter ticket."
                    : "The provider's answer was not a complete proposal. Try again.", stopwatch.Elapsed);
            }
            return new RefineResult(Map(answer, draft), null, stopwatch.Elapsed);
        }
        catch (Exception ex) when (LlmErrors.Describe(ex, connection, Limit, cancellationToken) is { } message)
        {
            LogFailed(PromptName, connection.Model, ex.GetType().Name, message);
            return new RefineResult(null, message, stopwatch.Elapsed);
        }
    }

    private static RefinedSpec? Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<RefinedSpec>(text, _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The user message: the draft, with the description in tags so its own headings do not mix with ours.</summary>
    internal static string DraftMessage(SpecDraft draft)
    {
        var text = new StringBuilder()
            .Append("Project: ").AppendLine(draft.ProjectName)
            .Append("Title: ").AppendLine(draft.Title);
        if (draft.MinSize is { } minSize)
        {
            text.Append("The human asked for more detail: the size is at least ").Append(Name(minSize)).AppendLine(".");
        }
        text.AppendLine()
            .AppendLine("<description>")
            .AppendLine(draft.Description.Trim())
            .AppendLine("</description>")
            .AppendLine()
            .AppendLine("<acceptance_criteria>");
        for (var i = 0; i < draft.Criteria.Count; i++)
        {
            text.Append(i + 1).Append(". ").AppendLine(draft.Criteria[i]);
        }
        return text.AppendLine("</acceptance_criteria>").ToString();
    }

    /// <summary>Cleans the model's answer to what differs from the draft and fits the size. Nothing here calls the model.</summary>
    internal static SpecProposal Map(RefinedSpec answer, SpecDraft draft)
    {
        var size = Size(answer.Size);
        if (draft.MinSize is { } minSize && minSize > size)
        {
            size = minSize;
        }
        var limits = Limits(size);

        var title = OneLine(answer.Title);
        var description = answer.Description?.Trim() ?? "";
        return new SpecProposal(
            size,
            TicketRules.TitleError(title) is null && title != draft.Title.Trim() ? title : null,
            limits.Description && description.Length > 0 && TicketRules.DescriptionError(description) is null && description != draft.Description.Trim()
                ? description
                : null,
            Criteria(answer.Criteria, draft, limits.Criteria),
            [.. Lines(answer.Assumptions?.Select(a => AssumedPrefix().Replace(OneLine(a), "")), a => a.Length > MaxAssumptionLength ? "too long" : null)
                .Take(limits.Assumptions)],
            Questions(answer.Questions, limits.Questions),
            PromptName);
    }

    // Rewrites of draft criteria (at most one each, and only when the text changes) always count; new ones only up to
    // the size's limit for the whole ticket. We never drop what the human wrote.
    private static List<CriterionChange> Criteria(List<RefinedCriterion?>? items, SpecDraft draft, int max)
    {
        var known = new HashSet<string>(draft.Criteria.Select(OneLine), StringComparer.OrdinalIgnoreCase);
        var rewritten = new HashSet<int>();
        var changes = new List<CriterionChange>();
        var room = Math.Max(0, Math.Min(max, TicketRules.MaxCriteria) - draft.Criteria.Count);
        foreach (var item in (items ?? []).OfType<RefinedCriterion>())
        {
            var text = ListMarker().Replace(OneLine(item.Text), "");
            if (text.Length == 0 || TicketRules.CriterionError(text) is not null || !known.Add(text))
            {
                continue; // empty, invalid, the same as a draft criterion (or as one proposed before)
            }
            if (item.Replaces is { } number && number >= 1 && number <= draft.Criteria.Count)
            {
                if (rewritten.Add(number - 1))
                {
                    changes.Add(new CriterionChange(number - 1, draft.Criteria[number - 1], text));
                }
            }
            else if (item.Replaces is null or 0 && room > 0)
            {
                changes.Add(new CriterionChange(null, null, text));
                room--;
            }
        }
        return changes;
    }

    private static List<SpecQuestion> Questions(List<RefinedQuestion?>? items, int max)
    {
        var questions = new List<SpecQuestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in (items ?? []).OfType<RefinedQuestion>())
        {
            var question = ListMarker().Replace(OneLine(item.Question), "");
            var options = (item.Options ?? []).Select(OneLine).Where(o => o.Length is > 0 and <= MaxOptionLength)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (question.Length is 0 or > MaxQuestionLength || options.Count is < 2 or > 3 || !seen.Add(question))
            {
                continue;
            }
            var recommended = options.FirstOrDefault(o => string.Equals(o, OneLine(item.Recommended), StringComparison.OrdinalIgnoreCase));
            questions.Add(new SpecQuestion(question, options, recommended));
            if (questions.Count == max)
            {
                break;
            }
        }
        return questions;
    }

    private static SpecSize Size(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "small" => SpecSize.Small,
        "medium" => SpecSize.Medium,
        _ => SpecSize.Large, // unknown: no limits beyond v1's
    };

    private static string Name(SpecSize size) => size.ToString().ToLowerInvariant();

    // One line each, list markers removed, empty and duplicate entries dropped, entries the rule refuses dropped.
    private static IEnumerable<string> Lines(IEnumerable<string?>? items, Func<string, string?> error) =>
        (items ?? [])
            .Select(OneLine)
            .Select(s => ListMarker().Replace(s, ""))
            .Where(s => s.Length > 0 && error(s) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string OneLine(string? text) => Whitespace().Replace(text ?? "", " ").Trim();

    [GeneratedRegex(@"[\s\p{Cc}]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(?:[-*•]|\d{1,3}[.)])\s+")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"^(?:[-*•]\s+)?assumed:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AssumedPrefix();

    [LoggerMessage(Level = LogLevel.Information, Message = "Refined a spec with {Prompt} on {Model} in {DurationMs} ms ({InputTokens} input, {OutputTokens} output tokens)")]
    private partial void LogRefined(string prompt, string model, long durationMs, long? inputTokens, long? outputTokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refining a spec with {Prompt} on {Model} failed with {ErrorType}: {Reason}")]
    private partial void LogFailed(string prompt, string model, string errorType, string reason);
}

/// <summary>The answer as parsed; its JSON schema is written out in <see cref="SpecRefiner"/>. Everything is optional here, the mapping copes.</summary>
internal sealed class RefinedSpec
{
    public string? Size { get; set; }

    public string? Title { get; set; }

    public string? Description { get; set; }

    public List<RefinedCriterion?>? Criteria { get; set; }

    public List<string?>? Assumptions { get; set; }

    public List<RefinedQuestion?>? Questions { get; set; }
}

internal sealed class RefinedCriterion
{
    public int? Replaces { get; set; }

    public string? Text { get; set; }
}

internal sealed class RefinedQuestion
{
    public string? Question { get; set; }

    public List<string?>? Options { get; set; }

    public string? Recommended { get; set; }
}
