using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tikai.Core.Git;
using Tikai.Git.Processes;

namespace Tikai.Git;

/// <summary>
/// <see cref="IGitService"/> on the git CLI. Every call runs with an isolated configuration (no system or global
/// git config, no credential helpers, no prompts), so the host's git setup never changes what happens.
/// Credentials go to git through environment variables only: never into arguments, URLs or repository config.
/// </summary>
public sealed partial class GitService(IProcessRunner runner, IOptions<GitOptions> options, ILogger<GitService> logger) : IGitService
{
    /// <summary>SSH host keys of the remote, kept inside the bare repository (trust on first use).</summary>
    public const string KnownHostsFile = "tikai_known_hosts";

    private const string Origin = "origin";
    private const string RemoteRefs = "refs/remotes/origin/";
    private const string LocalRefs = "refs/heads/";

    private static readonly string _nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    // Inherited variables that would point git at another repository, other config, or another way to get credentials.
    private static readonly string[] _clearedVariables =
    [
        "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_COMMON_DIR", "GIT_NAMESPACE", "GIT_CONFIG", "GIT_CONFIG_PARAMETERS", "GIT_ASKPASS", "SSH_ASKPASS",
        "GIT_SSH", "GIT_SSH_COMMAND", "GIT_SSH_VARIANT", "GIT_PROXY_COMMAND", "SSH_AUTH_SOCK",
    ];

    private static readonly Dictionary<string, string?> _tikaiIdentity = new()
    {
        ["GIT_AUTHOR_NAME"] = "Tikai",
        ["GIT_AUTHOR_EMAIL"] = "tikai@localhost",
        ["GIT_COMMITTER_NAME"] = "Tikai",
        ["GIT_COMMITTER_EMAIL"] = "tikai@localhost",
    };

    private readonly GitOptions _options = options.Value;

    public async Task InitBareAsync(string path, string defaultBranch, CancellationToken cancellationToken = default)
    {
        CheckBranch(defaultBranch);
        EnsureFree(path);

        await RunAsync(null, ["init", "--quiet", "--bare", $"--initial-branch={defaultBranch}", path], cancellationToken);
        var tree = (await RunAsync(path, ["mktree"], cancellationToken, input: [])).OutputText.Trim();
        var commit = (await RunAsync(path, ["commit-tree", tree, "-m", "Initial commit"], cancellationToken, environment: _tikaiIdentity))
            .OutputText.Trim();
        await RunAsync(path, ["update-ref", LocalRefs + defaultBranch, commit, ""], cancellationToken);
    }

    public async Task<MirrorResult> CreateMirrorAsync(
        string path,
        GitRemoteUrl remote,
        GitCredential? credential,
        string? defaultBranch,
        CancellationToken cancellationToken = default)
    {
        if (defaultBranch is not null)
        {
            CheckBranch(defaultBranch);
        }
        EnsureFree(path);

        // Built next to the target and moved into place at the end, so a failure leaves nothing behind.
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await RunAsync(null, ["init", "--quiet", "--bare", temp], cancellationToken);
            await RunAsync(temp, ["config", $"remote.{Origin}.url", remote.Value], cancellationToken);
            await RunAsync(temp, ["config", $"remote.{Origin}.fetch", $"+{LocalRefs}*:{RemoteRefs}*"], cancellationToken);

            var access = new RemoteAccess(remote, credential, Path.Combine(temp, KnownHostsFile));
            var branch = defaultBranch ?? await DetectDefaultBranchAsync(temp, access, cancellationToken) ?? "main";
            await RunAsync(temp, ["fetch", "--quiet", "--prune", "--no-tags", Origin], cancellationToken, access);

            var remoteHead = await ResolveCommitAsync(temp, RemoteRefs + branch, cancellationToken);
            if (remoteHead is null && (await ListBranchesAsync(temp, cancellationToken)).Count > 0)
            {
                throw new GitException($"The remote has no branch \"{branch}\".");
            }
            await RunAsync(temp, ["symbolic-ref", "HEAD", LocalRefs + branch], cancellationToken);
            if (remoteHead is not null)
            {
                await RunAsync(temp, ["update-ref", LocalRefs + branch, remoteHead, ""], cancellationToken);
            }

            Directory.Move(temp, path);
            return new MirrorResult(branch, IsEmpty: remoteHead is null);
        }
        catch
        {
            await DeleteRepositoryAsync(temp, CancellationToken.None);
            throw;
        }
    }

    public async Task<FetchResult> FetchAsync(
        string path,
        GitRemoteUrl remote,
        GitCredential? credential,
        string defaultBranch,
        CancellationToken cancellationToken = default)
    {
        CheckBranch(defaultBranch);
        var access = new RemoteAccess(remote, credential, Path.Combine(path, KnownHostsFile));
        await RunAsync(path, ["fetch", "--quiet", "--prune", "--no-tags", Origin], cancellationToken, access);

        var remoteHead = await ResolveCommitAsync(path, RemoteRefs + defaultBranch, cancellationToken);
        var localHead = await ResolveCommitAsync(path, LocalRefs + defaultBranch, cancellationToken);
        if (remoteHead is null)
        {
            return new FetchResult(DefaultBranchSync.MissingOnRemote);
        }
        if (localHead == remoteHead)
        {
            return new FetchResult(DefaultBranchSync.UpToDate);
        }
        if (localHead is null || await IsAncestorAsync(path, localHead, remoteHead, cancellationToken))
        {
            // The old value ("" = must not exist) makes update-ref fail if the branch moved in the meantime.
            await RunAsync(path, ["update-ref", LocalRefs + defaultBranch, remoteHead, localHead ?? ""], cancellationToken);
            return new FetchResult(DefaultBranchSync.FastForwarded);
        }
        return new FetchResult(await IsAncestorAsync(path, remoteHead, localHead, cancellationToken)
            ? DefaultBranchSync.LocalAhead
            : DefaultBranchSync.Diverged);
    }

    public async Task<IReadOnlyList<GitBranch>> ListBranchesAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(path, ["for-each-ref", "--format=%(refname)%09%(objectname)", LocalRefs, RemoteRefs], cancellationToken);
        return result.OutputText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts[0] != RemoteRefs + "HEAD")
            .Select(parts => parts[0].StartsWith(RemoteRefs, StringComparison.Ordinal)
                ? new GitBranch(parts[0][RemoteRefs.Length..], parts[1], IsRemote: true)
                : new GitBranch(parts[0][LocalRefs.Length..], parts[1], IsRemote: false))
            .ToList();
    }

    public async Task<string?> ResolveCommitAsync(string path, string revision, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(revision) || revision.StartsWith('-'))
        {
            throw new ArgumentException($"Invalid revision '{revision}'.", nameof(revision));
        }
        var result = await RunAsync(path, ["rev-parse", "--verify", "--quiet", revision + "^{commit}"], cancellationToken, check: false);
        return result.ExitCode == 0 ? result.OutputText.Trim() : null;
    }

    public async Task<byte[]?> ReadFileAsync(string path, string commitSha, string filePath, CancellationToken cancellationToken = default)
    {
        if (!ObjectId().IsMatch(commitSha))
        {
            throw new ArgumentException($"'{commitSha}' is not a full commit id.", nameof(commitSha));
        }
        if (string.IsNullOrEmpty(filePath) || filePath.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid file path '{filePath}'.", nameof(filePath));
        }

        // --batch reads the object name from stdin: no argument parsing, one answer per line.
        var result = await RunAsync(path, ["cat-file", "--batch"], cancellationToken, input: Encoding.UTF8.GetBytes($"{commitSha}:{filePath}\n"));
        var output = result.StandardOutput;
        var headerEnd = Array.IndexOf(output, (byte)'\n');
        var header = Encoding.UTF8.GetString(output, 0, headerEnd < 0 ? output.Length : headerEnd).Split(' ');
        if (header is not [_, "blob", var sizeText])
        {
            return null; // "<name> missing", or a directory
        }
        var size = int.Parse(sizeText, CultureInfo.InvariantCulture);
        return output.AsSpan(headerEnd + 1, size).ToArray();
    }

    public async Task CreateBundleAsync(string path, string branch, string outputFile, CancellationToken cancellationToken = default)
    {
        CheckBranch(branch);
        if (await ResolveCommitAsync(path, LocalRefs + branch, cancellationToken) is null)
        {
            throw new GitException($"The branch \"{branch}\" has no commits yet.");
        }
        await RunAsync(path, ["bundle", "create", "--quiet", Path.GetFullPath(outputFile), LocalRefs + branch], cancellationToken);
    }

    public Task DeleteRepositoryAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (!Directory.Exists(path))
            {
                return;
            }
            // Git makes object files read-only; on Windows that blocks deleting them.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(path, recursive: true);
        }, cancellationToken);

    private async Task<string?> DetectDefaultBranchAsync(string gitDir, RemoteAccess access, CancellationToken cancellationToken)
    {
        // "ref: refs/heads/main<TAB>HEAD"; also reported for an empty remote (unborn HEAD) by current git servers.
        var result = await RunAsync(gitDir, ["ls-remote", "--symref", Origin, "HEAD"], cancellationToken, access);
        var match = SymrefLine().Match(result.OutputText);
        return match.Success && BranchName.ValidationError(match.Groups["branch"].Value) is null ? match.Groups["branch"].Value : null;
    }

    private async Task<bool> IsAncestorAsync(string path, string ancestor, string descendant, CancellationToken cancellationToken)
    {
        var result = await RunAsync(path, ["merge-base", "--is-ancestor", ancestor, descendant], cancellationToken, check: false);
        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw Failure(["merge-base"], result),
        };
    }

    private async Task<ProcessResult> RunAsync(
        string? gitDir,
        string[] arguments,
        CancellationToken cancellationToken,
        RemoteAccess? remote = null,
        byte[]? input = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        bool check = true)
    {
        var env = BaseEnvironment();
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            env[name] = value;
        }
        var config = new List<KeyValuePair<string, string>> { new("credential.helper", "") };
        string? keyFile = null;
        try
        {
            if (remote is not null)
            {
                keyFile = PrepareRemoteAccess(remote, env, config);
            }
            env["GIT_CONFIG_COUNT"] = config.Count.ToString(CultureInfo.InvariantCulture);
            for (var i = 0; i < config.Count; i++)
            {
                env[$"GIT_CONFIG_KEY_{i}"] = config[i].Key;
                env[$"GIT_CONFIG_VALUE_{i}"] = config[i].Value;
            }

            string[] fullArguments = gitDir is null ? arguments : ["--git-dir", gitDir, .. arguments];
            LogCommand(fullArguments);
            var timeout = remote is null ? _options.LocalTimeout : _options.RemoteTimeout;
            ProcessResult result;
            try
            {
                result = await runner.RunAsync(new ProcessRequest(_options.GitPath, fullArguments, timeout, env, StandardInput: input), cancellationToken);
            }
            catch (ProcessTimeoutException ex)
            {
                throw new GitException($"git {arguments[0]} did not finish within {timeout}.", ex);
            }

            if (check && result.ExitCode != 0)
            {
                throw Failure(arguments, result);
            }
            return result;
        }
        finally
        {
            if (keyFile is not null)
            {
                File.Delete(keyFile);
            }
        }
    }

    /// <returns>The temporary key file to delete after the call, if any.</returns>
    private string? PrepareRemoteAccess(RemoteAccess remote, Dictionary<string, string?> env, List<KeyValuePair<string, string>> config)
    {
        if (!_options.IsAllowed(remote.Url.Protocol))
        {
            throw new GitException($"Remotes over {remote.Url.Protocol} are not allowed on this hub (setting Tikai:Git:AllowedProtocols).");
        }

        switch (remote.Credential, remote.Url.Transport)
        {
            case (null, not GitTransport.Ssh):
                return null;
            case (GitCredential.HttpsToken token, GitTransport.Https or GitTransport.Http):
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{token.Username}:{token.Token}"));
                // Scoped to the remote's origin, so the token is never sent to another host (for example after a redirect).
                config.Add(new($"http.{remote.Url.HttpOrigin}.extraHeader", $"Authorization: Basic {basic}"));
                return null;
            case (GitCredential.SshKey key, GitTransport.Ssh):
                var keyFile = WriteKeyFile(key);
                env["GIT_SSH_COMMAND"] = SshCommand(keyFile, remote.KnownHostsPath);
                return keyFile;
            case (null, GitTransport.Ssh):
                throw new GitException("SSH remotes need an SSH key as credential.");
            default:
                throw new GitException($"A {remote.Credential.GetType().Name} credential does not work with {remote.Url.Protocol} remotes.");
        }
    }

    private string WriteKeyFile(GitCredential.SshKey key)
    {
        var directory = Path.Combine(_options.TempPath ?? Path.GetTempPath(), "tikai-ssh");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var path = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // ssh refuses keys others can read
        }
        using (var stream = new StreamWriter(path, Encoding.ASCII, fileOptions))
        {
            // OpenSSH needs LF line endings and a final newline.
            stream.Write(key.PrivateKey.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() + "\n");
        }
        return path;
    }

    // Run by git through sh (also on Windows, where Git for Windows brings its own sh and ssh), so paths are
    // quoted for sh and use forward slashes.
    private static string SshCommand(string keyFile, string knownHostsPath) =>
        string.Join(' ',
            "ssh -F /dev/null",
            $"-i {ShellQuote(keyFile)}",
            "-o IdentitiesOnly=yes",
            "-o BatchMode=yes",
            "-o ConnectTimeout=30",
            "-o StrictHostKeyChecking=accept-new",
            $"-o UserKnownHostsFile={ShellQuote(knownHostsPath)}");

    private static string ShellQuote(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/');
        return normalized.Contains('\'', StringComparison.Ordinal)
            ? throw new ArgumentException($"Path '{path}' contains a quote.", nameof(path))
            : $"'{normalized}'";
    }

    private Dictionary<string, string?> BaseEnvironment()
    {
        var env = _clearedVariables.ToDictionary(name => name, _ => (string?)null);
        env["GIT_CONFIG_NOSYSTEM"] = "1";
        env["GIT_CONFIG_GLOBAL"] = _nullDevice;
        env["GIT_TERMINAL_PROMPT"] = "0";
        env["GIT_ALLOW_PROTOCOL"] = string.Join(':', _options.AllowedProtocolList);
        return env;
    }

    private static GitException Failure(string[] arguments, ProcessResult result)
    {
        var error = result.StandardError.Trim();
        if (error.Length > 2000)
        {
            error = "…" + error[^2000..];
        }
        // Git falls back to asking for a user name when the server rejects (or wants) a login; prompts are off.
        var hint = error.Contains("terminal prompts disabled", StringComparison.Ordinal)
            ? "The remote asked for a login: check that the credential is set, correct, and allowed to read this repository. "
            : "";
        return new GitException($"git {arguments[0]} failed (exit code {result.ExitCode}): {hint}{(error.Length > 0 ? error : "no error output")}");
    }

    private static void CheckBranch(string branch)
    {
        if (BranchName.ValidationError(branch) is { } error)
        {
            throw new ArgumentException(error, nameof(branch));
        }
    }

    private static void EnsureFree(string path)
    {
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new GitException($"'{path}' already exists and is not empty.");
        }
    }

    [GeneratedRegex("^([0-9a-f]{40}|[0-9a-f]{64})$")]
    private static partial Regex ObjectId();

    [GeneratedRegex(@"^ref: refs/heads/(?<branch>\S+)\tHEAD$", RegexOptions.Multiline)]
    private static partial Regex SymrefLine();

    [LoggerMessage(Level = LogLevel.Debug, Message = "git {Arguments}")]
    private partial void LogCommand(string[] arguments);

    private sealed record RemoteAccess(GitRemoteUrl Url, GitCredential? Credential, string KnownHostsPath);
}
