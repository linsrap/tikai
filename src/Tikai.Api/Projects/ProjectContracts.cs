using Tikai.Core.Git;
using Tikai.Core.Projects;
using Tikai.Core.Reviews;

namespace Tikai.Api.Projects;

public sealed record ProjectInfo(Guid Id, string Name, string Slug, string Key, DateTimeOffset CreatedAt)
{
    internal static ProjectInfo From(Project p) => new(p.Id, p.Name, p.Slug, p.Key, p.CreatedAt);
}

public sealed record CreateProjectRequest(string Name, string Slug, string Key);

public sealed record UpdateProjectRequest(string Name);

/// <summary>A project's settings as the project page edits them.</summary>
/// <param name="AnalysisModels">The model per review analysis tier; a null model means the default.</param>
/// <param name="DefaultAnalysisModels">What each tier uses when no model is set.</param>
/// <param name="Delivery">What Approve does, with the defaults filled in (merge, squash).</param>
public sealed record ProjectSettingsInfo(ReviewModels AnalysisModels, ReviewModels DefaultAnalysisModels, DeliverySettings Delivery)
{
    internal static ProjectSettingsInfo From(Project project)
    {
        var settings = ProjectSettings.Parse(project.Settings);
        return new(
            settings.Review?.Models ?? new ReviewModels(),
            new ReviewModels(
                ReviewModels.Default(AnalysisTier.Light),
                ReviewModels.Default(AnalysisTier.Normal),
                ReviewModels.Default(AnalysisTier.Strict)),
            settings.DeliveryOrDefault);
    }
}

/// <param name="Delivery">Null keeps the stored delivery settings.</param>
public sealed record UpdateProjectSettingsRequest(ReviewModels AnalysisModels, DeliverySettings? Delivery = null);

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

/// <param name="Credential">Null keeps the repository's credential (none for a local one).</param>
public sealed record ConnectRemoteRequest(string RemoteUrl, CredentialInput? Credential = null);

/// <param name="Pushed">The hub's default branch was pushed (the remote was empty or behind).</param>
public sealed record ConnectRemoteInfo(RepositoryInfo Repository, bool Pushed);

/// <param name="RemoteUrl">Null for a new local repository; otherwise the remote to mirror.</param>
/// <param name="DefaultBranch">Null: "main" for a local repository, detected from the remote otherwise.</param>
public sealed record CreateRepositoryRequest(string Name, string? RemoteUrl = null, string? DefaultBranch = null, CredentialInput? Credential = null);

/// <summary>A credential as entered. <c>https_token</c> uses Username (optional) and Token; <c>ssh_key</c> uses PrivateKey.</summary>
public sealed record CredentialInput(CredentialKind Kind, string? Username = null, string? Token = null, string? PrivateKey = null);

public sealed record BranchInfo(string Name, string Sha, bool IsRemote)
{
    internal static BranchInfo From(GitBranch b) => new(b.Name, b.Sha, b.IsRemote);
}
