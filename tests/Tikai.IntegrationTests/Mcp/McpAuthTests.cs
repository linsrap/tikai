using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tikai.Api.Runs;
using Tikai.Core.Runs;
using Tikai.Data;
using Tikai.IntegrationTests.Auth;
using Tikai.Mcp;

namespace Tikai.IntegrationTests.Mcp;

public sealed class McpAuthTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TikaiApiFactory _factory = null!;
    private BrowserClient _browser = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await TikaiApiFactory.CreateAsync(postgres);
        _browser = new BrowserClient(_factory.CreateClient(new() { HandleCookies = true }));
        await _browser.RefreshAntiforgeryAsync();
        await _browser.SignUpAdminAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _browser.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task A_token_for_run_X_cannot_read_run_Y()
    {
        var ticketX = await McpTestKit.CreateTicketAsync(_browser, "alpha", "ALP", "Ticket X");
        var ticketY = await McpTestKit.CreateTicketAsync(_browser, "beta", "BET", "Ticket Y");
        var (_, tokenX) = await McpTestKit.StartRunAsync(_factory, ticketX.Id);
        var (_, tokenY) = await McpTestKit.StartRunAsync(_factory, ticketY.Id);
        await using var agentX = await McpTestKit.ConnectAsync(_factory, tokenX);
        await using var agentY = await McpTestKit.ConnectAsync(_factory, tokenY);

        var seenByX = (await agentX.CallAsync("get_ticket")).Json();
        var seenByY = (await agentY.CallAsync("get_ticket")).Json();

        Assert.Equal("ALP-1", seenByX.GetProperty("key").GetString());
        Assert.Equal("Ticket X", seenByX.GetProperty("title").GetString());
        Assert.Equal(ticketX.Criteria.Select(c => c.Id), seenByX.GetProperty("criteria").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()));
        Assert.Equal("BET-1", seenByY.GetProperty("key").GetString());
        Assert.DoesNotContain("Ticket Y", seenByX.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Ticket X", seenByY.ToString(), StringComparison.Ordinal);

        // The same question from both runs is two items; Y's answer does not reach X's call.
        var askX = agentX.CallAsync("ask_human", new { question = "Which color?" });
        var askY = agentY.CallAsync("ask_human", new { question = "Which color?" });
        var items = await McpTestKit.WaitForOpenItemsAsync(_browser, 2);
        var itemX = items.Single(i => i.TicketKey == "ALP-1");
        var itemY = items.Single(i => i.TicketKey == "BET-1");
        await McpTestKit.AnswerAsync(_browser, itemY.Id, new { text = "blue for Y" });

        Assert.Equal("blue for Y", (await askY).Json().GetProperty("answer").GetString());
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(askX.IsCompleted);
        await McpTestKit.AnswerAsync(_browser, itemX.Id, new { text = "red for X" });
        Assert.Equal("red for X", (await askX).Json().GetProperty("answer").GetString());
    }

    [Fact]
    public async Task Mcp_needs_a_valid_run_token()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        using var http = _factory.CreateClient();

        using (var ok = await McpTestKit.PostRawAsync(http, $"Bearer {token}"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        foreach (var authorization in new string?[]
        {
            null,
            "Bearer",
            "Bearer not-a-token",
            $"Basic {token}",
            $"Bearer {RunToken.New()}",
            $"Bearer {token[..^2]}xx",
        })
        {
            using var response = await McpTestKit.PostRawAsync(http, authorization);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{authorization}: {response.StatusCode}");
            Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_login_cookie_does_not_open_mcp_and_a_run_token_does_not_open_the_api()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);

        using (var withCookie = await McpTestKit.PostRawAsync(_browser.Http, null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, withCookie.StatusCode);
        }
        using var http = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/projects");
        request.Headers.Authorization = new("Bearer", token);
        using var api = await http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
    }

    [Fact]
    public async Task The_token_ends_with_the_run_and_a_renewed_token_replaces_it()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (runId, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        using var http = _factory.CreateClient();

        string renewed;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            renewed = (await scope.ServiceProvider.GetRequiredService<RunService>().RenewTokenAsync(runId, CancellationToken.None))!;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpTestKit.PostRawAsync(http, $"Bearer {token}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await McpTestKit.PostRawAsync(http, $"Bearer {renewed}")).StatusCode);

        foreach (var ended in AgentRunStates.Ended)
        {
            await SetStateAsync(runId, ended);
            Assert.Equal(HttpStatusCode.Unauthorized, (await McpTestKit.PostRawAsync(http, $"Bearer {renewed}")).StatusCode);
        }
        await SetStateAsync(runId, AgentRunState.Running);
        Assert.Equal(HttpStatusCode.OK, (await McpTestKit.PostRawAsync(http, $"Bearer {renewed}")).StatusCode);

        await SetStateAsync(runId, AgentRunState.Cancelled);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<RunService>().RenewTokenAsync(runId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Only_the_tokens_hash_is_stored()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (runId, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);

        Assert.StartsWith(RunToken.Prefix, token, StringComparison.Ordinal);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
        var run = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == runId, TestContext.Current.CancellationToken);
        Assert.Equal(RunToken.Hash(token), run.TokenHash);
        var row = await db.Database.SqlQuery<string>($"SELECT row_to_json(r)::text AS \"Value\" FROM agent_runs r WHERE id = {runId}").ToListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(token[RunToken.Prefix.Length..], row.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_scope_comes_only_from_the_tokens_claims()
    {
        Assert.Throws<ModelContextProtocol.McpProtocolException>(() => RunScope.From(new System.Security.Claims.ClaimsPrincipal()));
        var scope = new RunScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(scope, RunScope.From(new(new System.Security.Claims.ClaimsIdentity(scope.ToClaims(), "test"))));
    }

    private async Task SetStateAsync(Guid runId, AgentRunState state)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
        await db.AgentRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.State, state), TestContext.Current.CancellationToken);
    }
}
