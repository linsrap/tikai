using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tikai.Core.Git;
using Tikai.Core.Projects;
using Tikai.Data;
using Tikai.Git;

namespace Tikai.Api.Projects;

internal static class RepositoryEndpoints
{
    private const int MaxNameLength = 100;

    public static void MapRepositoryEndpoints(this RouteGroupBuilder repositories)
    {
        repositories.MapGet("/", ListAsync);
        repositories.MapPost("/", CreateAsync);
        repositories.MapGet("/{id:guid}", GetAsync);
        repositories.MapGet("/{id:guid}/branches", ListBranchesAsync);
        repositories.MapPut("/{id:guid}/credential", SetCredentialAsync);
        repositories.MapPost("/{id:guid}/fetch", FetchAsync);
        repositories.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<Results<Ok<List<RepositoryInfo>>, ProblemHttpResult>> ListAsync(
        string slug, TikaiDbContext db, CancellationToken cancellationToken)
    {
        var project = await ProjectEndpoints.FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return ProjectEndpoints.NotFound();
        }
        var repositories = await db.Repositories.AsNoTracking()
            .Where(r => r.ProjectId == project.Id)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(repositories.Select(RepositoryInfo.From).ToList());
    }

    private static async Task<Results<Created<RepositoryInfo>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        string slug,
        CreateRepositoryRequest request,
        TikaiDbContext db,
        RepositoryService service,
        IOptions<GitOptions> gitOptions,
        CancellationToken cancellationToken)
    {
        var project = await ProjectEndpoints.FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return ProjectEndpoints.NotFound();
        }

        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim() ?? "";
        ProjectEndpoints.AddError(errors, "name", name.Length is 0 or > MaxNameLength || name.Any(char.IsControl)
            ? $"Enter a name of at most {MaxNameLength} characters."
            : null);
        var defaultBranch = string.IsNullOrWhiteSpace(request.DefaultBranch) ? null : request.DefaultBranch.Trim();
        if (defaultBranch is not null)
        {
            ProjectEndpoints.AddError(errors, "defaultBranch", BranchName.ValidationError(defaultBranch));
        }
        GitRemoteUrl? remote = null;
        if (!string.IsNullOrWhiteSpace(request.RemoteUrl))
        {
            ProjectEndpoints.AddError(errors, "remoteUrl", RemoteError(request.RemoteUrl, gitOptions.Value, out remote));
        }
        if (!errors.ContainsKey("remoteUrl"))
        {
            ProjectEndpoints.AddError(errors, "credential", RepositoryCredentials.ValidationError(request.Credential, remote));
        }
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        Repository repository;
        try
        {
            repository = remote is null
                ? await service.CreateLocalAsync(project, name, defaultBranch ?? "main", cancellationToken)
                : await service.CreateMirrorAsync(project, name, remote, defaultBranch, request.Credential, cancellationToken);
        }
        catch (DbUpdateException ex) when (RepositoryService.IsDuplicate(ex))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "A repository with this name already exists in the project");
        }
        return TypedResults.Created($"/api/projects/{slug}/repositories/{repository.Id}", RepositoryInfo.From(repository));
    }

    private static async Task<Results<Ok<RepositoryInfo>, ProblemHttpResult>> GetAsync(
        string slug, Guid id, TikaiDbContext db, CancellationToken cancellationToken)
    {
        var repository = await FindAsync(db, slug, id, cancellationToken);
        return repository is null ? ProjectEndpoints.NotFound("Repository") : TypedResults.Ok(RepositoryInfo.From(repository));
    }

    private static async Task<Results<Ok<List<BranchInfo>>, ProblemHttpResult>> ListBranchesAsync(
        string slug, Guid id, TikaiDbContext db, IGitService git, MirrorPaths paths, CancellationToken cancellationToken)
    {
        var repository = await FindAsync(db, slug, id, cancellationToken);
        if (repository is null)
        {
            return ProjectEndpoints.NotFound("Repository");
        }
        if (repository.Status != RepositoryStatus.Ready)
        {
            return NotReady(repository);
        }
        var branches = await git.ListBranchesAsync(paths.For(repository.ProjectId, repository.Id), cancellationToken);
        return TypedResults.Ok(branches.Select(BranchInfo.From).ToList());
    }

    /// <summary>Replaces the credential. It is never returned; fetch again (or retry the clone) to use it.</summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> SetCredentialAsync(
        string slug, Guid id, CredentialInput request, TikaiDbContext db, RepositoryService service, CancellationToken cancellationToken)
    {
        var repository = await FindAsync(db, slug, id, cancellationToken);
        if (repository is null)
        {
            return ProjectEndpoints.NotFound("Repository");
        }
        var remote = repository.RemoteUrl is null ? null : GitRemoteUrl.Parse(repository.RemoteUrl);
        if (RepositoryCredentials.ValidationError(request, remote) is { } error)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["credential"] = [error] });
        }
        await service.SetCredentialAsync(repository, request, cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Fetches now, or retries the clone when it failed.</summary>
    private static async Task<Results<Accepted<RepositoryInfo>, ProblemHttpResult>> FetchAsync(
        string slug, Guid id, TikaiDbContext db, RepositoryService service, CancellationToken cancellationToken)
    {
        var repository = await FindAsync(db, slug, id, cancellationToken);
        if (repository is null)
        {
            return ProjectEndpoints.NotFound("Repository");
        }
        if (repository.RemoteUrl is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "This repository has no remote to fetch from");
        }
        await service.RequestFetchAsync(repository, cancellationToken);
        return TypedResults.Accepted($"/api/projects/{slug}/repositories/{id}", RepositoryInfo.From(repository));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        string slug, Guid id, TikaiDbContext db, RepositoryService service, CancellationToken cancellationToken)
    {
        var repository = await FindAsync(db, slug, id, cancellationToken);
        if (repository is null)
        {
            return ProjectEndpoints.NotFound("Repository");
        }
        await service.DeleteAsync(repository, cancellationToken);
        return TypedResults.NoContent();
    }

    private static string? RemoteError(string input, GitOptions options, out GitRemoteUrl? remote)
    {
        if (!GitRemoteUrl.TryParse(input, out remote, out var error))
        {
            return error;
        }
        return options.IsAllowed(remote.Protocol)
            ? null
            : $"Remotes over {remote.Protocol} are not allowed on this hub. Allowed: {string.Join(", ", options.AllowedProtocolList)}.";
    }

    private static async Task<Repository?> FindAsync(TikaiDbContext db, string slug, Guid id, CancellationToken cancellationToken) =>
        await db.Repositories.SingleOrDefaultAsync(
            r => r.Id == id && db.Projects.Any(p => p.Id == r.ProjectId && p.Slug == slug), cancellationToken);

    private static ProblemHttpResult NotReady(Repository repository) => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: repository.Status == RepositoryStatus.Cloning ? "The repository is still being cloned" : "The repository could not be cloned",
        detail: repository.LastError);
}
