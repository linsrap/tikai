using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tikai.Core.Runs;
using Tikai.Data;
using Tikai.Mcp;

namespace Tikai.Api.Runs;

/// <summary>
/// Authenticates an agent by its run token (<c>Authorization: Bearer tikai_run_…</c>). The claims name the run,
/// its ticket and its project, read from the database, never from the request. A token of an ended run fails.
/// Only the MCP endpoint uses this scheme; the API uses the login cookie.
/// </summary>
internal sealed class RunTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    TikaiDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "RunToken";
    public const string PolicyName = "Agent";

    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }
        var token = header[BearerPrefix.Length..].Trim();
        if (!RunToken.IsWellFormed(token))
        {
            return AuthenticateResult.Fail("Not a run token.");
        }

        var hash = RunToken.Hash(token);
        var scope = await db.AgentRuns.AsNoTracking()
            .Where(r => r.TokenHash == hash && !AgentRunStates.Ended.Contains(r.State))
            .Join(db.Tickets, r => r.TicketId, t => t.Id, (r, t) => new RunScope(r.Id, t.Id, t.ProjectId))
            .SingleOrDefaultAsync(Context.RequestAborted);
        if (scope is null)
        {
            return AuthenticateResult.Fail("Unknown run token, or the run has ended.");
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(scope.ToClaims(), SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"tikai\"";
        return Task.CompletedTask;
    }
}
