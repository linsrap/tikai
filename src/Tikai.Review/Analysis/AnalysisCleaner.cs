using System.Text.RegularExpressions;
using Tikai.Core.Attention;
using Tikai.Core.Git;
using Tikai.Core.Reviews;

namespace Tikai.Review.Analysis;

/// <summary>The usable part of the model's answer: headline, places to look at, coverage, groups and risks that fit the ticket and the diff.</summary>
public sealed record CleanAnalysis(
    string Headline,
    IReadOnlyList<LookAt> LookAt,
    string Summary,
    IReadOnlyList<AnalysisCoverage> Coverage,
    IReadOnlyList<StoryGroup> Groups,
    IReadOnlyList<AnalysisFileRisk> Risks);

/// <summary>
/// Checks the model's answer against the diff and the ticket. Unknown criterion ids and paths are dropped, every
/// reviewable file ends up in exactly one group ("Other changes" for the rest), lock and generated files in none (the
/// review screen keeps them apart), look_at points at lines of files that exist after the change, a low risk is left out
/// (low is the default), and texts become single trimmed lines of bounded length. Nothing here calls the model.
/// </summary>
public static partial class AnalysisCleaner
{
    public const int MaxHeadlineLength = 160;
    public const int MaxLookAt = 3;
    public const int MaxLookAtReasonLength = 160;
    public const int MaxSummaryLength = 1_500;
    public const int MaxNoteLength = 400;
    public const int MaxTitleLength = 100;
    public const int MaxGroups = 12;
    public const int MaxRefsPerCriterion = 8;
    public const string OtherTitle = "Other changes";

    /// <returns>Null when the answer has nothing usable (no headline, summary, coverage or groups).</returns>
    public static CleanAnalysis? Clean(AnalysisAnswer answer, IReadOnlyList<AnalysisFile> files, IReadOnlyList<Guid> criterionIds)
    {
        var reviewable = files.Where(f => f.Reviewable).ToList();
        // Paths the model may name: the new path, and the old path of a rename (mapped to the new one).
        var byPath = new Dictionary<string, AnalysisFile>(StringComparer.Ordinal);
        foreach (var file in reviewable)
        {
            byPath[file.Path] = file;
        }
        foreach (var file in reviewable.Where(f => f.OldPath is not null))
        {
            byPath.TryAdd(file.OldPath!, file);
        }

        var headline = Text(answer.Headline, MaxHeadlineLength);
        var summary = Text(answer.Summary, MaxSummaryLength);
        var coverage = Coverage(answer.Coverage, byPath, criterionIds);
        var groups = Groups(answer.Groups, byPath, reviewable);
        var risks = Risks(answer.Files, byPath);
        var lookAt = LookAts(answer.LookAt, byPath);
        if (headline.Length == 0 && summary.Length == 0 && coverage.Count == 0 && !groups.Any(g => g.Title != OtherTitle))
        {
            return null;
        }
        return new CleanAnalysis(headline, lookAt, summary, coverage, groups, risks);
    }

    private static List<AnalysisCoverage> Coverage(List<AnswerCoverage?>? items, Dictionary<string, AnalysisFile> byPath, IReadOnlyList<Guid> criterionIds)
    {
        var found = new Dictionary<Guid, AnalysisCoverage>();
        foreach (var item in items ?? [])
        {
            if (item is null || !Guid.TryParse(item.CriterionId, out var id) || !criterionIds.Contains(id) || found.ContainsKey(id)
                || Status(item.Status) is not { } status)
            {
                continue;
            }
            var refs = (item.Refs ?? [])
                .Select(r => Ref(r, byPath))
                .OfType<CodeRef>()
                .Distinct()
                .Take(MaxRefsPerCriterion)
                .ToList();
            found[id] = new AnalysisCoverage(id, status, Text(item.Note, MaxNoteLength), refs);
        }
        // In the ticket's order; a criterion the model left out has no entry ("not assessed").
        return [.. criterionIds.Where(found.ContainsKey).Select(id => found[id])];
    }

    /// <summary>At most <see cref="MaxLookAt"/> lines of text files that exist after the change, each place once.</summary>
    private static List<LookAt> LookAts(List<AnswerLookAt?>? items, Dictionary<string, AnalysisFile> byPath) =>
        [.. (items ?? []).OfType<AnswerLookAt>()
            .Select(item => Ref(new AnswerRef { Path = item.Path, LineStart = item.Line, LineEnd = item.Line }, byPath) is { } at
                ? new LookAt(at.Path, at.LineStart, Text(item.Reason, MaxLookAtReasonLength))
                : null)
            .OfType<LookAt>()
            .DistinctBy(l => (l.Path, l.Line))
            .Take(MaxLookAt)];

    /// <summary>A ref to lines of a file that exists after the change; reversed ranges are turned around.</summary>
    private static CodeRef? Ref(AnswerRef? item, Dictionary<string, AnalysisFile> byPath)
    {
        if (item?.Path is not { } path || !byPath.TryGetValue(path.Trim(), out var file) || file.Kind == GitChangeKind.Deleted || file.Binary
            || item.LineStart is not { } start)
        {
            return null;
        }
        var end = item.LineEnd ?? start;
        (start, end) = start <= end ? (start, end) : (end, start);
        return start < 1 ? null : new CodeRef(file.Path, start, end);
    }

    private static List<StoryGroup> Groups(List<AnswerGroup?>? items, Dictionary<string, AnalysisFile> byPath, List<AnalysisFile> reviewable)
    {
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<StoryGroup>();
        foreach (var item in (items ?? []).OfType<AnswerGroup>())
        {
            if (groups.Count == MaxGroups - 1)
            {
                break; // one place is kept for "Other changes"
            }
            var title = Text(item.Title, MaxTitleLength);
            var groupFiles = (item.Files ?? [])
                .Select(p => p is not null && byPath.TryGetValue(p.Trim(), out var file) ? file.Path : null)
                .OfType<string>()
                .Where(placed.Add)
                .ToList();
            if (title.Length == 0 || title == OtherTitle || groupFiles.Count == 0)
            {
                placed.ExceptWith(groupFiles);
                continue;
            }
            groups.Add(new StoryGroup(title, Layer(item.Layer), Text(item.Note, MaxNoteLength), groupFiles));
        }
        var rest = reviewable.Select(f => f.Path).Where(p => !placed.Contains(p)).Order(StringComparer.Ordinal).ToList();
        if (rest.Count > 0)
        {
            groups.Add(new StoryGroup(OtherTitle, StoryLayer.Other, "", rest));
        }
        return groups;
    }

    private static List<AnalysisFileRisk> Risks(List<AnswerFile?>? items, Dictionary<string, AnalysisFile> byPath)
    {
        var risks = new Dictionary<string, AnalysisFileRisk>(StringComparer.Ordinal);
        foreach (var item in (items ?? []).OfType<AnswerFile>())
        {
            if (item.Path is { } path && byPath.TryGetValue(path.Trim(), out var file) && Risk(item.Risk) is { } risk && risk != FileRisk.Low)
            {
                risks.TryAdd(file.Path, new AnalysisFileRisk(file.Path, risk, Text(item.Reason, MaxNoteLength)));
            }
        }
        return [.. risks.Values.OrderBy(r => r.Path, StringComparer.Ordinal)];
    }

    private static CriterionCoverage? Status(string? value) => Normalize(value) switch
    {
        "covered" => CriterionCoverage.Covered,
        "partial" => CriterionCoverage.Partial,
        "missing" => CriterionCoverage.Missing,
        _ => null,
    };

    private static FileRisk? Risk(string? value) => Normalize(value) switch
    {
        "low" => FileRisk.Low,
        "medium" => FileRisk.Medium,
        "high" => FileRisk.High,
        _ => null,
    };

    private static StoryLayer Layer(string? value) => Normalize(value) switch
    {
        "data" => StoryLayer.Data,
        "domain" => StoryLayer.Domain,
        "api" => StoryLayer.Api,
        "ui" => StoryLayer.Ui,
        "tests" or "test" => StoryLayer.Tests,
        "config" => StoryLayer.Config,
        "docs" => StoryLayer.Docs,
        _ => StoryLayer.Other,
    };

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? "";

    /// <summary>One line, trimmed, cut at a word boundary with an ellipsis when too long.</summary>
    internal static string Text(string? text, int max)
    {
        var line = Whitespace().Replace(text ?? "", " ").Trim();
        if (line.Length <= max)
        {
            return line;
        }
        var cut = line[..(max - 1)];
        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut).TrimEnd() + "…";
    }

    [GeneratedRegex(@"[\s\p{Cc}]+")]
    private static partial Regex Whitespace();
}
