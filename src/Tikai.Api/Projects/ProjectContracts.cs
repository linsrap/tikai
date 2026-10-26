using Tikai.Core.Git;
using Tikai.Core.Projects;

namespace Tikai.Api.Projects;

public sealed record ProjectInfo(Guid Id, string Name, string Slug, string Key, DateTimeOffset CreatedAt)
{
    internal static ProjectInfo From(Project p) => new(p.Id, p.Name, p.Slug, p.Key, p.CreatedAt);
}

public sealed record CreateProjectRequest(string Name, string Slug, string Key);

public sealed record UpdateProjectRequest(string Name);

/// <summary>A repository as the API shows it. The credential itself is write-only: only its kind is returned.</summary>
public sealed record RepositoryInfo(
    Guid Id,
    string Name,
    string? RemoteUrl,
    string DefaultBranch,
    CredentialKind CredentialKind,
    RepositoryStatus Status,
    string? LastError,
    DateTimeOffset? LastFetchedAt,
    DateTimeOffset CreatedAt)
{
    internal static RepositoryInfo From(Repository r) =>
        new(r.Id, r.Name, r.RemoteUrl, r.DefaultBranch, r.CredentialKind, r.Status, r.LastError, r.LastFetchedAt, r.CreatedAt);
}

/// <param name="RemoteUrl">Null for a new local repository; otherwise the remote to mirror.</param>
/// <param name="DefaultBranch">Null: "main" for a local repository, detected from the remote otherwise.</param>
public sealed record CreateRepositoryRequest(string Name, string? RemoteUrl = null, string? DefaultBranch = null, CredentialInput? Credential = null);

/// <summary>A credential as entered. <c>https_token</c> uses Username (optional) and Token; <c>ssh_key</c> uses PrivateKey.</summary>
public sealed record CredentialInput(CredentialKind Kind, string? Username = null, string? Token = null, string? PrivateKey = null);

public sealed record BranchInfo(string Name, string Sha, bool IsRemote)
{
    internal static BranchInfo From(GitBranch b) => new(b.Name, b.Sha, b.IsRemote);
}
