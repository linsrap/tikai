using System.Text;
using Tikai.Core.Git;
using Tikai.Git;

namespace Tikai.IntegrationTests.Git;

/// <summary>GitService against real git: a local bare repo, and a "remote" that is a second bare repo over file://.</summary>
public sealed class GitServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly GitService _git = TestGit.CreateService();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Init_creates_a_bare_repo_whose_default_branch_has_one_empty_commit()
    {
        var path = _temp.Combine("local.git");

        await _git.InitBareAsync(path, "main", TestContext.Current.CancellationToken);

        var branch = Assert.Single(await _git.ListBranchesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(new GitBranch("main", branch.Sha, IsRemote: false), branch);
        Assert.Equal("true", await TestGit.RunAsync(path, "config", "core.bare"));
        Assert.Equal("Initial commit", await TestGit.RunAsync(path, "log", "-1", "--format=%s", "main"));
        Assert.Equal("Tikai <tikai@localhost>", await TestGit.RunAsync(path, "log", "-1", "--format=%an <%ae>", "main"));
        Assert.Equal("", await TestGit.RunAsync(path, "ls-tree", "main"));
    }

    [Fact]
    public async Task A_mirror_gets_the_remote_branches_and_its_default_branch()
    {
        var ct = TestContext.Current.CancellationToken;
        var remote = await TestGit.InitBareAsync(_temp.Combine("remote.git"), "trunk");
        var head = await TestGit.CommitAsync(remote, "trunk", "First", ("README.md", "hello\n"));
        var feature = await TestGit.CommitAsync(remote, "feature", "Feature", ("feature.txt", "wip\n"));
        var mirror = _temp.Combine("repos", "mirror.git");

        var result = await _git.CreateMirrorAsync(mirror, GitRemoteUrl.Parse(TestGit.FileUrl(remote)), null, null, ct);

        Assert.Equal(new MirrorResult("trunk", IsEmpty: false), result);
        Assert.Equal(
            [new GitBranch("trunk", head, false), new GitBranch("feature", feature, true), new GitBranch("trunk", head, true)],
            await _git.ListBranchesAsync(mirror, ct));
        Assert.Equal("refs/heads/trunk", await TestGit.RunAsync(mirror, "symbolic-ref", "HEAD"));
        Assert.Equal("hello\n", Encoding.UTF8.GetString((await _git.ReadFileAsync(mirror, head, "README.md", ct))!));
        Assert.Null(await _git.ReadFileAsync(mirror, head, "missing.txt", ct));
        Assert.Null(await _git.ReadFileAsync(mirror, head, "feature.txt", ct));
        Assert.Equal(head, await _git.ResolveCommitAsync(mirror, "trunk", ct));
        Assert.Null(await _git.ResolveCommitAsync(mirror, "no-such-branch", ct));
        Assert.Equal(["mirror.git"], Directory.GetFileSystemEntries(_temp.Combine("repos")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Fetch_brings_new_commits_and_branches_prunes_deleted_ones_and_keeps_tikai_branches()
    {
        var ct = TestContext.Current.CancellationToken;
        var remote = await TestGit.InitBareAsync(_temp.Combine("remote.git"));
        await TestGit.CommitAsync(remote, "main", "First", ("a.txt", "1\n"));
        await TestGit.CommitAsync(remote, "old-feature", "Old", ("b.txt", "1\n"));
        var url = GitRemoteUrl.Parse(TestGit.FileUrl(remote));
        var mirror = _temp.Combine("mirror.git");
        await _git.CreateMirrorAsync(mirror, url, null, "main", ct);
        var runBranch = await TestGit.CommitAsync(mirror, "tikai/HUB-1-0192f0c4", "Agent work", ("c.txt", "agent\n"));

        var second = await TestGit.CommitAsync(remote, "main", "Second", ("a.txt", "2\n"));
        var newBranch = await TestGit.CommitAsync(remote, "new-feature", "New", ("d.txt", "1\n"));
        await TestGit.RunAsync(remote, "update-ref", "-d", "refs/heads/old-feature");

        var result = await _git.FetchAsync(mirror, url, null, "main", ct);

        Assert.Equal(DefaultBranchSync.FastForwarded, result.DefaultBranch);
        Assert.Equal(
            [
                new GitBranch("main", second, false),
                new GitBranch("tikai/HUB-1-0192f0c4", runBranch, false),
                new GitBranch("main", second, true),
                new GitBranch("new-feature", newBranch, true),
            ],
            await _git.ListBranchesAsync(mirror, ct));

        Assert.Equal(DefaultBranchSync.UpToDate, (await _git.FetchAsync(mirror, url, null, "main", ct)).DefaultBranch);
    }

    [Fact]
    public async Task Fetch_never_moves_a_default_branch_that_is_ahead_or_diverged()
    {
        var ct = TestContext.Current.CancellationToken;
        var remote = await TestGit.InitBareAsync(_temp.Combine("remote.git"));
        await TestGit.CommitAsync(remote, "main", "First", ("a.txt", "1\n"));
        var url = GitRemoteUrl.Parse(TestGit.FileUrl(remote));
        var mirror = _temp.Combine("mirror.git");
        await _git.CreateMirrorAsync(mirror, url, null, null, ct);

        // A merge on the hub that is not pushed yet (step 16).
        var merged = await TestGit.CommitAsync(mirror, "main", "Merge HUB-1", ("a.txt", "merged\n"));
        Assert.Equal(DefaultBranchSync.LocalAhead, (await _git.FetchAsync(mirror, url, null, "main", ct)).DefaultBranch);
        Assert.Equal(merged, await _git.ResolveCommitAsync(mirror, "main", ct));

        // Someone else pushed meanwhile.
        var theirs = await TestGit.CommitAsync(remote, "main", "Their change", ("b.txt", "1\n"));
        Assert.Equal(DefaultBranchSync.Diverged, (await _git.FetchAsync(mirror, url, null, "main", ct)).DefaultBranch);
        Assert.Equal(merged, await _git.ResolveCommitAsync(mirror, "main", ct));
        Assert.Equal(theirs, await _git.ResolveCommitAsync(mirror, "refs/remotes/origin/main", ct));
    }

    [Fact]
    public async Task An_empty_remote_can_be_mirrored_and_its_first_commit_fetched()
    {
        var ct = TestContext.Current.CancellationToken;
        var remote = await TestGit.InitBareAsync(_temp.Combine("remote.git"), "develop");
        var url = GitRemoteUrl.Parse(TestGit.FileUrl(remote));
        var mirror = _temp.Combine("mirror.git");

        var result = await _git.CreateMirrorAsync(mirror, url, null, null, ct);

        Assert.True(result.IsEmpty);
        Assert.Empty(await _git.ListBranchesAsync(mirror, ct));
        Assert.Equal(DefaultBranchSync.MissingOnRemote, (await _git.FetchAsync(mirror, url, null, result.DefaultBranch, ct)).DefaultBranch);

        var first = await TestGit.CommitAsync(remote, result.DefaultBranch, "First", ("a.txt", "1\n"));
        Assert.Equal(DefaultBranchSync.FastForwarded, (await _git.FetchAsync(mirror, url, null, result.DefaultBranch, ct)).DefaultBranch);
        Assert.Equal(first, await _git.ResolveCommitAsync(mirror, result.DefaultBranch, ct));
    }

    [Fact]
    public async Task A_failed_mirror_leaves_nothing_behind()
    {
        var ct = TestContext.Current.CancellationToken;
        var remote = await TestGit.InitBareAsync(_temp.Combine("remote.git"));
        await TestGit.CommitAsync(remote, "main", "First", ("a.txt", "1\n"));
        var parent = _temp.Combine("repos");

        var missingBranch = await Assert.ThrowsAsync<GitException>(() =>
            _git.CreateMirrorAsync(Path.Combine(parent, "a.git"), GitRemoteUrl.Parse(TestGit.FileUrl(remote)), null, "release", ct));
        var missingRemote = await Assert.ThrowsAsync<GitException>(() =>
            _git.CreateMirrorAsync(Path.Combine(parent, "b.git"), GitRemoteUrl.Parse(TestGit.FileUrl(_temp.Combine("nope.git"))), null, null, ct));

        Assert.Contains("no branch \"release\"", missingBranch.Message, StringComparison.Ordinal);
        Assert.Contains("git ls-remote failed", missingRemote.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(parent));
    }

    [Fact]
    public async Task Protocols_outside_the_allowlist_are_refused_by_tikai_and_by_git_itself()
    {
        var ct = TestContext.Current.CancellationToken;
        var remote = await TestGit.InitBareAsync(_temp.Combine("remote.git"));
        await TestGit.CommitAsync(remote, "main", "First", ("a.txt", "1\n"));
        var mirror = _temp.Combine("mirror.git");
        await _git.CreateMirrorAsync(mirror, GitRemoteUrl.Parse(TestGit.FileUrl(remote)), null, null, ct);
        var strict = TestGit.CreateService("https,ssh");

        var byTikai = await Assert.ThrowsAsync<GitException>(() =>
            strict.FetchAsync(mirror, GitRemoteUrl.Parse(TestGit.FileUrl(remote)), null, "main", ct));
        // The repository's origin is still file://: git's GIT_ALLOW_PROTOCOL check stops it even if Tikai's is passed.
        var byGit = await Assert.ThrowsAsync<GitException>(() =>
            strict.FetchAsync(mirror, GitRemoteUrl.Parse("https://example.org/repo.git"), null, "main", ct));

        Assert.Contains("not allowed on this hub", byTikai.Message, StringComparison.Ordinal);
        Assert.Contains("transport 'file' not allowed", byGit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bundle_carries_the_default_branch_and_nothing_else()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = _temp.Combine("hub.git");
        await _git.InitBareAsync(path, "main", ct);
        var main = await TestGit.CommitAsync(path, "main", "On main", ("a.txt", "a"));
        var secret = await TestGit.CommitAsync(path, "other-team/secret", "Secret", ("s.txt", "s"));
        var bundle = _temp.Combine("run.bundle");

        await _git.CreateBundleAsync(path, "main", bundle, ct);

        Assert.Equal($"{main} refs/heads/main", await TestGit.RunAsync(null, "bundle", "list-heads", bundle));
        var clone = _temp.Combine("clone.git");
        await TestGit.RunAsync(null, "clone", "--quiet", "--bare", bundle, clone);
        await Assert.ThrowsAsync<InvalidOperationException>(() => TestGit.RunAsync(clone, "cat-file", "-e", secret));
        await Assert.ThrowsAsync<GitException>(() => _git.CreateBundleAsync(path, "empty", _temp.Combine("none.bundle"), ct));
    }

    [Fact]
    public async Task Repositories_with_read_only_object_files_can_be_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = _temp.Combine("local.git");
        await _git.InitBareAsync(path, "main", ct);
        await TestGit.RunAsync(path, "gc", "--quiet"); // writes read-only pack files

        await _git.DeleteRepositoryAsync(path, ct);

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task The_host_git_config_and_environment_do_not_leak_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var recorder = new RecordingRunner(new Tikai.Git.Processes.ProcessRunner());
        var git = TestGit.CreateService(runner: recorder);
        await git.InitBareAsync(_temp.Combine("local.git"), "main", ct);

        var env = recorder.Requests.First().Environment!;
        Assert.Equal("1", env["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
        Assert.True(env.ContainsKey("GIT_ASKPASS") && env["GIT_ASKPASS"] is null, "an inherited askpass helper (VS Code sets one) must be removed");
        Assert.True(env.ContainsKey("GIT_DIR") && env["GIT_DIR"] is null);
        Assert.Equal("credential.helper", env["GIT_CONFIG_KEY_0"]);
        Assert.Equal("", env["GIT_CONFIG_VALUE_0"]);
    }
}
