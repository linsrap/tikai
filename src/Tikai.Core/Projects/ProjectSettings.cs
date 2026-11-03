using System.Text.Json;
using Tikai.Core.Llm;
using Tikai.Core.Reviews;

namespace Tikai.Core.Projects;

/// <summary>Per-project settings, stored as JSON in <c>projects.settings</c>. Missing parts mean the defaults.</summary>
public sealed record ProjectSettings(ReviewSettings? Review = null)
{
    private static readonly JsonSerializerOptions _options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static ProjectSettings Parse(string json) => JsonSerializer.Deserialize<ProjectSettings>(json, _options) ?? new();

    public string ToJson() => JsonSerializer.Serialize(this, _options);

    /// <summary>The model the review analysis of a tier uses: the project's choice, else the default.</summary>
    public string AnalysisModel(AnalysisTier tier) => Review?.Models?.For(tier) ?? ReviewModels.Default(tier);
}

public sealed record ReviewSettings(ReviewModels? Models = null);

/// <summary>A model id per analysis tier; null means the default for that tier.</summary>
public sealed record ReviewModels(string? Light = null, string? Normal = null, string? Strict = null)
{
    public string? For(AnalysisTier tier) => tier switch
    {
        AnalysisTier.Light => Light,
        AnalysisTier.Normal => Normal,
        _ => Strict,
    };

    /// <summary>Cheap for small changes in light paths, full for normal changes, strongest for strict paths and large diffs.</summary>
    public static string Default(AnalysisTier tier) => tier switch
    {
        AnalysisTier.Light => "claude-haiku-5-5",
        AnalysisTier.Normal => "claude-sonnet-5-5",
        _ => "claude-opus-5-5",
    };

    /// <summary>Each model is null or a valid model id.</summary>
    public IEnumerable<(string Field, string Error)> Errors() =>
        new[] { ("light", Light), ("normal", Normal), ("strict", Strict) }
            .Where(m => m.Item2 is not null && LlmProviderRules.ModelError(m.Item2) is not null)
            .Select(m => (m.Item1, LlmProviderRules.ModelError(m.Item2)!));
}
