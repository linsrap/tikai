using System.Security.Claims;
using ModelContextProtocol;

namespace Tikai.Mcp;

/// <summary>
/// The run an MCP request belongs to, from its run token. Tools take no run, ticket or project ids as
/// arguments: everything they touch comes from here, so an agent cannot reach another run's data.
/// </summary>
public sealed record RunScope(Guid RunId, Guid TicketId, Guid ProjectId)
{
    public IEnumerable<Claim> ToClaims() =>
    [
        new(RunClaims.RunId, RunId.ToString()),
        new(RunClaims.TicketId, TicketId.ToString()),
        new(RunClaims.ProjectId, ProjectId.ToString()),
    ];

    /// <summary>Reads the scope from the authenticated request; fails when the request was not authenticated with a run token.</summary>
    public static RunScope From(ClaimsPrincipal? user) =>
        Guid.TryParse(user?.FindFirst(RunClaims.RunId)?.Value, out var run)
        && Guid.TryParse(user.FindFirst(RunClaims.TicketId)?.Value, out var ticket)
        && Guid.TryParse(user.FindFirst(RunClaims.ProjectId)?.Value, out var project)
            ? new RunScope(run, ticket, project)
            : throw new McpProtocolException("This request is not authenticated with a run token.", McpErrorCode.InvalidRequest);
}

public static class RunClaims
{
    public const string RunId = "tikai:run";
    public const string TicketId = "tikai:ticket";
    public const string ProjectId = "tikai:project";
}
