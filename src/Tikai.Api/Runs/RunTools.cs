using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tikai.Api.Attention;
using Tikai.Core.Agents;
using Tikai.Core.Attention;
using Tikai.Core.Events;
using Tikai.Core.Runs;
using Tikai.Core.Tickets;
using Tikai.Data;
using Tikai.Data.Runs;
using Tikai.Mcp;

namespace Tikai.Api.Runs;

/// <summary>The hub side of the MCP tools. Every query is bound to the run from the token, never to tool arguments.</summary>
internal sealed class RunTools(TikaiDbContext db, AttentionService attention, IEventPublisher events, TimeProvider timeProvider) : IRunTools
{
    // The transcript keeps no key- or token-shaped text, also not from what the agent reports.
    private static readonly SecretRedactor _redactor = new([]);

    public async Task<TicketResult> GetTicketAsync(RunScope run, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Criteria)
            .SingleAsync(t => t.Id == run.TicketId && t.ProjectId == run.ProjectId, cancellationToken);
        var projectKey = await db.Projects.Where(p => p.Id == run.ProjectId).Select(p => p.Key).SingleAsync(cancellationToken);
        return new TicketResult(
            TicketKey.Format(projectKey, ticket.Number),
            ticket.Title,
            ticket.Description,
            SnakeCase(ticket.Status),
            SnakeCase(ticket.Priority),
            [.. ticket.Criteria.OrderBy(c => c.Position).Select(c => new CriterionResult(c.Id, c.Text))]);
    }

    public async Task ReportProgressAsync(RunScope run, string currentStep, string? note, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.AgentRuns.Where(r => r.Id == run.RunId).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.ProgressStep, currentStep)
            .SetProperty(r => r.ProgressNote, note)
            .SetProperty(r => r.ProgressAt, now)
            .SetProperty(r => r.UpdatedAt, now), cancellationToken);
        await db.AppendAsync(run.RunId,
            [new ParsedEvent(RunEventKind.Progress, new JsonObject { ["step"] = _redactor.Redact(currentStep), ["note"] = note is null ? null : _redactor.Redact(note) })],
            null, now, cancellationToken);
        await events.PublishAsync(AgentRun.Topic, run.RunId, cancellationToken);
        await events.PublishAsync(RunEvent.Topic, run.RunId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<string?> SubmitForReviewAsync(
        RunScope run, string? summary, IReadOnlyList<CriterionMapping>? criteriaMapping, IReadOnlyList<string?>? openConcerns, CancellationToken cancellationToken)
    {
        var criterionIds = (await db.AcceptanceCriteria.Where(c => c.TicketId == run.TicketId).Select(c => c.Id).ToListAsync(cancellationToken)).ToHashSet();
        var (payload, error) = AttentionRules.Review(summary, criteriaMapping, openConcerns, criterionIds);
        if (payload is null)
        {
            return error;
        }
        // The git part (bundle, change set, review round, run state) comes with roadmap step 13.
        await attention.RecordAsync(run, AttentionKind.Review, payload, cancellationToken);
        return null;
    }

    public Task<RaisedItem> RaiseAsync<TPayload>(RunScope run, AttentionKind kind, TPayload payload, CancellationToken cancellationToken) =>
        attention.RaiseAsync(run, kind, payload, cancellationToken);

    public Task DeliveredAsync(RunScope run, Guid itemId, CancellationToken cancellationToken) =>
        attention.DeliveredAsync(run, itemId, cancellationToken);

    private static string SnakeCase<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
