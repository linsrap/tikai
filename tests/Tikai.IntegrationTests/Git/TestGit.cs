using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tikai.Git;
using Tikai.Git.Processes;

namespace Tikai.IntegrationTests.Git;

/// <summary>Builds test repositories with git plumbing (no working tree) and creates GitService instances.</summary>
internal static class TestGit
{
    private static readonly ProcessRunner _runner = new();

    public static GitService CreateService(string protocols = "https,ssh,http,file", IProcessRunner? runner = null, string? tempPath = null) =>
        new(runner ?? _runner, Options.Create(new GitOptions { AllowedProtocols = protocols, TempPath = tempPath }), NullLogger<GitService>.Instance);

    public static string FileUrl(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    public static async Task<string> RunAsync(string? gitDir, params string[] arguments)
    {
        string[] full = gitDir is null ? arguments : ["--git-dir", gitDir, .. arguments];
        var result = await _runner.RunAsync(new ProcessRequest("git", full, TimeSpan.FromMinutes(1), new Dictionary<string, string?>
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["GIT_DIR"] = null,
            ["GIT_AUTHOR_NAME"] = "Test",
            ["GIT_AUTHOR_EMAIL"] = "test@example.org",
            ["GIT_COMMITTER_NAME"] = "Test",
            ["GIT_COMMITTER_EMAIL"] = "test@example.org",
        }));
        return result.ExitCode == 0
            ? result.OutputText.Trim()
            : throw new InvalidOperationException($"git {string.Join(' ', full)} failed: {result.StandardError}");
    }

    /// <summary>A bare repository whose HEAD points at <paramref name="defaultBranch"/>, without commits.</summary>
    public static async Task<string> InitBareAsync(string path, string defaultBranch = "main")
    {
        await RunAsync(null, "init", "--quiet", "--bare", $"--initial-branch={defaultBranch}", path);
        return path;
    }

    /// <summary>Adds a commit with the given top-level files on top of the branch (or as its first commit).</summary>
    public static async Task<string> CommitAsync(string gitDir, string branch, string message, params (string Name, string Content)[] files)
    {
        var entries = new StringBuilder();
        foreach (var (name, content) in files)
        {
            var blob = await HashAsync(gitDir, content);
            entries.Append(System.Globalization.CultureInfo.InvariantCulture, $"100644 blob {blob}\t{name}\n");
        }
        var tree = await RunWithInputAsync(gitDir, entries.ToString(), "mktree");
        var parent = await TryResolveAsync(gitDir, "refs/heads/" + branch);
        string[] commitArgs = parent is null ? ["commit-tree", tree, "-m", message] : ["commit-tree", tree, "-p", parent, "-m", message];
        var commit = await RunAsync(gitDir, commitArgs);
        await RunAsync(gitDir, "update-ref", "refs/heads/" + branch, commit);
        return commit;
    }

    public static async Task<string?> TryResolveAsync(string gitDir, string revision)
    {
        var result = await _runner.RunAsync(new ProcessRequest("git", ["--git-dir", gitDir, "rev-parse", "--verify", "--quiet", revision], TimeSpan.FromMinutes(1)));
        return result.ExitCode == 0 ? result.OutputText.Trim() : null;
    }

    private static async Task<string> HashAsync(string gitDir, string content) =>
        await RunWithInputAsync(gitDir, content, "hash-object", "-w", "--stdin");

    private static async Task<string> RunWithInputAsync(string gitDir, string input, params string[] arguments)
    {
        var result = await _runner.RunAsync(new ProcessRequest(
            "git", ["--git-dir", gitDir, .. arguments], TimeSpan.FromMinutes(1), StandardInput: Encoding.UTF8.GetBytes(input)));
        return result.ExitCode == 0
            ? result.OutputText.Trim()
            : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.StandardError}");
    }
}

/// <summary>A unique temp directory, deleted (including git's read-only files) on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tikai-tests", Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(Path, recursive: true);
    }
}

/// <summary>Passes calls through and keeps every request, to check what git was given.</summary>
internal sealed class RecordingRunner(IProcessRunner inner) : IProcessRunner
{
    public ConcurrentQueue<ProcessRequest> Requests { get; } = new();

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Enqueue(request);
        return inner.RunAsync(request, cancellationToken);
    }
}
