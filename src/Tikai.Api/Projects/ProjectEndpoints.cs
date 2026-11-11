using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tikai.Core.Projects;
using Tikai.Data;

namespace Tikai.Api.Projects;

internal static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this RouteGroupBuilder api)
    {
        var projects = api.MapGroup("/projects").WithTags("Projects").RequireAuthorization();

        projects.MapGet("/", ListAsync);
        projects.MapPost("/", CreateAsync);
        projects.MapGet("/{slug}", GetAsync);
        projects.MapPut("/{slug}", UpdateAsync);
        projects.MapPost("/{slug}/archive", ArchiveAsync);
        projects.MapPost("/{slug}/restore", RestoreAsync);
        projects.MapPost("/{slug}/delete", DeleteAsync);
        projects.MapGet("/{slug}/settings", GetSettingsAsync);
        projects.MapPut("/{slug}/settings", UpdateSettingsAsync);

        projects.MapGroup("/{slug}/repositories").MapRepositoryEndpoints();
    }

    private static async Task<Ok<List<ProjectInfo>>> ListAsync(TikaiDbContext db, CancellationToken cancellationToken)
    {
        var projects = await db.Projects.AsNoTracking().OrderBy(p => p.Name).ToListAsync(cancellationToken);
        return TypedResults.Ok(projects.Select(ProjectInfo.From).ToList());
    }

    private static async Task<Results<Created<ProjectInfo>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateProjectRequest request, TikaiDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        AddNameError(errors, request.Name);
        AddError(errors, "slug", ProjectSlug.ValidationError(request.Slug));
        AddError(errors, "key", ProjectKey.ValidationError(request.Key));
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var project = new Project { Name = request.Name.Trim(), Slug = request.Slug, Key = request.Key };
        db.Projects.Add(project);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (RepositoryService.IsDuplicate(ex))
        {
            var slugTaken = await db.Projects.AnyAsync(p => p.Slug == request.Slug, cancellationToken);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: slugTaken ? "A project with this slug already exists" : "A project with this key already exists",
                detail: "Slug and key must be unique.");
        }
        return TypedResults.Created($"/api/projects/{project.Slug}", ProjectInfo.From(project));
    }

    private static async Task<Results<Ok<ProjectInfo>, ProblemHttpResult>> GetAsync(string slug, TikaiDbContext db, CancellationToken cancellationToken)
    {
        var project = await FindAsync(db, slug, cancellationToken);
        return project is null ? NotFound() : TypedResults.Ok(ProjectInfo.From(project));
    }

    private static async Task<Results<Ok<ProjectInfo>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        string slug, UpdateProjectRequest request, TikaiDbContext db, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        AddNameError(errors, request.Name);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        // Not a tracked save: creating a ticket changes the project row (its ticket counter), so the xmin check
        // would fail a rename that overlaps with it. For a name, the last write wins.
        var renamed = await db.Projects.Where(p => p.Slug == slug).ExecuteUpdateAsync(
            set => set.SetProperty(p => p.Name, request.Name.Trim()).SetProperty(p => p.UpdatedAt, timeProvider.GetUtcNow()), cancellationToken);
        if (renamed == 0)
        {
            return NotFound();
        }
        return TypedResults.Ok(ProjectInfo.From((await FindAsync(db, slug, cancellationToken))!));
    }

    private static async Task<Results<Ok<ProjectSettingsInfo>, ProblemHttpResult>> GetSettingsAsync(
        string slug, TikaiDbContext db, CancellationToken cancellationToken)
    {
        var project = await FindAsync(db, slug, cancellationToken);
        return project is null ? NotFound() : TypedResults.Ok(ProjectSettingsInfo.From(project));
    }

    /// <summary>
    /// The model per review analysis tier, and what Approve does (merge or push the branch, squash or merge commit). Ids
    /// are checked for form only: the provider (maybe a gateway) decides which exist, and a wrong one shows as a failed
    /// analysis with the provider's message.
    /// </summary>
    private static async Task<Results<Ok<ProjectSettingsInfo>, ValidationProblem, ProblemHttpResult>> UpdateSettingsAsync(
        string slug, UpdateProjectSettingsRequest request, TikaiDbContext db, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var models = request.AnalysisModels ?? new ReviewModels();
        models = new ReviewModels(Empty(models.Light), Empty(models.Normal), Empty(models.Strict));
        var errors = models.Errors().ToDictionary(e => "analysisModels." + e.Field, e => new[] { e.Error });
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }
        var project = await FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }
        var settings = ProjectSettings.Parse(project.Settings);
        var json = (settings with
        {
            Review = (settings.Review ?? new ReviewSettings()) with { Models = models },
            Delivery = request.Delivery ?? settings.Delivery,
        }).ToJson();
        // Not a tracked save, for the same reason as a rename (the ticket counter shares the row).
        await db.Projects.Where(p => p.Id == project.Id).ExecuteUpdateAsync(
            set => set.SetProperty(p => p.Settings, json).SetProperty(p => p.UpdatedAt, timeProvider.GetUtcNow()), cancellationToken);
        project.Settings = json;
        return TypedResults.Ok(ProjectSettingsInfo.From(project));
    }

    private static string? Empty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// Hides the project and keeps its history. Its active runs are cancelled and its previews stopped first; nothing
    /// can be started in it until it is restored. Refused (409) while an approved change is being merged or pushed.
    /// </summary>
    private static async Task<Results<Ok<ProjectInfo>, ProblemHttpResult>> ArchiveAsync(
        string slug, TikaiDbContext db, ProjectLifecycle lifecycle, CancellationToken cancellationToken)
    {
        var project = await FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }
        if (project.DeletingAt is not null)
        {
            return Closed(project)!;
        }
        if (await lifecycle.IsDeliveringAsync(project.Id, cancellationToken))
        {
            return Delivering();
        }
        await lifecycle.ArchiveAsync(project.Id, cancellationToken);
        return TypedResults.Ok(ProjectInfo.From(await db.Projects.AsNoTracking().SingleAsync(p => p.Id == project.Id, cancellationToken)));
    }

    private static async Task<Results<Ok<ProjectInfo>, ProblemHttpResult>> RestoreAsync(
        string slug, TikaiDbContext db, ProjectLifecycle lifecycle, CancellationToken cancellationToken)
    {
        var project = await FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }
        if (project.DeletingAt is not null)
        {
            return Closed(project)!;
        }
        await lifecycle.RestoreAsync(project.Id, cancellationToken);
        return TypedResults.Ok(ProjectInfo.From(await db.Projects.AsNoTracking().SingleAsync(p => p.Id == project.Id, cancellationToken)));
    }

    /// <summary>
    /// Deletes the project with everything in it: tickets, runs, change sets, previews, repositories and the hub's
    /// mirrors (a git remote is never touched). The request repeats the project's name. Active runs are cancelled and
    /// previews stopped; the rows go once their containers and directories are gone, so the project shows as
    /// <see cref="ProjectInfo.Deleting"/> for a moment. Refused (409) while an approved change is being merged or pushed.
    /// </summary>
    private static async Task<Results<Accepted<ProjectInfo>, ValidationProblem, ProblemHttpResult>> DeleteAsync(
        string slug, DeleteProjectRequest request, TikaiDbContext db, ProjectLifecycle lifecycle, CancellationToken cancellationToken)
    {
        var project = await FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }
        if (!string.Equals(request.Name?.Trim(), project.Name, StringComparison.Ordinal))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Type the project's name exactly to confirm."] });
        }
        if (await lifecycle.IsDeliveringAsync(project.Id, cancellationToken))
        {
            return Delivering();
        }
        await lifecycle.RequestDeleteAsync(project.Id, cancellationToken);
        return TypedResults.Accepted($"/api/projects/{slug}", ProjectInfo.From(project) with { Deleting = true });
    }

    private static ProblemHttpResult Delivering() => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "A change is being delivered",
        detail: "An approved change of this project is being merged or pushed. Try again when it is done.");

    /// <summary>Why nothing can be started or added in the project (it is archived or being deleted); null when it is open.</summary>
    internal static ProblemHttpResult? Closed(Project project) => ClosedReason(project) is var (title, detail)
        ? TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail)
        : null;

    internal static (string Title, string Detail)? ClosedReason(Project project) => project switch
    {
        { DeletingAt: not null } => ("The project is being deleted", "Nothing can be started or changed in it any more."),
        { ArchivedAt: not null } => ("The project is archived", "Restore it on its settings page to work in it again."),
        _ => null,
    };

    internal static Task<Project?> FindAsync(TikaiDbContext db, string slug, CancellationToken cancellationToken) =>
        db.Projects.SingleOrDefaultAsync(p => p.Slug == slug, cancellationToken);

    internal static ProblemHttpResult NotFound(string what = "Project") =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: $"{what} not found");

    internal static void AddError(Dictionary<string, string[]> errors, string field, string? error)
    {
        if (error is not null)
        {
            errors[field] = [error];
        }
    }

    private static void AddNameError(Dictionary<string, string[]> errors, string? name) =>
        AddError(errors, "name", string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200 ? "Enter a name of at most 200 characters." : null);
}
