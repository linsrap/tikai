using Tikai.Core.Git;
using Tikai.Git;

namespace Tikai.IntegrationTests.Git;

/// <summary>
/// Taking submitted work into a mirror and diffing it, with real git: a "run clone" (a bare clone of the mirror here)
/// makes the bundle, as the bundle container does in a run.
/// </summary>
public sealed class GitReviewTests : IDisposable
{
    private const string Branch = "tikai/HUB-1-abcdef12";
    private const string Moving = "a file that moves\nwith a few lines\nso that git sees the rename\n";

    private readonly TempDirectory _temp = new();
    private readonly GitService _git = TestGit.CreateService();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task An_imported_bundle_lands_on_the_target_refs_and_its_diff_and_blobs_can_be_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mirror, clone, start) = await SetUpAsync(("change.txt", "first\n"), ("gone.txt", "bye\n"), ("old.txt", Moving));
        var head = await TestGit.CommitAsync(clone, Branch, "Work",
            ("change.txt", "first\nsecond\n"), ("new.txt", Moving), ("added.txt", "new\n"), ("image.png", "\u0089PNG\0\u0001"));
        var bundle = await BundleAsync(clone, start);

        var imported = await _git.ImportBundleAsync(mirror, bundle, Branch, ["refs/heads/" + Branch, "refs/tikai/rounds/x/1"], ct);

        Assert.Equal(head, imported);
        Assert.Equal(head, await TestGit.TryResolveAsync(mirror, "refs/heads/" + Branch));
        Assert.Equal(head, await TestGit.TryResolveAsync(mirror, "refs/tikai/rounds/x/1"));
        Assert.Equal("", await TestGit.RunAsync(mirror, "for-each-ref", "refs/tikai/incoming/"));
        Assert.Equal(start, await _git.MergeBaseAsync(mirror, "refs/heads/main", head, ct));

        var diff = await _git.DiffAsync(mirror, start, head, 100, ct);
        Assert.False(diff.Truncated);
        var files = diff.Files.ToDictionary(f => f.Path);
        Assert.Equal(["added.txt", "change.txt", "gone.txt", "image.png", "new.txt"], files.Keys.Order());
        Assert.Equal(new GitDiffEntry(GitChangeKind.Modified, "change.txt", "change.txt", files["change.txt"].OldBlob, files["change.txt"].NewBlob, "100644", "100644", 1, 0),
            files["change.txt"]);
        Assert.Equal((GitChangeKind.Deleted, "gone.txt", (string?)null, "000000"), (files["gone.txt"].Kind, files["gone.txt"].OldPath, files["gone.txt"].NewBlob, files["gone.txt"].NewMode));
        Assert.Equal((GitChangeKind.Renamed, "old.txt", "new.txt", 0, 0), (files["new.txt"].Kind, files["new.txt"].OldPath, files["new.txt"].NewPath, files["new.txt"].Additions, files["new.txt"].Deletions));
        Assert.Equal((GitChangeKind.Added, (string?)null, (string?)null), (files["added.txt"].Kind, files["added.txt"].OldPath, files["added.txt"].OldBlob));
        Assert.Null(files["image.png"].Additions); // binary

        var first = await _git.DiffAsync(mirror, start, head, 2, ct);
        Assert.True(first.Truncated);
        Assert.Equal(2, first.Files.Count);

        var blob = await _git.ReadBlobAsync(mirror, files["change.txt"].NewBlob!, 1024, ct);
        Assert.Equal("first\nsecond\n"u8.ToArray(), blob!.Content);
        Assert.Equal(13, blob.Size);
        var cut = await _git.ReadBlobAsync(mirror, files["change.txt"].NewBlob!, 5, ct);
        Assert.Equal((13L, (byte[]?)null), (cut!.Size, cut.Content));
        Assert.Null(await _git.ReadBlobAsync(mirror, new string('1', 40), 1024, ct));
        // A commit id is not a blob.
        Assert.Null(await _git.ReadBlobAsync(mirror, head, 1024, ct));
    }

    [Fact]
    public async Task A_rewritten_branch_is_taken_and_the_earlier_round_ref_keeps_its_commit()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mirror, clone, start) = await SetUpAsync(("a.txt", "a\n"));
        var first = await TestGit.CommitAsync(clone, Branch, "First", ("a.txt", "a\n"), ("b.txt", "b\n"));
        await _git.ImportBundleAsync(mirror, await BundleAsync(clone, start), Branch, ["refs/heads/" + Branch, "refs/tikai/rounds/x/1"], ct);

        // The agent rewrote its branch: a new commit on the start, not on top of the first.
        await TestGit.RunAsync(clone, "update-ref", "refs/heads/" + Branch, start);
        var second = await TestGit.CommitAsync(clone, Branch, "Second", ("a.txt", "a\n"), ("c.txt", "c\n"));
        await _git.ImportBundleAsync(mirror, await BundleAsync(clone, start, "second"), Branch, ["refs/heads/" + Branch, "refs/tikai/rounds/x/2"], ct);

        Assert.Equal(second, await TestGit.TryResolveAsync(mirror, "refs/heads/" + Branch));
        Assert.Equal(first, await TestGit.TryResolveAsync(mirror, "refs/tikai/rounds/x/1"));
        Assert.Equal(second, await TestGit.TryResolveAsync(mirror, "refs/tikai/rounds/x/2"));
    }

    [Fact]
    public async Task A_bundle_whose_prerequisites_the_mirror_lacks_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mirror, clone, _) = await SetUpAsync(("a.txt", "a\n"));
        var only = await TestGit.CommitAsync(clone, Branch, "Only in the clone", ("a.txt", "x\n"));
        await TestGit.CommitAsync(clone, Branch, "On top", ("a.txt", "y\n"));
        var bundle = await BundleAsync(clone, only);

        var error = await Assert.ThrowsAsync<GitException>(() => _git.ImportBundleAsync(mirror, bundle, Branch, ["refs/heads/" + Branch], ct));

        Assert.Contains("prerequisite", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await TestGit.TryResolveAsync(mirror, "refs/heads/" + Branch));
    }

    [Fact]
    public async Task A_bundle_with_a_malformed_object_is_refused_by_fsck_and_leaves_no_ref()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mirror, clone, start) = await SetUpAsync(("a.txt", "a\n"));
        var tree = await TestGit.RunAsync(clone, "rev-parse", start + "^{tree}");
        // An author line without an email: git itself never writes this, fsck rejects it.
        var bad = await HashLiterallyAsync(clone, $"tree {tree}\nparent {start}\nauthor Mallory 1700000000 +0000\ncommitter Mallory 1700000000 +0000\n\nBad\n");
        await TestGit.RunAsync(clone, "update-ref", "refs/heads/" + Branch, bad);
        var bundle = await BundleAsync(clone, start);

        var error = await Assert.ThrowsAsync<GitException>(() => _git.ImportBundleAsync(mirror, bundle, Branch, ["refs/heads/" + Branch], ct));

        Assert.Contains("fsck", error.Message, StringComparison.Ordinal);
        Assert.Null(await TestGit.TryResolveAsync(mirror, "refs/heads/" + Branch));
        Assert.Equal("", await TestGit.RunAsync(mirror, "for-each-ref", "refs/tikai/incoming/"));
    }

    [Fact]
    public async Task Histories_without_a_common_commit_have_no_merge_base()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = _temp.Combine("hub.git");
        await _git.InitBareAsync(path, "main", ct);
        var other = await TestGit.CommitAsync(path, "unrelated", "Unrelated", ("x.txt", "x"));

        Assert.Null(await _git.MergeBaseAsync(path, "refs/heads/main", other, ct));
    }

    /// <summary>A mirror with the files on main, and a bare clone of it standing in for a run clone.</summary>
    private async Task<(string Mirror, string Clone, string Start)> SetUpAsync(params (string Name, string Content)[] files)
    {
        var mirror = _temp.Combine("hub.git");
        await _git.InitBareAsync(mirror, "main", TestContext.Current.CancellationToken);
        var start = await TestGit.CommitAsync(mirror, "main", "Seed", files);
        var clone = _temp.Combine("clone.git");
        await TestGit.RunAsync(null, "clone", "--quiet", "--bare", mirror, clone);
        await TestGit.RunAsync(clone, "update-ref", "refs/heads/" + Branch, start);
        return (mirror, clone, start);
    }

    /// <summary>As the bundle container makes it: the run branch, without what the run started from.</summary>
    private async Task<string> BundleAsync(string clone, string start, string name = "review")
    {
        var bundle = _temp.Combine(name + ".bundle");
        await TestGit.RunAsync(clone, "bundle", "create", "--quiet", bundle, "refs/heads/" + Branch, "^" + start);
        return bundle;
    }

    private static async Task<string> HashLiterallyAsync(string gitDir, string content)
    {
        var file = Path.Combine(Path.GetDirectoryName(gitDir)!, "object.txt");
        await File.WriteAllTextAsync(file, content);
        return await TestGit.RunAsync(gitDir, "hash-object", "-t", "commit", "--literally", "-w", file);
    }
}
