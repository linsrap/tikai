using Tikai.Core.Git;
using Tikai.Core.Reviews;
using Tikai.Review;

namespace Tikai.Api.Reviews;

/// <summary>
/// A round's diff as the reviewer sees it: the full change (base → head), or the interdiff since the round before:
/// head → head when the base stayed, and with a changed base the files whose change differs, each with its full change.
/// </summary>
/// <param name="Kind">How the interdiff was computed; <see cref="InterdiffKind.None"/> for the full view and the first round.</param>
/// <param name="FromSha">The commit of the diff's old side.</param>
/// <param name="ToSha">The commit of the diff's new side (always the round's head).</param>
/// <param name="Dropped">With a changed base: files the round before changed and this one no longer does.</param>
internal sealed record RoundDiff(ReviewView View, InterdiffKind Kind, string FromSha, string ToSha, GitDiff Diff, IReadOnlyList<string> Dropped);

internal static class ReviewDiffs
{
    /// <summary>With a changed base, files beyond this many patch comparisons are listed as changed without one.</summary>
    public const int MaxPatchComparisons = 400;

    private const long ComparedPatchBytes = 1024 * 1024;

    public static async Task<RoundDiff> ForAsync(
        IGitService git, string mirror, ReviewRound round, ReviewRound? previous, ReviewView view, int maxFiles, CancellationToken cancellationToken)
    {
        var kind = view == ReviewView.Full ? InterdiffKind.None : Interdiff.Kind(previous?.BaseSha, round.BaseSha);
        switch (kind)
        {
            case InterdiffKind.Heads:
                return new RoundDiff(ReviewView.Interdiff, kind, previous!.HeadSha, round.HeadSha,
                    await git.DiffAsync(mirror, previous.HeadSha, round.HeadSha, maxFiles, cancellationToken), []);
            case InterdiffKind.PerFile:
                return await PerFileAsync(git, mirror, round, previous!, maxFiles, cancellationToken);
            default:
                // The first round's interdiff is its full change.
                return new RoundDiff(view, kind, round.BaseSha, round.HeadSha,
                    await git.DiffAsync(mirror, round.BaseSha, round.HeadSha, maxFiles, cancellationToken), []);
        }
    }

    private static async Task<RoundDiff> PerFileAsync(
        IGitService git, string mirror, ReviewRound round, ReviewRound previous, int maxFiles, CancellationToken cancellationToken)
    {
        var before = await git.DiffAsync(mirror, previous.BaseSha, previous.HeadSha, int.MaxValue, cancellationToken);
        var now = await git.DiffAsync(mirror, round.BaseSha, round.HeadSha, int.MaxValue, cancellationToken);
        var (_, toCompare) = Interdiff.Pair(before.Files, now.Files);

        var changed = new HashSet<string>(StringComparer.Ordinal);
        var dropped = new List<string>();
        var compared = 0;
        foreach (var (earlier, current) in toCompare)
        {
            if (current is null)
            {
                dropped.Add(earlier!.Path);
                continue;
            }
            // Binary files and new files have no patch to compare; files past the limit count as changed.
            if (earlier is { Additions: not null } && current.Additions is not null && compared++ < MaxPatchComparisons)
            {
                var old = await git.PatchAsync(mirror, previous.BaseSha, previous.HeadSha, earlier.Path, OldPath(earlier), ComparedPatchBytes, cancellationToken);
                var @new = await git.PatchAsync(mirror, round.BaseSha, round.HeadSha, current.Path, OldPath(current), ComparedPatchBytes, cancellationToken);
                if (Interdiff.SamePatch(old, @new))
                {
                    continue;
                }
            }
            changed.Add(current.Path);
        }
        var files = now.Files.Where(f => changed.Contains(f.Path)).ToList();
        return new RoundDiff(ReviewView.Interdiff, InterdiffKind.PerFile, round.BaseSha, round.HeadSha,
            new GitDiff([.. files.Take(maxFiles)], files.Count > maxFiles), dropped);
    }

    private static string? OldPath(GitDiffEntry entry) => entry.Kind == GitChangeKind.Renamed ? entry.OldPath : null;
}
