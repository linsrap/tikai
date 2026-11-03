using System.Text;
using Tikai.Core.Attention;
using Tikai.Core.Git;
using Tikai.Core.Reviews;

namespace Tikai.Review.Analysis;

public sealed record CriterionText(Guid Id, string Text);

/// <summary>What the analysis of one round is about: the ticket as it is now, the agent's self-report, the changed files.</summary>
public sealed record AnalysisContext(
    string TicketKey,
    string Title,
    string Description,
    IReadOnlyList<CriterionText> Criteria,
    ReviewPayload SelfReport,
    IReadOnlyList<AnalysisFile> Files);

/// <summary>
/// Builds the user message for the review analysis. Patches are read within a budget per tier; files over it, binary,
/// large, lock and generated files are only listed. Everything the agent wrote (patches, paths, self-report) sits in
/// tags that the prompt calls data, and our closing tags are broken up inside it so it cannot end a section early.
/// </summary>
public static class AnalysisInput
{
    /// <summary>At most this many files get a patch, whatever the budget.</summary>
    public const int MaxPatchedFiles = 400;

    /// <summary>How much patch text (in characters) the model reads per tier.</summary>
    public static int PatchBudget(AnalysisTier tier) => tier switch
    {
        AnalysisTier.Light => 60_000,
        AnalysisTier.Normal => 200_000,
        _ => 400_000,
    };

    /// <summary>The files whose patch is worth reading, in path order: text files that are neither lock, generated nor large.</summary>
    public static IReadOnlyList<AnalysisFile> PatchCandidates(IEnumerable<AnalysisFile> files) =>
        [.. files.Where(f => f.Reviewable && !f.Binary && !f.Flags.Large).OrderBy(f => f.Path, StringComparer.Ordinal).Take(MaxPatchedFiles)];

    /// <param name="patches">Patch text per path, for the files that fit the budget.</param>
    /// <returns>The message, and the reviewable files the model did not get a patch of.</returns>
    public static (string Message, IReadOnlyList<string> Omitted) Build(AnalysisContext context, IReadOnlyDictionary<string, string> patches)
    {
        var omitted = context.Files.Where(f => f.Reviewable && !patches.ContainsKey(f.Path)).Select(f => f.Path).Order(StringComparer.Ordinal).ToList();
        var text = new StringBuilder()
            .Append("Ticket: ").Append(context.TicketKey).Append(" — ").AppendLine(context.Title)
            .AppendLine()
            .AppendLine("<description>")
            .AppendLine(context.Description.Trim())
            .AppendLine("</description>")
            .AppendLine()
            .AppendLine("<acceptance_criteria>");
        foreach (var criterion in context.Criteria)
        {
            text.Append("- id: ").AppendLine(criterion.Id.ToString("D"))
                .Append("  text: ").AppendLine(OneLine(criterion.Text));
        }
        text.AppendLine("</acceptance_criteria>")
            .AppendLine()
            .AppendLine("<self_report>")
            .Append("Summary: ").AppendLine(Data(context.SelfReport.Summary.Trim()))
            .AppendLine("Coverage the agent claims:");
        foreach (var mapping in context.SelfReport.CriteriaMapping)
        {
            text.Append("- ").Append(mapping.CriterionId.ToString("D")).Append(": ").Append(Name(mapping.Status))
                .AppendLine(mapping.Note is { } note ? " — " + Data(OneLine(note)) : "");
        }
        text.AppendLine("Open concerns:");
        foreach (var concern in context.SelfReport.OpenConcerns)
        {
            text.Append("- ").AppendLine(Data(OneLine(concern)));
        }
        text.AppendLine("</self_report>")
            .AppendLine()
            .AppendLine("<files>");
        foreach (var file in context.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            text.AppendLine(Data(FileLine(file, patches.ContainsKey(file.Path))));
        }
        text.AppendLine("</files>")
            .AppendLine()
            .AppendLine("<diff>");
        foreach (var (_, patch) in patches.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            text.Append(Data(patch));
            if (!patch.EndsWith('\n'))
            {
                text.AppendLine();
            }
        }
        text.AppendLine("</diff>");
        return (text.ToString(), omitted);
    }

    /// <summary>"M src/a.cs (+3 -1)", with what the model does not get to see in brackets.</summary>
    internal static string FileLine(AnalysisFile file, bool patched)
    {
        var letter = file.Kind switch
        {
            GitChangeKind.Added => "A",
            GitChangeKind.Deleted => "D",
            GitChangeKind.Renamed => "R",
            GitChangeKind.TypeChanged => "T",
            _ => "M",
        };
        var line = new StringBuilder(letter).Append(' ');
        if (file.OldPath is { } old)
        {
            line.Append(old).Append(" -> ");
        }
        line.Append(file.Path);
        if (!file.Binary)
        {
            line.Append(" (+").Append(file.Additions).Append(" -").Append(file.Deletions).Append(')');
        }
        string? note = file switch
        {
            { Flags.Lock: true } => "lock file, not shown",
            { Flags.Generated: true } => "generated, not shown",
            { Binary: true } => "binary",
            { Flags.Large: true } => "large, patch omitted",
            _ when !patched => "patch omitted (over the budget)",
            _ => null,
        };
        return note is null ? line.ToString() : line.Append(" [").Append(note).Append(']').ToString();
    }

    private static string Name(CriterionCoverage coverage) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(coverage.ToString());

    private static string OneLine(string text) => string.Join(' ', text.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    // Agent-written text cannot close one of our sections.
    private static string Data(string text) =>
        text.Replace("</diff>", "<\\/diff>", StringComparison.OrdinalIgnoreCase)
            .Replace("</files>", "<\\/files>", StringComparison.OrdinalIgnoreCase)
            .Replace("</self_report>", "<\\/self_report>", StringComparison.OrdinalIgnoreCase);
}
