using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tikai.Api.Runs;
using Tikai.Api.Tickets;
using Tikai.Core.Runs;
using Tikai.Data;
using Tikai.IntegrationTests.Auth;
using Tikai.IntegrationTests.Mcp;

namespace Tikai.IntegrationTests.Runs;

// Assigning without starting containers: the agent image is one that does not exist, so a run that gets through
// fails right away in its start job.
public sealed class RunApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TikaiApiFactory _factory = null!;
    private BrowserClient _browser = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await TikaiApiFactory.CreateAsync(postgres, new Dictionary<string, string>
        {
            ["Tikai:Agents:ClaudeCodeImage"] = "tikai/agent-does-not-exist:test",
            ["Tikai:Agents:RunsVolume"] = "tikai-test-unused",
        });
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
    public async Task A_project_without_a_repository_cannot_assign()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "norepo", "NR", "T");
        await AddProviderAsync("norepo");

        await AssertProblemAsync(await AssignAsync("norepo", ticket), HttpStatusCode.Conflict, "No repository");
    }

    [Fact]
    public async Task Several_repositories_need_a_choice_and_only_this_projects_ones_count()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "multi", "MU", "T");
        await AddProviderAsync("multi");
        await AddRepositoryAsync("multi", "one");
        await AddRepositoryAsync("multi", "two");
        var other = await McpTestKit.CreateTicketAsync(_browser, "other", "OT", "T");
        var foreign = await AddRepositoryAsync("other", "foreign");

        using var missing = await AssignAsync("multi", ticket);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("several repositories", await missing.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        using var wrong = await AssignAsync("multi", ticket, new { repositoryId = foreign });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var two = (await _browser.GetJsonAsync<List<Api.Projects.RepositoryInfo>>("/api/projects/multi/repositories")).Single(r => r.Name == "two");
        using var chosen = await AssignAsync("multi", ticket, new { repositoryId = two.Id });
        Assert.Equal(HttpStatusCode.Accepted, chosen.StatusCode);
        var run = (await chosen.Content.ReadFromJsonAsync<RunInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
        Assert.Equal("two", run.RepositoryName);
        Assert.NotNull(other);
    }

    [Fact]
    public async Task A_repository_that_is_not_ready_cannot_be_used()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "notready", "NO", "T");
        await AddProviderAsync("notready");
        var id = await AddRepositoryAsync("notready", "remote", remoteUrl: "https://example.invalid/never.git");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            // Whatever the clone job did meanwhile: still cloning.
            await scope.ServiceProvider.GetRequiredService<TikaiDbContext>().Repositories.Where(r => r.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, Core.Projects.RepositoryStatus.Cloning), TestContext.Current.CancellationToken);
        }

        await AssertProblemAsync(await AssignAsync("notready", ticket), HttpStatusCode.Conflict, "Repository not ready");
    }

    [Fact]
    public async Task Without_a_provider_the_answer_says_where_to_add_one()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "nokey", "NK", "T");
        await AddRepositoryAsync("nokey", "app");

        await AssertProblemAsync(await AssignAsync("nokey", ticket), HttpStatusCode.Conflict, "No LLM provider");
        using var unknown = await AssignAsync("nokey", ticket, new { providerId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task The_users_own_provider_is_used_when_the_project_has_none()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "ownkey", "OK", "T");
        await AddRepositoryAsync("ownkey", "app");
        using var own = await _browser.PostAsync("/api/llm-providers", new { name = "Mine", defaultModel = "claude-sonnet-5-5", apiKey = "own-key-0123456789" });
        own.EnsureSuccessStatusCode();

        using var response = await AssignAsync("ownkey", ticket);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("claude-sonnet-5-5", (await response.Content.ReadFromJsonAsync<RunInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!.Model);
    }

    [Fact]
    public async Task A_closed_ticket_cannot_be_assigned()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "closed", "CL", "T");
        await AddProviderAsync("closed");
        await AddRepositoryAsync("closed", "app");
        (await _browser.PutAsync($"/api/projects/closed/tickets/{ticket.Number}", new
        {
            title = ticket.Title,
            description = ticket.Description,
            status = "closed",
            priority = "normal",
            criteria = ticket.Criteria.Select(c => new { id = c.Id, text = c.Text }),
        })).EnsureSuccessStatusCode();

        await AssertProblemAsync(await AssignAsync("closed", ticket), HttpStatusCode.Conflict, "Ticket is not open");
    }

    [Fact]
    public async Task One_active_run_per_ticket_and_per_repository()
    {
        var first = await McpTestKit.CreateTicketAsync(_browser, "busy", "BU", "First");
        using var second = await _browser.PostAsync("/api/projects/busy/tickets", new { title = "Second", description = "", criteria = Array.Empty<string>() });
        var secondTicket = (await second.Content.ReadFromJsonAsync<TicketInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
        await AddProviderAsync("busy");
        var repository = await AddRepositoryAsync("busy", "app");
        // An active run that no job will pick up (as if its agent were working).
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var (run, _) = await scope.ServiceProvider.GetRequiredService<RunService>().CreateAsync(first.Id, TestContext.Current.CancellationToken);
            var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
            await db.AgentRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.State, AgentRunState.Running).SetProperty(r => r.RepositoryId, repository), TestContext.Current.CancellationToken);
        }

        await AssertProblemAsync(await AssignAsync("busy", first), HttpStatusCode.Conflict, "A run is active");
        await AssertProblemAsync(await AssignAsync("busy", secondTicket), HttpStatusCode.Conflict, "A run is active");
    }

    [Fact]
    public async Task The_database_also_refuses_a_second_active_run()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "dbrule", "DB", "T");
        await using var scope = _factory.Services.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<RunService>();
        await runs.CreateAsync(ticket.Id, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DbUpdateException>(() => runs.CreateAsync(ticket.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_run_that_cannot_start_fails_and_the_ticket_is_open_again()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "noimage", "NI", "T");
        await AddProviderAsync("noimage");
        await AddRepositoryAsync("noimage", "app");

        using var response = await AssignAsync("noimage", ticket);
        var run = (await response.Content.ReadFromJsonAsync<RunInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
        for (var i = 0; i < 200 && run.State != AgentRunState.Failed; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            run = await _browser.GetJsonAsync<RunInfo>($"/api/runs/{run.Id}");
        }

        Assert.Equal(AgentRunState.Failed, run.State);
        var events = await _browser.GetJsonAsync<List<RunEventInfo>>($"/api/runs/{run.Id}/events");
        Assert.Contains(events, e => e.Payload.GetRawText().Contains("is not on this Docker host", StringComparison.Ordinal));
        Assert.Empty(await _browser.GetJsonAsync<List<RunEventInfo>>($"/api/runs/{run.Id}/events?afterSeq={events[^1].Seq}"));
        Assert.Equal(Core.Tickets.TicketStatus.Open, (await _browser.GetJsonAsync<TicketInfo>($"/api/projects/noimage/tickets/{ticket.Number}")).Status);
        var listed = await _browser.GetJsonAsync<List<RunInfo>>($"/api/projects/noimage/tickets/{ticket.Number}/runs");
        Assert.Equal(run.Id, Assert.Single(listed).Id);
    }

    [Fact]
    public async Task Unknown_runs_are_404_and_everything_needs_a_login()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _browser.GetAsync($"/api/runs/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _browser.PostAsync($"/api/runs/{Guid.NewGuid()}/cancel")).StatusCode);

        using var anonymous = new BrowserClient(_factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        await anonymous.RefreshAntiforgeryAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/runs/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/projects/x/tickets/1/runs")).StatusCode);
    }

    private Task<HttpResponseMessage> AssignAsync(string slug, TicketInfo ticket, object? body = null) =>
        _browser.PostAsync($"/api/projects/{slug}/tickets/{ticket.Number}/runs", body ?? new { });

    private async Task AddProviderAsync(string slug) =>
        (await _browser.PostAsync($"/api/projects/{slug}/llm-providers", new { name = "Team", defaultModel = "claude-haiku-5-5", apiKey = "team-key-0123456789" }))
            .EnsureSuccessStatusCode();

    private async Task<Guid> AddRepositoryAsync(string slug, string name, string? remoteUrl = null)
    {
        using var response = await _browser.PostAsync($"/api/projects/{slug}/repositories", new { name, remoteUrl });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Api.Projects.RepositoryInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!.Id;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string title)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == status, $"{(int)response.StatusCode} {body}");
            Assert.Contains(title, body, StringComparison.Ordinal);
        }
    }
}
