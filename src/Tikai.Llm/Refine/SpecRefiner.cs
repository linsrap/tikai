using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Tikai.Core.Llm;
using Tikai.Core.Tickets;

namespace Tikai.Llm.Refine;

/// <summary>A ticket as it is on screen (maybe unsaved), to be refined.</summary>
public sealed record SpecDraft(string ProjectName, string Title, string Description, IReadOnlyList<string> Criteria);

/// <summary>
/// What the model proposes. Every part is cleaned to fit the ticket rules; a null title or description
/// means there is no usable proposal for that field.
/// </summary>
/// <param name="PromptVersion">The prompt file that produced it, e.g. "refine-spec.v1".</param>
public sealed record SpecProposal(string? Title, string? Description, IReadOnlyList<string> Criteria, IReadOnlyList<string> Questions, string PromptVersion);

/// <summary>Either a proposal or, for people, why there is none.</summary>
public sealed record RefineResult(SpecProposal? Proposal, string? Error, TimeSpan Duration);

/// <summary>"Refine spec": sends a ticket draft to a provider with the versioned prompt and maps the structured answer.</summary>
public sealed partial class SpecRefiner(IChatClientFactory factory, ILogger<SpecRefiner> logger)
{
    public const string PromptName = "refine-spec.v1";
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);
    internal const int MaxOutputTokens = 8_000;
    internal const int MaxQuestions = 10;
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    // Written out, not inferred from RefinedSpec: the Anthropic SDK silently leaves out a schema without a "required"
    // list (no output_config is sent then), and inferred schemas of settable properties have none.
    private static readonly JsonElement _schema = JsonElement.Parse("""
        {
          "type": "object",
          "properties": {
            "title": { "type": "string", "description": "One-line title in the imperative, at most 80 characters." },
            "description": { "type": "string", "description": "Markdown with the sections Goal, Context, Constraints, Out of scope (only those with real content)." },
            "criteria": { "type": "array", "items": { "type": "string" }, "description": "Acceptance criteria: one checkable statement per entry, one line each, no numbering." },
            "questions": { "type": "array", "items": { "type": "string" }, "description": "Up to five open questions for the human, one line each. Empty when the ticket is clear." }
          },
          "required": ["title", "description", "criteria", "questions"],
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
                    ResponseFormat = ChatResponseFormat.ForJsonSchema(_schema, "refined_spec", "The refined ticket and open questions."),
                },
                timeout.Token);
            LogRefined(PromptName, connection.Model, stopwatch.ElapsedMilliseconds, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);
            if (Parse(response.Text) is not { } answer)
            {
                return new RefineResult(null, response.FinishReason == ChatFinishReason.Length
                    ? "The answer was cut off at the output limit. Try again with a shorter ticket."
                    : "The provider's answer was not a complete proposal. Try again.", stopwatch.Elapsed);
            }
            return new RefineResult(Map(answer), null, stopwatch.Elapsed);
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
            .Append("Title: ").AppendLine(draft.Title)
            .AppendLine()
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

    /// <summary>Cleans the model's answer to what a ticket accepts. Nothing here calls the model.</summary>
    internal static SpecProposal Map(RefinedSpec answer)
    {
        var title = OneLine(answer.Title);
        var description = answer.Description?.Trim();
        var criteria = Lines(answer.Criteria, TicketRules.CriterionError).Take(TicketRules.MaxCriteria).ToList();
        var questions = Lines(answer.Questions, q => q.Length > TicketRules.MaxCriterionLength ? "too long" : null).Take(MaxQuestions).ToList();
        return new SpecProposal(
            TicketRules.TitleError(title) is null ? title : null,
            string.IsNullOrEmpty(description) || TicketRules.DescriptionError(description) is not null ? null : description,
            criteria,
            questions,
            PromptName);
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Refined a spec with {Prompt} on {Model} in {DurationMs} ms ({InputTokens} input, {OutputTokens} output tokens)")]
    private partial void LogRefined(string prompt, string model, long durationMs, long? inputTokens, long? outputTokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refining a spec with {Prompt} on {Model} failed with {ErrorType}: {Reason}")]
    private partial void LogFailed(string prompt, string model, string errorType, string reason);
}

/// <summary>The answer as parsed; its JSON schema is written out in <see cref="SpecRefiner"/>. Everything is optional here, the mapping copes.</summary>
internal sealed class RefinedSpec
{
    public string? Title { get; set; }

    public string? Description { get; set; }

    public List<string?>? Criteria { get; set; }

    public List<string?>? Questions { get; set; }
}
