using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tikai.Api.Llm;
using Tikai.Core.Attention;
using Tikai.Core.Tickets;
using Tikai.Data;

namespace Tikai.Api.Attention;

/// <summary>The inbox's API: open items, and answering them. The inbox UI comes with roadmap step 12.</summary>
internal static class AttentionEndpoints
{
    public static void MapAttentionEndpoints(this RouteGroupBuilder api)
    {
        var items = api.MapGroup("/attention-items").WithTags("Attention").RequireAuthorization();
        items.MapGet("/", ListOpenAsync);
        items.MapGet("/{id:guid}", GetAsync);
        items.MapPost("/{id:guid}/answer", AnswerAsync);
    }

    /// <summary>Open items of all projects, newest first.</summary>
    private static async Task<Ok<List<AttentionItemInfo>>> ListOpenAsync(TikaiDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await QueryAsync(db, db.AttentionItems.Where(i => i.Status == AttentionStatus.Open), cancellationToken));

    private static async Task<Results<Ok<AttentionItemInfo>, ProblemHttpResult>> GetAsync(Guid id, TikaiDbContext db, CancellationToken cancellationToken) =>
        (await QueryAsync(db, db.AttentionItems.Where(i => i.Id == id), cancellationToken)) is [var item]
            ? TypedResults.Ok(item)
            : NotFound();

    /// <summary>Answers a question or decides on a plan. The waiting agent gets the answer as its tool result.</summary>
    private static async Task<Results<Ok<AttentionItemInfo>, ValidationProblem, ProblemHttpResult>> AnswerAsync(
        Guid id, AnswerRequest request, ClaimsPrincipal user, TikaiDbContext db, AttentionService attention, CancellationToken cancellationToken)
    {
        var item = await db.AttentionItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }
        if (item.Kind is not (AttentionKind.Question or AttentionKind.PlanApproval))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "This item is not answered here",
                detail: "Only questions and plan approvals take an answer.");
        }
        var (answer, error) = AttentionRules.Answer(item.Kind, request.Text, request.Decision);
        if (error is var (field, message))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
        }
        if (!await attention.AnswerAsync(id, answer!, LlmProviderService.UserId(user), cancellationToken))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The item is already answered",
                detail: "Someone answered it first. Reload to see the answer.");
        }
        return TypedResults.Ok((await QueryAsync(db, db.AttentionItems.Where(i => i.Id == id), cancellationToken))[0]);
    }

    private static async Task<List<AttentionItemInfo>> QueryAsync(TikaiDbContext db, IQueryable<AttentionItem> items, CancellationToken cancellationToken)
    {
        var rows = await items.AsNoTracking()
            .Join(db.Tickets, i => i.TicketId, t => t.Id, (i, t) => new { Item = i, t.Number, t.ProjectId })
            .Join(db.Projects, x => x.ProjectId, p => p.Id, (x, p) => new { x.Item, x.Number, p.Slug, p.Key })
            .OrderByDescending(x => x.Item.CreatedAt).ThenByDescending(x => x.Item.Id)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(x => new AttentionItemInfo(
            x.Item.Id,
            x.Item.Kind,
            x.Item.Status,
            x.Item.RunId,
            x.Slug,
            x.Number,
            TicketKey.Format(x.Key, x.Number),
            x.Item.Kind == AttentionKind.Question ? AttentionJson.Deserialize<QuestionPayload>(x.Item.Payload) : null,
            x.Item.Kind == AttentionKind.PlanApproval ? AttentionJson.Deserialize<PlanPayload>(x.Item.Payload) : null,
            x.Item.Kind == AttentionKind.Review ? AttentionJson.Deserialize<ReviewPayload>(x.Item.Payload) : null,
            AttentionService.AnswerOf(x.Item),
            x.Item.CreatedAt,
            x.Item.AnsweredAt))];
    }

    private static ProblemHttpResult NotFound() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Attention item not found");
}
