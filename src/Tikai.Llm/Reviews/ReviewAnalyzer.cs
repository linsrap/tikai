using System.Diagnostics;
using System.Text.Json;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Tikai.Core.Llm;
using Tikai.Core.Reviews;

namespace Tikai.Llm.Reviews;

/// <summary>Either the model's parsed answer or, for people, why there is none.</summary>
/// <param name="Transient">Worth trying again later (rate limit, overload, timeout, provider unreachable).</param>
public sealed record AnalyzerResult(
    AnalysisAnswer? Answer,
    string? Error,
    bool Transient,
    long? InputTokens,
    long? OutputTokens,
    TimeSpan Duration);

/// <summary>
/// The review analysis call: the versioned prompt, the round's prepared message (see Tikai.Review's AnalysisInput)
/// and an explicit JSON schema. Cleaning the answer against the diff is not done here.
/// </summary>
public sealed partial class ReviewAnalyzer(IChatClientFactory factory, ILogger<ReviewAnalyzer> logger)
{
    public const string PromptName = "review-analysis.v2";

    /// <summary>For a later round: the same schema, read from the previous analysis and the interdiff.</summary>
    public const string InterdiffPromptName = "review-analysis-interdiff.v2";
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // Written out with "required" lists: the Anthropic SDK silently sends no schema without one.
    private static readonly JsonElement _schema = JsonElement.Parse("""
        {
          "type": "object",
          "properties": {
            "headline": { "type": "string", "description": "One sentence, at most 160 characters: what the change does and whether it meets the ticket." },
            "look_at": {
              "type": "array",
              "description": "At most 3 places the reviewer must read closely, most important first. Empty when nothing stands out.",
              "items": {
                "type": "object",
                "properties": {
                  "path": { "type": "string", "description": "The file's path as listed (new path)." },
                  "line": { "type": "integer", "description": "A line in the new version of the file." },
                  "reason": { "type": "string", "description": "One line, at most 160 characters: what to check there." }
                },
                "required": ["path", "line", "reason"],
                "additionalProperties": false
              }
            },
            "coverage": {
              "type": "array",
              "description": "One entry per acceptance criterion.",
              "items": {
                "type": "object",
                "properties": {
                  "criterion_id": { "type": "string", "description": "The criterion's id as given." },
                  "status": { "type": "string", "enum": ["covered", "partial", "missing"] },
                  "note": { "type": "string", "description": "One line: what implements it, or what is missing." },
                  "refs": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "path": { "type": "string", "description": "The file's path as listed (new path)." },
                        "line_start": { "type": "integer", "description": "First line in the new version of the file." },
                        "line_end": { "type": "integer", "description": "Last line in the new version of the file." }
                      },
                      "required": ["path", "line_start", "line_end"],
                      "additionalProperties": false
                    }
                  }
                },
                "required": ["criterion_id", "status", "note", "refs"],
                "additionalProperties": false
              }
            },
            "groups": {
              "type": "array",
              "description": "Files in reading order, grouped by what they do.",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string", "description": "A few words." },
                  "layer": { "type": "string", "enum": ["data", "domain", "api", "ui", "tests", "config", "docs", "other"] },
                  "note": { "type": "string", "description": "One line: what this group does in the change." },
                  "files": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["title", "layer", "note", "files"],
                "additionalProperties": false
              }
            },
            "files": {
              "type": "array",
              "description": "Only files whose change is medium or high risk; every other file is low.",
              "items": {
                "type": "object",
                "properties": {
                  "path": { "type": "string" },
                  "risk": { "type": "string", "enum": ["medium", "high"] },
                  "reason": { "type": "string", "description": "One line the reviewer can act on." }
                },
                "required": ["path", "risk", "reason"],
                "additionalProperties": false
              }
            },
            "summary": { "type": "string", "description": "Details for who wants them: what the change does and how, at most 3 sentences." }
          },
          "required": ["headline", "look_at", "coverage", "groups", "files", "summary"],
          "additionalProperties": false
        }
        """);

    public static int MaxOutputTokens(AnalysisTier tier) => tier == AnalysisTier.Strict ? 16_000 : 8_000;

    /// <param name="prompt">The prompt file; <see cref="PromptName"/> or <see cref="InterdiffPromptName"/>.</param>
    public async Task<AnalyzerResult> AnalyzeAsync(
        LlmConnection connection, string message, AnalysisTier tier, CancellationToken cancellationToken, string prompt = PromptName)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Limit);
        try
        {
            using var chat = factory.Create(connection);
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.System, Prompts.Load(prompt)), new ChatMessage(ChatRole.User, message)],
                new ChatOptions
                {
                    MaxOutputTokens = MaxOutputTokens(tier),
                    ResponseFormat = ChatResponseFormat.ForJsonSchema(_schema, "review_analysis", "The analysis of a code change against its ticket."),
                },
                timeout.Token);
            var input = response.Usage?.InputTokenCount;
            var output = response.Usage?.OutputTokenCount;
            LogAnalyzed(prompt, connection.Model, stopwatch.ElapsedMilliseconds, input, output);
            if (Parse(response.Text) is not { } answer)
            {
                return new AnalyzerResult(null, response.FinishReason == ChatFinishReason.Length
                    ? "The answer was cut off at the output limit."
                    : "The provider's answer was not a complete analysis.", false, input, output, stopwatch.Elapsed);
            }
            return new AnalyzerResult(answer, null, false, input, output, stopwatch.Elapsed);
        }
        catch (Exception ex) when (LlmErrors.Describe(ex, connection, Limit, cancellationToken) is { } error)
        {
            LogFailed(prompt, connection.Model, ex.GetType().Name, error);
            return new AnalyzerResult(null, error, IsTransient(ex), null, null, stopwatch.Elapsed);
        }
    }

    internal static AnalysisAnswer? Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<AnalysisAnswer>(text, _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        OperationCanceledException or AnthropicIOException or AnthropicRateLimitException or Anthropic5xxException => true,
        AnthropicApiException api => (int)api.StatusCode >= 500,
        _ => false,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysed a review round with {Prompt} on {Model} in {DurationMs} ms ({InputTokens} input, {OutputTokens} output tokens)")]
    private partial void LogAnalyzed(string prompt, string model, long durationMs, long? inputTokens, long? outputTokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Analysing a review round with {Prompt} on {Model} failed with {ErrorType}: {Reason}")]
    private partial void LogFailed(string prompt, string model, string errorType, string reason);
}
