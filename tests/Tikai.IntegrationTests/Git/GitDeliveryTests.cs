using System.Text;
using Tikai.Core.Git;
using Tikai.Git;

namespace Tikai.IntegrationTests.Git;

/// <summary>
/// What delivering an approved change needs from git, with real git: merging without a worktree, committing a tree,
/// pushing without force (to a bare repository over file:// and over smart HTTP), and connecting a remote.
/// </summary>
public sealed class GitDeliveryTests : IDisposable
{
    private const string Branch = "tikai/HUB-1-abcdef12";
    private static readonly GitIdentity _approver = new("Ada Lovelace", "ada@example.org");

    private readonly TempDirectory _temp = new();
    private readonly GitService _git = TestGit.CreateService();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task A_clean_merge_gives_a_tree_and_a_conflict_its_files_without_touching_any_ref()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = await TestGit.InitBareAsync(_temp.Combine("hub.git"));
        var start = await TestGit.CommitAsync(repo, "main", "Start", ("a.txt", "a\n"), ("b.txt", "b\n"));
        await TestGit.RunAsync(repo, "update-ref", "refs/heads/" + Branch, start);
        var work = await TestGit.CommitAsync(repo, Branch, "Work", ("a.txt", "agent\n"), ("b.txt", "b\n"), ("new.txt", "new\n"));
        var moved = await TestGit.CommitAsync(repo, "main", "Other change", ("a.txt", "a\n"), ("b.txt", "people\n"));

        var clean = await _git.MergeTreeAsync(repo, moved, work, ct);
        Assert.True(clean.Clean);
        Assert.NotEqual(await _git.ResolveTreeAsync(repo, moved, ct), clean.Tree);
        Assert.Null(await _git.ResolveTreeAsync(repo, new string('1', 40), ct));
        var commit = await _git.CommitTreeAsync(repo, clean.Tree, [moved], "HUB-1: Work\n\nTicket: HUB-1\n", GitIdentity.Agent, _approver, ct);
        Assert.Equal("agent\n", Encoding.UTF8.GetString((await _git.ReadFileAsync(repo, commit, "a.txt", ct))!));
        Assert.Equal("people\n", Encoding.UTF8.GetString((await _git.ReadFileAsync(repo, commit, "b.txt", ct))!));
        Assert.Equal(
            "Claude Code (Tikai) <claude-code@agents.tikai.invalid>|Ada Lovelace <ada@example.org>|" + moved,
            await TestGit.RunAsync(repo, "log", "-1", "--format=%an <%ae>|%cn <%ce>|%P", commit));
        Assert.Equal("HUB-1: Work\n\nTicket: HUB-1", await TestGit.RunAsync(repo, "log", "-1", "--format=%B", commit));
        Assert.Equal(moved, await TestGit.TryResolveAsync(repo, "refs/heads/main"));

        var both = await TestGit.CommitAsync(repo, "main", "Same line", ("a.txt", "people\n"), ("b.txt", "people\n"));
        var conflict = await _git.MergeTreeAsync(repo, both, work, ct);
        Assert.False(conflict.Clean);
        Assert.Equal(["a.txt"], conflict.Conflicts);
        Assert.Equal(both, await TestGit.TryResolveAsync(repo, "refs/heads/main"));

        // A ref moves only from the commit it was read at.
        await Assert.ThrowsAsync<GitException>(() => _git.UpdateRefAsync(repo, "refs/heads/main", commit, moved, ct));
        await _git.UpdateRefAsync(repo, "refs/heads/main", commit, both, ct);
        await _git.DeleteRefAsync(repo, "refs/heads/" + Branch, ct);
        await _git.DeleteRefAsync(repo, "refs/heads/" + Branch, ct); // gone already: nothing to do
        Assert.Null(await TestGit.TryResolveAsync(repo, "refs/heads/" + Branch));
    }

    [Fact]
    public async Task A_push_lands_without_force_and_one_the_remote_moved_ahead_of_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var remote = await TestGit.InitBareAsync(_temp.Combine("remote.git"));
        await TestGit.CommitAsync(remote, "main", "Start", ("a.txt", "a\n"));
        var url = GitRemoteUrl.Parse(TestGit.FileUrl(remote));
        var mirror = _temp.Combine("mirror.git");
        await _git.CreateMirrorAsync(mirror, url, null, null, ct);
        var start = (await _git.ResolveCommitAsync(mirror, "refs/heads/main", ct))!;
        var tree = await TestGit.RunAsync(mirror, "rev-parse", start + "^{tree}");
        var next = await _git.CommitTreeAsync(mirror, tree, [start], "Next\n", GitIdentity.Agent, _approver, ct);

        Assert.Equal(PushResult.Pushed, await _git.PushAsync(mirror, url, null, next, "refs/heads/main", ct));
        Assert.Equal(next, await TestGit.TryResolveAsync(remote, "refs/heads/main"));
        Assert.Equal(PushResult.Pushed, await _git.PushAsync(mirror, url, null, next, "refs/heads/" + Branch, ct));
        Assert.Equal(next, await TestGit.TryResolveAsync(remote, "refs/heads/" + Branch));

        // Someone pushes in between: the hub's next commit is not a fast-forward any more.
        await TestGit.CommitAsync(remote, "main", "Someone else", ("a.txt", "x\n"));
        var late = await _git.CommitTreeAsync(mirror, tree, [next], "Late\n", GitIdentity.Agent, _approver, ct);
        Assert.Equal(PushResult.Rejected, await _git.PushAsync(mirror, url, null, late, "refs/heads/main", ct));
        Assert.NotEqual(late, await TestGit.TryResolveAsync(remote, "refs/heads/main"));
    }

    [Fact]
    public async Task A_push_over_http_sends_the_token_and_a_refused_login_is_an_error_for_people()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = _temp.Combine("served");
        var remote = await TestGit.InitBareAsync(Path.Combine(root, "remote.git"));
        var start = await TestGit.CommitAsync(remote, "main", "Start", ("a.txt", "a\n"));
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:tok_123456"));
        await using var server = await GitHttpServer.StartAsync(root, expected);
        var url = GitRemoteUrl.Parse($"{server.BaseUrl}/remote.git");
        var credential = new GitCredential.HttpsToken("x-access-token", "tok_123456");
        var mirror = _temp.Combine("mirror.git");
        await _git.CreateMirrorAsync(mirror, url, credential, null, ct);
        var tree = await TestGit.RunAsync(mirror, "rev-parse", start + "^{tree}");
        var next = await _git.CommitTreeAsync(mirror, tree, [start], "Next\n", GitIdentity.Agent, _approver, ct);

        Assert.Equal(PushResult.Pushed, await _git.PushAsync(mirror, url, credential, next, "refs/heads/main", ct));
        Assert.Equal(next, await TestGit.TryResolveAsync(remote, "refs/heads/main"));

        var refused = await Assert.ThrowsAsync<GitException>(() => _git.PushAsync(mirror, url, new GitCredential.HttpsToken("x", "wrong"), next, "refs/heads/other", ct));
        Assert.Contains("git push failed", refused.Message, StringComparison.Ordinal);
        Assert.Null(await TestGit.TryResolveAsync(remote, "refs/heads/other"));
    }

    [Fact]
    public async Task A_local_repository_connects_to_an_empty_remote_and_pushes_its_default_branch()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = _temp.Combine("hub.git");
        await _git.InitBareAsync(hub, "main", ct);
        var head = await TestGit.CommitAsync(hub, "main", "Merged work", ("a.txt", "a\n"));
        await TestGit.RunAsync(hub, "update-ref", "refs/heads/" + Branch, head);
        var remote = await TestGit.InitBareAsync(_temp.Combine("remote.git"));
        var url = GitRemoteUrl.Parse(TestGit.FileUrl(remote));

        var result = await _git.ConnectRemoteAsync(hub, url, null, "main", ct);

        Assert.True(result.Pushed);
        Assert.Equal(head, await TestGit.TryResolveAsync(remote, "refs/heads/main"));
        Assert.Null(await TestGit.TryResolveAsync(remote, "refs/heads/" + Branch)); // only the default branch
        Assert.Equal(head, await TestGit.TryResolveAsync(hub, "refs/remotes/origin/main"));
        // From now on it fetches like any mirror.
        var later = await TestGit.CommitAsync(remote, "main", "On the remote", ("a.txt", "b\n"));
        Assert.Equal(DefaultBranchSync.FastForwarded, (await _git.FetchAsync(hub, url, null, "main", ct)).DefaultBranch);
        Assert.Equal(later, await TestGit.TryResolveAsync(hub, "refs/heads/main"));
        // Connecting again to the same remote, now level with the hub, pushes nothing.
        Assert.False((await _git.ConnectRemoteAsync(hub, url, null, "main", ct)).Pushed);
    }

    [Fact]
    public async Task A_remote_with_its_own_history_is_refused_and_the_repository_stays_as_it_was()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = _temp.Combine("hub.git");
        await _git.InitBareAsync(hub, "main", ct);
        var head = await TestGit.CommitAsync(hub, "main", "Merged work", ("a.txt", "a\n"));
        var withReadme = await TestGit.InitBareAsync(_temp.Combine("readme.git"));
        await TestGit.CommitAsync(withReadme, "main", "Initial commit", ("README.md", "# New\n"));
        var otherBranch = await TestGit.InitBareAsync(_temp.Combine("other.git"));
        await TestGit.CommitAsync(otherBranch, "master", "Initial commit", ("README.md", "# New\n"));

        var diverged = await Assert.ThrowsAsync<GitException>(() => _git.ConnectRemoteAsync(hub, GitRemoteUrl.Parse(TestGit.FileUrl(withReadme)), null, "main", ct));
        Assert.Contains("Connect an empty repository", diverged.Message, StringComparison.Ordinal);
        var missing = await Assert.ThrowsAsync<GitException>(() => _git.ConnectRemoteAsync(hub, GitRemoteUrl.Parse(TestGit.FileUrl(otherBranch)), null, "main", ct));
        Assert.Contains("no \"main\"", missing.Message, StringComparison.Ordinal);
        var unreachable = await Assert.ThrowsAsync<GitException>(() => _git.ConnectRemoteAsync(hub, GitRemoteUrl.Parse(TestGit.FileUrl(_temp.Combine("nowhere.git"))), null, "main", ct));
        Assert.Contains("git fetch failed", unreachable.Message, StringComparison.Ordinal);

        Assert.Equal(head, await TestGit.TryResolveAsync(hub, "refs/heads/main"));
        Assert.Equal("", await TestGit.RunAsync(hub, "for-each-ref", "refs/remotes/"));
        Assert.Equal("", await TestGit.RunAsync(hub, "config", "--get-regexp", "^remote\\.").ContinueWith(t => t.IsFaulted ? "" : t.Result, ct));
        Assert.Equal("Initial commit", await TestGit.RunAsync(withReadme, "log", "-1", "--format=%s", "main"));
    }
}
