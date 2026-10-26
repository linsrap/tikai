using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tikai.Api.Projects;
using Tikai.Core.Llm;
using Tikai.Data;

namespace Tikai.Api.Llm;

internal static class LlmProviderEndpoints
{
    public static void MapLlmProviderEndpoints(this RouteGroupBuilder api)
    {
        var providers = api.MapGroup("/llm-providers").WithTags("LLM providers").RequireAuthorization();
        providers.MapGet("/", ListOwnAsync);
        providers.MapPost("/", CreateOwnAsync);
        providers.MapGet("/{id:guid}", GetAsync);
        providers.MapPut("/{id:guid}", UpdateAsync);
        providers.MapDelete("/{id:guid}", DeleteAsync);
        providers.MapPost("/{id:guid}/test", TestAsync);

        var projectProviders = api.MapGroup("/projects/{slug}/llm-providers").WithTags("LLM providers").RequireAuthorization();
        projectProviders.MapGet("/", ListForProjectAsync);
        projectProviders.MapPost("/", CreateForProjectAsync);
    }

    private static async Task<Ok<List<LlmProviderInfo>>> ListOwnAsync(ClaimsPrincipal user, TikaiDbContext db, CancellationToken cancellationToken)
    {
        var userId = LlmProviderService.UserId(user);
        var providers = await db.LlmProviders.AsNoTracking().Where(p => p.OwnerUserId == userId).OrderBy(p => p.Name).ToListAsync(cancellationToken);
        return TypedResults.Ok(providers.Select(LlmProviderInfo.From).ToList());
    }

    private static async Task<Results<Ok<List<LlmProviderInfo>>, ProblemHttpResult>> ListForProjectAsync(
        string slug, TikaiDbContext db, CancellationToken cancellationToken)
    {
        var project = await ProjectEndpoints.FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return ProjectEndpoints.NotFound();
        }
        var providers = await db.LlmProviders.AsNoTracking().Where(p => p.OwnerProjectId == project.Id).OrderBy(p => p.Name).ToListAsync(cancellationToken);
        return TypedResults.Ok(providers.Select(LlmProviderInfo.From).ToList());
    }

    private static Task<Results<Created<LlmProviderInfo>, ValidationProblem, ProblemHttpResult>> CreateOwnAsync(
        CreateLlmProviderRequest request, ClaimsPrincipal user, LlmProviderService service, CancellationToken cancellationToken) =>
        CreateAsync(request, service, LlmProviderService.UserId(user), null, cancellationToken);

    private static async Task<Results<Created<LlmProviderInfo>, ValidationProblem, ProblemHttpResult>> CreateForProjectAsync(
        string slug, CreateLlmProviderRequest request, TikaiDbContext db, LlmProviderService service, CancellationToken cancellationToken)
    {
        var project = await ProjectEndpoints.FindAsync(db, slug, cancellationToken);
        return project is null
            ? ProjectEndpoints.NotFound()
            : await CreateAsync(request, service, null, project.Id, cancellationToken);
    }

    private static async Task<Results<Created<LlmProviderInfo>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateLlmProviderRequest request, LlmProviderService service, Guid? userId, Guid? projectId, CancellationToken cancellationToken)
    {
        var errors = Validate(request.Name, request.DefaultModel, request.BaseUrl);
        ProjectEndpoints.AddError(errors, "apiKey", LlmProviderRules.ApiKeyError(request.ApiKey));
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }
        try
        {
            var provider = await service.CreateAsync(request, userId, projectId, cancellationToken);
            return TypedResults.Created($"/api/llm-providers/{provider.Id}", LlmProviderInfo.From(provider));
        }
        catch (DbUpdateException ex) when (RepositoryService.IsDuplicate(ex))
        {
            return DuplicateName();
        }
    }

    private static async Task<Results<Ok<LlmProviderInfo>, ProblemHttpResult>> GetAsync(
        Guid id, ClaimsPrincipal user, LlmProviderService service, CancellationToken cancellationToken)
    {
        var provider = await FindAsync(service, user, id, cancellationToken);
        return provider is null ? NotFound() : TypedResults.Ok(LlmProviderInfo.From(provider));
    }

    private static async Task<Results<Ok<LlmProviderInfo>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, UpdateLlmProviderRequest request, ClaimsPrincipal user, LlmProviderService service, CancellationToken cancellationToken)
    {
        var errors = Validate(request.Name, request.DefaultModel, request.BaseUrl);
        if (request.ApiKey is not null)
        {
            ProjectEndpoints.AddError(errors, "apiKey", LlmProviderRules.ApiKeyError(request.ApiKey));
        }
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var provider = await FindAsync(service, user, id, cancellationToken);
        if (provider is null)
        {
            return NotFound();
        }
        try
        {
            await service.UpdateAsync(provider, request, cancellationToken);
        }
        catch (DbUpdateException ex) when (RepositoryService.IsDuplicate(ex))
        {
            return DuplicateName();
        }
        return TypedResults.Ok(LlmProviderInfo.From(provider));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id, ClaimsPrincipal user, LlmProviderService service, TikaiDbContext db, CancellationToken cancellationToken)
    {
        var provider = await FindAsync(service, user, id, cancellationToken);
        if (provider is null)
        {
            return NotFound();
        }
        db.LlmProviders.Remove(provider);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Sends one short message with the stored key and model. Costs a few tokens.</summary>
    private static async Task<Results<Ok<LlmTestResponse>, ProblemHttpResult>> TestAsync(
        Guid id, ClaimsPrincipal user, LlmProviderService service, CancellationToken cancellationToken)
    {
        var provider = await FindAsync(service, user, id, cancellationToken);
        if (provider is null)
        {
            return NotFound();
        }
        var result = await service.TestAsync(provider, cancellationToken);
        return TypedResults.Ok(new LlmTestResponse(result.Ok, result.Message, result.Reply, (int)result.Duration.TotalMilliseconds));
    }

    private static Dictionary<string, string[]> Validate(string? name, string? model, string? baseUrl)
    {
        var errors = new Dictionary<string, string[]>();
        ProjectEndpoints.AddError(errors, "name", LlmProviderRules.NameError(name));
        ProjectEndpoints.AddError(errors, "defaultModel", LlmProviderRules.ModelError(model));
        ProjectEndpoints.AddError(errors, "baseUrl", LlmProviderRules.BaseUrlError(baseUrl));
        return errors;
    }

    private static Task<LlmProvider?> FindAsync(LlmProviderService service, ClaimsPrincipal user, Guid id, CancellationToken cancellationToken) =>
        service.Visible(LlmProviderService.UserId(user)).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    private static ProblemHttpResult NotFound() => ProjectEndpoints.NotFound("LLM provider");

    private static ProblemHttpResult DuplicateName() =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "A provider with this name already exists");
}
