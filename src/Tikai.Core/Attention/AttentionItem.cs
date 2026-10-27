using Tikai.Core.Common;

namespace Tikai.Core.Attention;

/// <summary>
/// Something that needs a human: a question, a plan to approve, a submitted review… Blocking kinds keep an agent's
/// tool call waiting until <see cref="Answer"/> is set. The database row is the source of truth for the answer;
/// an open MCP call only waits for it.
/// </summary>
public class AttentionItem : Entity
{
    /// <summary>Event topic for new and answered items.</summary>
    public const string Topic = "attention_items";

    public AttentionKind Kind { get; set; }

    /// <summary>The run that raised it; null for items no run raised.</summary>
    public Guid? RunId { get; set; }

    public Guid TicketId { get; set; }

    public AttentionStatus Status { get; set; }

    /// <summary>JSON of the kind's payload record (<see cref="QuestionPayload"/>, <see cref="PlanPayload"/>, <see cref="ReviewPayload"/>).</summary>
    public required string Payload { get; set; }

    /// <summary>
    /// SHA-256 (hex) of the request that raised a blocking item, see <see cref="AttentionJson.RequestHash"/>.
    /// The same request from the same run again is merged into this item.
    /// </summary>
    public string? RequestHash { get; set; }

    /// <summary>JSON of <see cref="AttentionAnswer"/> once answered.</summary>
    public string? Answer { get; set; }

    public Guid? AnsweredById { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }

    /// <summary>
    /// When the answer was handed to the agent: as the result of a waiting (or repeated) tool call, or in a resume
    /// prompt. An answer that is stored but not delivered still has to reach the agent.
    /// </summary>
    public DateTimeOffset? DeliveredAt { get; set; }
}

public enum AttentionKind
{
    Question,
    Permission,
    PlanApproval,
    Review,
    Conflict,
    Failure,
}

public enum AttentionStatus
{
    Open,
    Answered,
    Dismissed,
}

public static class AttentionKinds
{
    /// <summary>Kinds whose MCP call waits for the answer.</summary>
    public static bool IsBlocking(this AttentionKind kind) =>
        kind is AttentionKind.Question or AttentionKind.PlanApproval or AttentionKind.Permission;

    public static IReadOnlyList<AttentionKind> Blocking { get; } =
        [.. Enum.GetValues<AttentionKind>().Where(k => k.IsBlocking())];
}
