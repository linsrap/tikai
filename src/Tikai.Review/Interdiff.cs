using System.Text;
using System.Text.RegularExpressions;
using Tikai.Core.Git;

namespace Tikai.Review;

/// <summary>What a reviewer sees of a round: everything since its base, or only what changed since the round before.</summary>
public enum ReviewView
{
    Full,
    Interdiff,
}

/// <summary>How a round's interdiff is computed.</summary>
public enum InterdiffKind
{
    /// <summary>The first round: there is nothing to compare with, so the interdiff is the full change.</summary>
    None,

    /// <summary>Same base as the round before: a tree diff of the two heads (right also after a rewritten history).</summary>
    Heads,

    /// <summary>
    /// The base changed (a rebase): a diff of the heads would show upstream changes as the agent's, so the files whose
    /// change differs between the two rounds are listed with their whole change.
    /// </summary>
    PerFile,
}

/// <summary>The pure parts of the interdiff: which kind applies, and whether a file's change differs between rounds.</summary>
public static partial class Interdiff
{
    /// <param name="previousBase">The base of the round before, or null for the first round.</param>
    public static InterdiffKind Kind(string? previousBase, string currentBase) =>
        previousBase is null ? InterdiffKind.None
        : string.Equals(previousBase, currentBase, StringComparison.Ordinal) ? InterdiffKind.Heads
        : InterdiffKind.PerFile;

    /// <summary>
    /// Files changed in either round (by their current path) whose change may differ: the same blobs on both sides mean
    /// the same change; everything else needs a patch comparison (<see cref="SamePatch"/>). In the order of the current
    /// round, then files only the earlier round changed.
    /// </summary>
    public static (IReadOnlyList<GitDiffEntry> Same, IReadOnlyList<(GitDiffEntry? Previous, GitDiffEntry? Current)> ToCompare) Pair(
        IReadOnlyList<GitDiffEntry> previous, IReadOnlyList<GitDiffEntry> current)
    {
        var before = new Dictionary<string, GitDiffEntry>(StringComparer.Ordinal);
        foreach (var entry in previous)
        {
            before.TryAdd(entry.Path, entry);
        }
        var same = new List<GitDiffEntry>();
        var compare = new List<(GitDiffEntry?, GitDiffEntry?)>();
        foreach (var entry in current)
        {
            if (before.Remove(entry.Path, out var earlier))
            {
                if (earlier.OldBlob == entry.OldBlob && earlier.NewBlob == entry.NewBlob && earlier.OldPath == entry.OldPath)
                {
                    same.Add(entry);
                }
                else
                {
                    compare.Add((earlier, entry));
                }
            }
            else
            {
                compare.Add((null, entry));
            }
        }
        compare.AddRange(previous.Where(p => before.ContainsKey(p.Path)).Select(p => ((GitDiffEntry?)p, (GitDiffEntry?)null)));
        return (same, compare);
    }

    /// <summary>
    /// Two patches of one file make the same change when they are equal without what a moved base changes anyway: the
    /// "index" line (blob ids) and the line numbers in hunk headers. A missing patch (too large) never matches.
    /// </summary>
    public static bool SamePatch(string? previous, string? current) =>
        previous is not null && current is not null && string.Equals(Normalize(previous), Normalize(current), StringComparison.Ordinal);

    internal static string Normalize(string patch)
    {
        var text = new StringBuilder(patch.Length);
        foreach (var line in patch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("index ", StringComparison.Ordinal))
            {
                continue;
            }
            text.Append(HunkHeader().Replace(line, "@@ @@")).Append('\n');
        }
        return text.ToString();
    }

    [GeneratedRegex(@"^@@ -\d+(,\d+)? \+\d+(,\d+)? @@")]
    private static partial Regex HunkHeader();
}
