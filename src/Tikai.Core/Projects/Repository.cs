using Tikai.Core.Common;

namespace Tikai.Core.Projects;

/// <summary>
/// A git repository of a project. The hub keeps a bare copy (the mirror) in the data volume;
/// with a remote it fetches from there, without one the mirror is the only copy.
/// </summary>
public class Repository : Entity
{
    public Guid ProjectId { get; set; }

    public required string Name { get; set; }

    /// <summary>Null for a local-only repository. Fixed after creation.</summary>
    public string? RemoteUrl { get; set; }

    /// <summary>Empty while the first clone has yet to detect it from the remote.</summary>
    public required string DefaultBranch { get; set; }

    public CredentialKind CredentialKind { get; set; }

    /// <summary>The credential, encrypted with <see cref="Security.ISecretProtector"/>. Never leaves the hub.</summary>
    public string? EncryptedCredential { get; set; }

    public RepositoryStatus Status { get; set; }

    /// <summary>Last clone or fetch error, shown to people; null after a successful fetch.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset? LastFetchedAt { get; set; }

    /// <summary>When the periodic fetch is due next (database clock); null for local repositories.</summary>
    public DateTimeOffset? NextFetchAt { get; set; }
}

public enum CredentialKind
{
    None,
    HttpsToken,
    SshKey,
}

public enum RepositoryStatus
{
    /// <summary>The first clone from the remote is queued or running.</summary>
    Cloning,

    /// <summary>The mirror exists and can be used.</summary>
    Ready,

    /// <summary>The first clone failed; see <see cref="Repository.LastError"/>. Fetching again retries it.</summary>
    Failed,
}
