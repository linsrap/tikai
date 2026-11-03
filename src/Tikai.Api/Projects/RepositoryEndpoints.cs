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
        repositories.MapPut("/{id:guid}/remote", ConnectRemoteAsync);
        repositories.MapGet("/{id:guid}/bundle", BundleAsync);
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

    /// <summary>
    /// Sets the repository's remote: a local repository gets its first one, a remote one a new URL. The hub fetches and,
    /// when the remote is empty or behind, pushes its default branch; a remote whose history the hub's default branch
    /// does not continue is refused (409, nothing changes). A credential that is not given stays as it is.
    /// </summary>
    private static async Task<Results<Ok<ConnectRemoteInfo>, ValidationProblem, ProblemHttpResult>> ConnectRemoteAsync(
        string slug, Guid id, ConnectRemoteRequest request, TikaiDbContext db, RepositoryService service, IOptions<GitOptions> gitOptions,
        CancellationToken cancellationToken)
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
        var errors = new Dictionary<string, string[]>();
        GitRemoteUrl? remote = null;
        ProjectEndpoints.AddError(errors, "remoteUrl", string.IsNullOrWhiteSpace(request.RemoteUrl)
            ? "Enter the remote's URL."
            : RemoteError(request.RemoteUrl, gitOptions.Value, out remote));
        if (remote is not null && request.Credential is not null)
        {
            ProjectEndpoints.AddError(errors, "credential", RepositoryCredentials.ValidationError(request.Credential, remote));
        }
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }
        if (await db.ChangeSets.AnyAsync(c => c.RepositoryId == id && c.Status == Core.Reviews.ChangeSetStatus.Delivering, cancellationToken))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "A change is being delivered",
                detail: "An approved change of this repository is being merged or pushed. Try again when it is done.");
        }
        try
        {
            var pushed = await service.ConnectRemoteAsync(repository, remote!, request.Credential, cancellationToken);
            return TypedResults.Ok(new ConnectRemoteInfo(RepositoryInfo.From(repository), pushed));
        }
        catch (GitException ex)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "The remote was not connected", detail: ex.Message);
        }
    }

    /// <summary>
    /// The hub's default branch as a git bundle, for a copy on people's own computers: "git clone x.bundle" the first
    /// time, "git pull x.bundle main" later. The hub's own refs (run branches, review rounds) are not in it.
    /// </summary>
    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> BundleAsync(
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
        var file = Path.Combine(Path.GetTempPath(), $"tikai-download-{Guid.NewGuid():N}.bundle");
        try
        {
            await git.CreateBundleAsync(paths.For(repository.ProjectId, repository.Id), repository.DefaultBranch, file, withHead: true, cancellationToken);
        }
        catch (GitException ex)
        {
            File.Delete(file);
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "No bundle", detail: ex.Message);
        }
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        return TypedResults.File(stream, "application/octet-stream", BundleName(repository.Name));
    }

    /// <summary>The repository's name as a file name: letters, digits, dot, dash and underscore.</summary>
    internal static string BundleName(string name)
    {
        var cleaned = new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-')]).Trim('.', '-');
        return (cleaned.Length == 0 ? "repository" : cleaned) + ".bundle";
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
