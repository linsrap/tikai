namespace Tikai.Core.Git;

/// <summary>
/// Git operations on the hub's bare repositories (mirrors). Remote branches are kept in refs/remotes/origin/*;
/// refs/heads/* belong to the hub: the default branch follows the remote by fast-forward, and Tikai's own
/// branches (tikai/*) are never touched by a fetch.
/// </summary>
public interface IGitService
{
    /// <summary>Creates a bare repository whose default branch has one empty commit.</summary>
    Task InitBareAsync(string path, string defaultBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a bare repository at <paramref name="path"/> that fetches from <paramref name="remote"/>.
    /// The default branch is detected from the remote unless given. Nothing is left at the path on failure.
    /// </summary>
    Task<MirrorResult> CreateMirrorAsync(
        string path,
        GitRemoteUrl remote,
        GitCredential? credential,
        string? defaultBranch,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches from the remote and fast-forwards the local default branch if it can.</summary>
    Task<FetchResult> FetchAsync(
        string path,
        GitRemoteUrl remote,
        GitCredential? credential,
        string defaultBranch,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitBranch>> ListBranchesAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>The commit id a revision points to, or null when it does not exist.</summary>
    Task<string?> ResolveCommitAsync(string path, string revision, CancellationToken cancellationToken = default);

    /// <summary>A file's content at a commit, or null when the file does not exist there.</summary>
    Task<byte[]?> ReadFileAsync(string path, string commitSha, string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a git bundle with only <paramref name="branch"/> (refs/heads) and its history, e.g. for a run clone:
    /// the run gets that branch and nothing else of the repository.
    /// </summary>
    Task CreateBundleAsync(string path, string branch, string outputFile, CancellationToken cancellationToken = default);

    /// <summary>Deletes a repository directory, including git's read-only object files on Windows.</summary>
    Task DeleteRepositoryAsync(string path, CancellationToken cancellationToken = default);
}

/// <param name="IsEmpty">The remote has no commits on the default branch yet.</param>
public sealed record MirrorResult(string DefaultBranch, bool IsEmpty);

public sealed record FetchResult(DefaultBranchSync DefaultBranch);

/// <summary>What a fetch did with the local default branch.</summary>
public enum DefaultBranchSync
{
    UpToDate,
    FastForwarded,

    /// <summary>The hub has commits the remote does not have yet (a merge waiting to be pushed).</summary>
    LocalAhead,

    /// <summary>Local and remote both have commits the other lacks; nothing was changed.</summary>
    Diverged,

    /// <summary>The remote has no such branch (empty repository, or the branch was deleted).</summary>
    MissingOnRemote,
}

/// <param name="IsRemote">True for refs/remotes/origin/*, false for the hub's own refs/heads/*.</param>
public sealed record GitBranch(string Name, string Sha, bool IsRemote);

/// <summary>A git command failed. The message is meant for people (it includes git's error output).</summary>
public sealed class GitException(string message, Exception? innerException = null) : Exception(message, innerException);
