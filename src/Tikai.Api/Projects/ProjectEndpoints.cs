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
        projects.MapDelete("/{slug}", DeleteAsync);

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
        string slug, UpdateProjectRequest request, TikaiDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        AddNameError(errors, request.Name);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var project = await FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }
        project.Name = request.Name.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ProjectInfo.From(project));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(string slug, TikaiDbContext db, CancellationToken cancellationToken)
    {
        var project = await FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }
        if (await db.Repositories.AnyAsync(r => r.ProjectId == project.Id, cancellationToken))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The project still has repositories",
                detail: "Delete its repositories first.");
        }
        db.Projects.Remove(project);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

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
