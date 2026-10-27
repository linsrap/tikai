using System.Globalization;
using Tikai.Core.Git;

namespace Tikai.Git;

/// <summary>
/// Reads <c>git diff-tree -r -z -M</c> output: <c>--raw</c> for kinds, modes and blob ids, <c>--numstat</c> for line
/// counts. Both list the same entries in the same order.
/// </summary>
internal static class GitDiffParser
{
    public static List<GitDiffEntry> Parse(string raw, string numstat)
    {
        var counts = Counts(numstat);
        var entries = new List<GitDiffEntry>();
        var tokens = raw.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            // ":<old mode> <new mode> <old id> <new id> <status>", then one path, or two for a rename.
            if (!tokens[i].StartsWith(':'))
            {
                continue;
            }
            var header = tokens[i][1..].Split(' ');
            if (header.Length < 5 || i + 1 >= tokens.Length)
            {
                throw new FormatException($"Unexpected diff-tree line '{tokens[i]}'.");
            }
            var status = header[4][0];
            var renamed = status is 'R' or 'C';
            var first = tokens[++i];
            var second = renamed && i + 1 < tokens.Length ? tokens[++i] : null;
            var kind = status switch
            {
                'A' or 'C' => GitChangeKind.Added,
                'D' => GitChangeKind.Deleted,
                'R' => GitChangeKind.Renamed,
                'T' => GitChangeKind.TypeChanged,
                _ => GitChangeKind.Modified,
            };
            var (additions, deletions) = entries.Count < counts.Count ? counts[entries.Count] : (null, null);
            entries.Add(new GitDiffEntry(
                kind,
                OldPath: kind == GitChangeKind.Added && !renamed ? null : first,
                NewPath: kind == GitChangeKind.Deleted ? null : second ?? first,
                OldBlob: Blob(header[2]),
                NewBlob: Blob(header[3]),
                header[0],
                header[1],
                additions,
                deletions));
        }
        return entries;
    }

    private static string? Blob(string id) => id.Trim('0').Length == 0 ? null : id;

    /// <summary>"added\tdeleted\tpath", or "added\tdeleted\t" followed by two paths for a rename; "-" for binary files.</summary>
    private static List<(int?, int?)> Counts(string numstat)
    {
        var counts = new List<(int?, int?)>();
        var tokens = numstat.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var parts = tokens[i].Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }
            counts.Add((Number(parts[0]), Number(parts[1])));
            if (parts[2].Length == 0)
            {
                i += 2; // a rename: the two paths follow as their own tokens
            }
        }
        return counts;
    }

    private static int? Number(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
}
