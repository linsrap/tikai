using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tikai.Api.Llm;
using Tikai.Api.Projects;
using Tikai.Data;
using Tikai.Llm.Refine;

namespace Tikai.Api.Tickets;

internal static class RefineSpecEndpoint
{
    public static void MapRefineSpecEndpoint(this RouteGroupBuilder api) =>
        api.MapPost("/projects/{slug}/refine-spec", RefineAsync).WithTags("Tickets").RequireAuthorization();

    /// <summary>Asks an LLM for a sharper version of a ticket draft. Costs tokens on the provider's key; stores nothing.</summary>
    private static async Task<Results<Ok<SpecProposalInfo>, ValidationProblem, ProblemHttpResult>> RefineAsync(
        string slug,
        RefineSpecRequest request,
        ClaimsPrincipal user,
        TikaiDbContext db,
        LlmProviderService providers,
        SpecRefiner refiner,
        CancellationToken cancellationToken)
    {
        var criteria = request.Criteria ?? [];
        var errors = TicketEndpoints.Validate(request.Title, request.Description, criteria);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }
        var project = await ProjectEndpoints.FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return ProjectEndpoints.NotFound();
        }

        var usable = providers.UsableFor(LlmProviderService.UserId(user), project.Id);
        var provider = request.ProviderId is { } id
            ? await usable.SingleOrDefaultAsync(p => p.Id == id, cancellationToken)
            : await usable.FirstOrDefaultAsync(cancellationToken);
        if (provider is null && request.ProviderId is not null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["providerId"] = ["Choose one of this project's providers or one of your own."],
            });
        }
        if (provider is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "No LLM provider",
                detail: "Add an LLM provider to this project (on the project page) or your own under Settings.");
        }

        RefineResult result;
        try
        {
            result = await refiner.RefineAsync(
                providers.Connection(provider),
                new SpecDraft(project.Name, request.Title.Trim(), request.Description ?? "", [.. criteria.Select(c => c.Trim())]),
                cancellationToken);
        }
        catch (CryptographicException)
        {
            result = new RefineResult(null, $"The key of \"{provider.Name}\" cannot be decrypted (the hub's keys changed). Enter the key again.", TimeSpan.Zero);
        }
        if (result.Proposal is not { } proposal)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Refining failed", detail: result.Error);
        }
        return TypedResults.Ok(new SpecProposalInfo(
            proposal.Title,
            proposal.Description,
            proposal.Criteria,
            proposal.Questions,
            proposal.PromptVersion,
            provider.Id,
            provider.Name,
            provider.DefaultModel,
            (int)result.Duration.TotalMilliseconds));
    }
}
