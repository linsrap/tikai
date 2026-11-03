using Tikai.Core.Attention;

namespace Tikai.Core.Reviews;

/// <summary>Where a round's analysis stands; null on rounds from before analyses existed.</summary>
public enum AnalysisStatus
{
    Pending,
    Done,
    Failed,
}

/// <summary>How much analysis a round gets: the tier picks the model and how much of the diff the model reads.</summary>
public enum AnalysisTier
{
    Light,
    Normal,
    Strict,
}

public enum FileRisk
{
    Low,
    Medium,
    High,
}

/// <summary>The layer a story group belongs to; the default reading order is this order.</summary>
public enum StoryLayer
{
    Data,
    Domain,
    Api,
    Ui,
    Tests,
    Config,
    Docs,
    Other,
}

/// <summary>
/// An LLM's analysis of a review round, cleaned against the diff and the ticket before it is stored
/// (<c>review_rounds.analysis</c>). Every changed file is in exactly one group.
/// </summary>
/// <param name="TierReason">Why the tier was chosen, for people ("strict: src/Migrations/x.cs").</param>
/// <param name="PromptVersion">The prompt file that produced it, e.g. "review-analysis.v1".</param>
/// <param name="PatchesOmitted">Files the model saw only as a list entry (over the tier's budget, or no text diff).</param>
public sealed record ReviewAnalysis(
    AnalysisTier Tier,
    string TierReason,
    string Model,
    string PromptVersion,
    string Summary,
    IReadOnlyList<AnalysisCoverage> Coverage,
    IReadOnlyList<StoryGroup> Groups,
    IReadOnlyList<AnalysisFileRisk> Risks,
    IReadOnlyList<string> PatchesOmitted,
    long? InputTokens,
    long? OutputTokens,
    int DurationMs);

/// <summary>How the analysis sees one acceptance criterion; refs point at lines of the new files.</summary>
public sealed record AnalysisCoverage(Guid CriterionId, CriterionCoverage Status, string Note, IReadOnlyList<CodeRef> Refs);

public sealed record CodeRef(string Path, int LineStart, int LineEnd);

/// <summary>Files that belong together when reading the change, in reading order.</summary>
public sealed record StoryGroup(string Title, StoryLayer Layer, string Note, IReadOnlyList<string> Files);

public sealed record AnalysisFileRisk(string Path, FileRisk Risk, string Reason);

/// <summary>
/// The model's answer as parsed from its JSON, before cleaning. Everything is optional and enums are plain strings:
/// the cleaning step decides what is usable.
/// </summary>
public sealed class AnalysisAnswer
{
    public string? Summary { get; set; }

    public List<AnswerCoverage?>? Coverage { get; set; }

    public List<AnswerGroup?>? Groups { get; set; }

    public List<AnswerFile?>? Files { get; set; }
}

public sealed class AnswerCoverage
{
    public string? CriterionId { get; set; }

    public string? Status { get; set; }

    public string? Note { get; set; }

    public List<AnswerRef?>? Refs { get; set; }
}

public sealed class AnswerRef
{
    public string? Path { get; set; }

    public int? LineStart { get; set; }

    public int? LineEnd { get; set; }
}

public sealed class AnswerGroup
{
    public string? Title { get; set; }

    public string? Layer { get; set; }

    public string? Note { get; set; }

    public List<string?>? Files { get; set; }
}

public sealed class AnswerFile
{
    public string? Path { get; set; }

    public string? Risk { get; set; }

    public string? Reason { get; set; }
}
