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
    /// the run gets that branch and nothing else of the repository. Returns the commit the bundle holds.
    /// </summary>
    Task<string> CreateBundleAsync(string path, string branch, string outputFile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes work from an untrusted bundle (made from a run clone): checks that the mirror has its prerequisites, fetches
    /// <paramref name="branch"/> with every object checked (fsck), and points each of <paramref name="targetRefs"/> at
    /// it (forced: they are the hub's own refs). Returns the fetched commit.
    /// </summary>
    Task<string> ImportBundleAsync(
        string path, string bundleFile, string branch, IReadOnlyList<string> targetRefs, CancellationToken cancellationToken = default);

    /// <summary>The best common ancestor of two commits, or null when they share no history.</summary>
    Task<string?> MergeBaseAsync(string path, string first, string second, CancellationToken cancellationToken = default);

    /// <summary>The files changed between two commits (renames detected), at most <paramref name="maxFiles"/>.</summary>
    Task<GitDiff> DiffAsync(string path, string baseSha, string headSha, int maxFiles, CancellationToken cancellationToken = default);

    /// <summary>
    /// The unified diff (3 lines of context) of one changed file between two commits, with the rename when
    /// <paramref name="oldPath"/> is given; null when it is longer than <paramref name="maxBytes"/>.
    /// </summary>
    Task<string?> PatchAsync(
        string path, string baseSha, string headSha, string filePath, string? oldPath, long maxBytes, CancellationToken cancellationToken = default);

    /// <summary>A blob's size, and its content when it is at most <paramref name="maxBytes"/>; null when it does not exist.</summary>
    Task<GitBlob?> ReadBlobAsync(string path, string blobSha, long maxBytes, CancellationToken cancellationToken = default);

    /// <summary>Deletes a repository directory, including git's read-only object files on Windows.</summary>
    Task DeleteRepositoryAsync(string path, CancellationToken cancellationToken = default);
}

/// <param name="Truncated">There were more files than asked for.</param>
public sealed record GitDiff(IReadOnlyList<GitDiffEntry> Files, bool Truncated);

/// <summary>One changed file. Paths are null on the side where the file does not exist (added, deleted).</summary>
/// <param name="OldBlob">Null when the file was added.</param>
/// <param name="NewBlob">Null when the file was deleted.</param>
/// <param name="Additions">Changed lines; null for a binary file.</param>
public sealed record GitDiffEntry(
    GitChangeKind Kind,
    string? OldPath,
    string? NewPath,
    string? OldBlob,
    string? NewBlob,
    string OldMode,
    string NewMode,
    int? Additions,
    int? Deletions)
{
    /// <summary>The path the file has now, or had before it was deleted.</summary>
    public string Path => NewPath ?? OldPath!;
}

public enum GitChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,

    /// <summary>Only the type changed, e.g. a file became a symlink.</summary>
    TypeChanged,
}

/// <param name="Content">Null when the blob is bigger than asked for.</param>
public sealed record GitBlob(long Size, byte[]? Content);

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
