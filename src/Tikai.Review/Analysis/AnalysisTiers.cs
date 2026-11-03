using Tikai.Core.Git;
using Tikai.Core.Reviews;

namespace Tikai.Review.Analysis;

/// <summary>A changed file of a round as the analysis sees it: the diff entry's facts plus the review flags.</summary>
/// <param name="OldPath">Where a renamed file was before.</param>
/// <param name="Additions">Changed lines; null for a binary file.</param>
public sealed record AnalysisFile(string Path, string? OldPath, GitChangeKind Kind, int? Additions, int? Deletions, FileFlags Flags)
{
    public bool Binary => Additions is null;

    /// <summary>Lock and generated files are listed but not read, grouped or rated.</summary>
    public bool Reviewable => !Flags.Lock && !Flags.Generated;

    /// <summary>Changed lines that count toward the size of the change.</summary>
    public int CountedLines => Reviewable && !Flags.Large ? (Additions ?? 0) + (Deletions ?? 0) : 0;
}

/// <summary>The tier a round's analysis gets and why, in words for people.</summary>
public sealed record TierChoice(AnalysisTier Tier, string Reason);

/// <summary>
/// Picks the analysis tier from a change's size and paths. Strict: a reviewable file in a sensitive path, or a large
/// change. Light: a small change that touches only docs and tests. Normal: everything else. The globs stand in for
/// per-repository trust rules until those exist.
/// </summary>
public static class AnalysisTiers
{
    /// <summary>Above this many counted lines a change is strict.</summary>
    public const int StrictLines = 1_500;

    /// <summary>At most this many counted lines for a light change.</summary>
    public const int LightLines = 300;

    // Matched case-sensitively, as git does; hence both spellings of the usual directory names.
    public static readonly IReadOnlyList<string> StrictGlobs =
    [
        "**/auth/**", "**/Auth/**", "**/security/**", "**/Security/**", "**/migrations/**", "**/Migrations/**",
        "**/*.sql", "**/Dockerfile", "**/compose*.yml", ".github/workflows/**", "**/.gitattributes",
    ];

    public static readonly IReadOnlyList<string> LightGlobs =
    [
        "docs/**", "**/*.md", "**/*.txt", "**/*.test.*", "**/*.spec.*", "**/*Tests.cs", "tests/**", "test/**", "**/__tests__/**",
    ];

    private static readonly PathPattern[] _strict = [.. StrictGlobs.Select(PathPattern.Parse)];
    private static readonly PathPattern[] _light = [.. LightGlobs.Select(PathPattern.Parse)];

    public static TierChoice Choose(IReadOnlyCollection<AnalysisFile> files)
    {
        var reviewable = files.Where(f => f.Reviewable).ToList();
        var lines = files.Sum(f => f.CountedLines);
        if (reviewable.FirstOrDefault(f => Matches(_strict, f)) is { } sensitive)
        {
            return new(AnalysisTier.Strict, $"strict path: {sensitive.Path}");
        }
        if (lines > StrictLines)
        {
            return new(AnalysisTier.Strict, $"large change: {lines} changed lines");
        }
        if (reviewable.Count == 0)
        {
            return new(AnalysisTier.Light, "only lock or generated files");
        }
        if (lines <= LightLines && reviewable.All(f => All(_light, f)))
        {
            return new(AnalysisTier.Light, $"small change ({lines} lines) in docs and tests only");
        }
        return new(AnalysisTier.Normal, $"{lines} changed lines");
    }

    // A rename out of (or into) a sensitive place is strict; a light rename stays within light places.
    private static bool Matches(PathPattern[] patterns, AnalysisFile file) =>
        patterns.Any(p => p.Matches(file.Path)) || (file.OldPath is { } old && patterns.Any(p => p.Matches(old)));

    private static bool All(PathPattern[] patterns, AnalysisFile file) =>
        patterns.Any(p => p.Matches(file.Path)) && (file.OldPath is not { } old || patterns.Any(p => p.Matches(old)));
}
