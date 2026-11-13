using Microsoft.EntityFrameworkCore;
using Tikai.Core.Attention;
using Tikai.Data;

namespace Tikai.Api.Attention;

/// <summary>
/// Whether a run had a plan approved. An agent skips the plan for a change it judges small (run-start.v5), so the
/// review says when a run started without one.
/// </summary>
public static class RunPlans
{
    /// <summary>The runs among <paramref name="runIds"/> with at least one approved plan.</summary>
    public static async Task<HashSet<Guid>> ApprovedAsync(TikaiDbContext db, IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken)
    {
        if (runIds.Count == 0)
        {
            return [];
        }
        var answered = await db.AttentionItems.AsNoTracking()
            .Where(i => i.RunId != null && runIds.Contains(i.RunId.Value)
                && i.Kind == AttentionKind.PlanApproval && i.Status == AttentionStatus.Answered)
            .ToListAsync(cancellationToken);
        return [.. answered
            .Where(i => AttentionService.AnswerOf(i) is { Decision: PlanDecision.Approve })
            .Select(i => i.RunId!.Value)];
    }
}
