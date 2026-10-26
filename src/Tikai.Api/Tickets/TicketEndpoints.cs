using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tikai.Api.Projects;
using Tikai.Core.Tickets;
using Tikai.Data;

namespace Tikai.Api.Tickets;

internal static class TicketEndpoints
{
    public static void MapTicketEndpoints(this RouteGroupBuilder api)
    {
        var tickets = api.MapGroup("/projects/{slug}/tickets").WithTags("Tickets").RequireAuthorization();
        tickets.MapGet("/", ListAsync);
        tickets.MapPost("/", CreateAsync);
        tickets.MapGet("/{number:int}", GetAsync);
        tickets.MapPut("/{number:int}", UpdateAsync);
        tickets.MapDelete("/{number:int}", DeleteAsync);
    }

    /// <summary>All tickets of the project, newest first.</summary>
    private static async Task<Results<Ok<List<TicketSummary>>, ProblemHttpResult>> ListAsync(
        string slug, TikaiDbContext db, CancellationToken cancellationToken)
    {
        var project = await ProjectEndpoints.FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return ProjectEndpoints.NotFound();
        }
        var projectKey = project.Key;
        var tickets = await db.Tickets.AsNoTracking()
            .Where(t => t.ProjectId == project.Id)
            .OrderByDescending(t => t.Number)
            .Select(t => new TicketSummary(
                t.Id, t.Number, TicketKey.Format(projectKey, t.Number), t.Title, t.Status, t.Priority, t.Criteria.Count, t.CreatedAt, t.UpdatedAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(tickets);
    }

    private static async Task<Results<Created<TicketInfo>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        string slug, CreateTicketRequest request, TikaiDbContext db, TicketService service, CancellationToken cancellationToken)
    {
        var errors = Validate(request.Title, request.Description, request.Criteria ?? []);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }
        var project = await ProjectEndpoints.FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return ProjectEndpoints.NotFound();
        }
        var ticket = await service.CreateAsync(project, request, cancellationToken);
        return TypedResults.Created(
            $"/api/projects/{project.Slug}/tickets/{ticket.Number.ToString(CultureInfo.InvariantCulture)}",
            TicketInfo.From(ticket, project.Key));
    }

    private static async Task<Results<Ok<TicketInfo>, ProblemHttpResult>> GetAsync(
        string slug, int number, TikaiDbContext db, TicketService service, CancellationToken cancellationToken)
    {
        var project = await ProjectEndpoints.FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return ProjectEndpoints.NotFound();
        }
        var ticket = await service.FindAsync(project.Id, number, cancellationToken);
        return ticket is null ? NotFound() : TypedResults.Ok(TicketInfo.From(ticket, project.Key));
    }

    private static async Task<Results<Ok<TicketInfo>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        string slug, int number, UpdateTicketRequest request, TikaiDbContext db, TicketService service, CancellationToken cancellationToken)
    {
        // Criteria may be missing from a hand-written request despite the contract.
        var criteria = (IReadOnlyList<CriterionInput>?)request.Criteria;
        var errors = Validate(request.Title, request.Description, [.. (criteria ?? []).Select(c => c?.Text)]);
        if (criteria is null || criteria.Contains(null))
        {
            errors["criteria"] = ["Send the complete list of criteria (it may be empty)."];
        }
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }
        var project = await ProjectEndpoints.FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return ProjectEndpoints.NotFound();
        }
        var ticket = await service.FindAsync(project.Id, number, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }
        try
        {
            if (await service.UpdateAsync(ticket, request, cancellationToken) is { } criteriaError)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["criteria"] = [criteriaError] });
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The ticket was changed meanwhile",
                detail: "Someone else saved this ticket while you were editing it. Reload it and make your change again.");
        }
        return TypedResults.Ok(TicketInfo.From(ticket, project.Key));
    }

    /// <summary>Deletes the ticket and its criteria. Its number is not given out again.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        string slug, int number, TikaiDbContext db, CancellationToken cancellationToken)
    {
        var project = await ProjectEndpoints.FindAsync(db, slug, cancellationToken);
        if (project is null)
        {
            return ProjectEndpoints.NotFound();
        }
        var deleted = await db.Tickets.Where(t => t.ProjectId == project.Id && t.Number == number).ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? NotFound() : TypedResults.NoContent();
    }

    internal static Dictionary<string, string[]> Validate(string? title, string? description, IReadOnlyList<string?> criteria)
    {
        var errors = new Dictionary<string, string[]>();
        ProjectEndpoints.AddError(errors, "title", TicketRules.TitleError(title));
        ProjectEndpoints.AddError(errors, "description", description is null ? "Send a description (it may be empty)." : TicketRules.DescriptionError(description));
        ProjectEndpoints.AddError(errors, "criteria", TicketRules.CriteriaCountError(criteria.Count));
        for (var i = 0; i < criteria.Count; i++)
        {
            ProjectEndpoints.AddError(errors, $"criteria[{i}]", TicketRules.CriterionError(criteria[i]) is { } error ? $"Criterion {i + 1}: {error}" : null);
        }
        return errors;
    }

    private static ProblemHttpResult NotFound() => ProjectEndpoints.NotFound("Ticket");
}
