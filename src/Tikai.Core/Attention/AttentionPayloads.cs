using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tikai.Core.Attention;

/// <summary>An agent's question (ask_human). An option is answered by sending its text.</summary>
/// <param name="Recommended">The option the agent would choose (null on questions from before step 20).</param>
public sealed record QuestionPayload(string Question, string? Context, IReadOnlyList<string> Options, string? Recommended = null);

/// <summary>An agent's plan (propose_plan), approved or sent back with notes.</summary>
/// <param name="Summary">The plan in one sentence, the inbox's headline (null on plans from before step 20).</param>
public sealed record PlanPayload(IReadOnlyList<string> Steps, IReadOnlyList<string> Risks, IReadOnlyList<string> FilesExpected, string? Summary = null);

/// <summary>The agent's self-report from submit_for_review.</summary>
public sealed record ReviewPayload(string Summary, IReadOnlyList<CriterionMapping> CriteriaMapping, IReadOnlyList<string> OpenConcerns);

/// <summary>How the agent says it covered one acceptance criterion (by its stable id).</summary>
public sealed record CriterionMapping(Guid CriterionId, CriterionCoverage Status, string? Note);

public enum CriterionCoverage
{
    Covered,
    Partial,
    Missing,
}

/// <summary>A human's answer: the text for a question; the decision (and notes as text) for a plan.</summary>
public sealed record AttentionAnswer(string? Text, PlanDecision? Decision);

public enum PlanDecision
{
    Approve,
    Revise,
}

/// <summary>How payloads and answers are stored (jsonb) and how a blocking request is recognised again.</summary>
public static class AttentionJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Stored {typeof(T).Name} is null.");

    /// <summary>
    /// SHA-256 (hex) of the kind and the payload JSON. The payload's strings are trimmed when it is built and
    /// nothing else is normalized: a fuzzy match could hand an answer to a different question.
    /// </summary>
    public static string RequestHash<T>(AttentionKind kind, T payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\n{Serialize(payload)}")));
}

/// <summary>Why a run failed, for the inbox (no answer expected; retry and dismiss come with the inbox).</summary>
public sealed record FailurePayload(string Reason, int? ExitCode);

/// <summary>An approved round could not be merged: the default branch moved and these files conflict.</summary>
public sealed record ConflictPayload(int RoundNo, string DefaultBranch, IReadOnlyList<string> Files);
