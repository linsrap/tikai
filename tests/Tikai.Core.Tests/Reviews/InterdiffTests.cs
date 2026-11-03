using Tikai.Core.Git;
using Tikai.Core.Reviews;
using Tikai.Review;

namespace Tikai.Core.Tests.Reviews;

public class InterdiffTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static GitDiffEntry Entry(string path, string? oldBlob, string? newBlob, string? oldPath = null) =>
        new(oldPath is null ? GitChangeKind.Modified : GitChangeKind.Renamed, oldPath ?? path, path, oldBlob, newBlob, "100644", "100644", 1, 1);

    [Fact]
    public void The_first_round_has_no_interdiff_the_same_base_compares_heads_and_a_new_base_compares_per_file()
    {
        Assert.Equal(InterdiffKind.None, Interdiff.Kind(null, A));
        Assert.Equal(InterdiffKind.Heads, Interdiff.Kind(A, A));
        Assert.Equal(InterdiffKind.PerFile, Interdiff.Kind(A, B));
    }

    [Fact]
    public void Files_with_the_same_blobs_are_the_same_change_and_the_rest_is_compared_by_patch()
    {
        var previous = new[] { Entry("same.txt", "1", "2"), Entry("fixed.txt", "1", "2"), Entry("dropped.txt", "1", "2") };
        var current = new[] { Entry("fixed.txt", "1", "3"), Entry("same.txt", "1", "2"), Entry("new.txt", null, "4") };

        var (same, compare) = Interdiff.Pair(previous, current);

        Assert.Equal(["same.txt"], same.Select(e => e.Path));
        Assert.Equal(
            [("fixed.txt", "fixed.txt"), (null, "new.txt"), ("dropped.txt", null)],
            compare.Select(c => (c.Previous?.Path, c.Current?.Path)));
    }

    [Fact]
    public void A_rename_from_somewhere_else_is_compared()
    {
        var (same, compare) = Interdiff.Pair([Entry("b.txt", "1", "2", oldPath: "a.txt")], [Entry("b.txt", "1", "2", oldPath: "c.txt")]);

        Assert.Empty(same);
        Assert.Single(compare);
    }

    [Fact]
    public void Patches_that_differ_only_in_blob_ids_and_line_numbers_are_the_same_change()
    {
        const string before = "diff --git a/x.txt b/x.txt\nindex 1111111..2222222 100644\n--- a/x.txt\n+++ b/x.txt\n@@ -3,2 +3,3 @@ class X\n a\n+b\n c\n";
        const string moved = "diff --git a/x.txt b/x.txt\nindex 3333333..4444444 100644\n--- a/x.txt\n+++ b/x.txt\n@@ -10,2 +10,3 @@ class X\n a\n+b\n c\n";
        const string other = "diff --git a/x.txt b/x.txt\nindex 3333333..4444444 100644\n--- a/x.txt\n+++ b/x.txt\n@@ -10,2 +10,3 @@ class X\n a\n+B\n c\n";

        Assert.True(Interdiff.SamePatch(before, moved));
        Assert.True(Interdiff.SamePatch(before, moved.Replace("\n", "\r\n", StringComparison.Ordinal)));
        Assert.False(Interdiff.SamePatch(before, other));
        Assert.False(Interdiff.SamePatch(null, null));
        Assert.False(Interdiff.SamePatch(before, null));
    }

    [Fact]
    public void Thread_bodies_are_trimmed_and_limited()
    {
        Assert.Equal(("Fix this.", null), ThreadRules.Body("  Fix this.\n"));
        Assert.NotNull(ThreadRules.Body(" ").Error);
        Assert.NotNull(ThreadRules.Body(new string('x', ThreadRules.MaxBodyLength + 1)).Error);
    }

    [Fact]
    public void An_anchor_has_path_side_and_lines_together_or_nothing()
    {
        Assert.Equal((null, null), ThreadRules.Anchor(null, null, null, null));
        Assert.Equal(new ThreadAnchor("a.cs", DiffSide.New, 4, 4), ThreadRules.Anchor("a.cs", DiffSide.New, 4, null).Anchor);
        Assert.Equal(new ThreadAnchor("a.cs", DiffSide.Old, 2, 9), ThreadRules.Anchor("a.cs", DiffSide.Old, 2, 9).Anchor);
        Assert.NotNull(ThreadRules.Anchor("a.cs", null, 1, 1).Error);
        Assert.NotNull(ThreadRules.Anchor(null, DiffSide.New, 1, 1).Error);
        Assert.NotNull(ThreadRules.Anchor("a.cs", DiffSide.New, 0, 1).Error);
        Assert.NotNull(ThreadRules.Anchor("a.cs", DiffSide.New, 5, 4).Error);
    }
}
