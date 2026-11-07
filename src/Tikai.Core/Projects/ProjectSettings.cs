using System.Text.Json;
using System.Text.Json.Serialization;
using Tikai.Core.Llm;
using Tikai.Core.Reviews;

namespace Tikai.Core.Projects;

/// <summary>Per-project settings, stored as JSON in <c>projects.settings</c>. Missing parts mean the defaults.</summary>
public sealed record ProjectSettings(
    ReviewSettings? Review = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DeliverySettings? Delivery = null)
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static ProjectSettings Parse(string json) => JsonSerializer.Deserialize<ProjectSettings>(json, _options) ?? new();

    public string ToJson() => JsonSerializer.Serialize(this, _options);

    /// <summary>The model the review analysis of a tier uses: the project's choice, else the default.</summary>
    public string AnalysisModel(AnalysisTier tier) => Review?.Models?.For(tier) ?? ReviewModels.Default(tier);

    /// <summary>How an approved change leaves the hub, with the defaults filled in.</summary>
    [JsonIgnore]
    public DeliverySettings DeliveryOrDefault => new(Delivery?.Mode ?? DeliveryMode.Merge, Delivery?.MergeStyle ?? MergeStyle.Squash);
}

/// <summary>What Approve does; null parts mean the defaults (merge, squash).</summary>
public sealed record DeliverySettings(DeliveryMode? Mode = null, MergeStyle? MergeStyle = null);

public enum DeliveryMode
{
    /// <summary>The hub merges into the default branch and pushes it (when the repository has a remote).</summary>
    Merge,

    /// <summary>The hub pushes the run branch as it is; people merge it on the remote (e.g. a pull request).</summary>
    PushBranch,
}

public enum MergeStyle
{
    /// <summary>One new commit on the default branch with the change's tree.</summary>
    Squash,

    /// <summary>A merge commit with the default branch and the run branch as parents, keeping the agent's commits.</summary>
    MergeCommit,
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
