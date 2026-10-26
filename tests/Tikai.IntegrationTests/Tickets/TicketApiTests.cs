using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tikai.Api.Tickets;
using Tikai.Core.Tickets;
using Tikai.Data;
using Tikai.Data.Tickets;
using Tikai.IntegrationTests.Auth;

namespace Tikai.IntegrationTests.Tickets;

public sealed class TicketApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TikaiApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await TikaiApiFactory.CreateAsync(postgres);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Ticket_endpoints_need_a_login()
    {
        using var browser = await AdminWithProjectAsync();
        (await browser.PostAsync("/api/projects/hub/tickets", new { title = "First" })).EnsureSuccessStatusCode();
        using var anonymous = new BrowserClient(_factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        await anonymous.RefreshAntiforgeryAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/projects/hub/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/projects/hub/tickets", new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/projects/hub/tickets/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsync("/api/projects/hub/tickets/1", Update("x", []))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/api/projects/hub/tickets/1")).StatusCode);
    }

    [Fact]
    public async Task Tickets_created_at_the_same_time_get_unique_gapless_numbers()
    {
        using var browser = await AdminWithProjectAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            browser.PostAsync("/api/projects/hub/tickets", new { title = $"Ticket {i}", criteria = new List<string> { "one", "two" } })));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var tickets = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<TicketInfo>(TikaiJson.Options)));
        Assert.Equal(Enumerable.Range(1, 20), tickets.Select(t => t!.Number).Order());
        Assert.Equal(Enumerable.Range(1, 20).Select(n => $"HUB-{n}"), tickets.OrderBy(t => t!.Number).Select(t => t!.Key));
        var list = await browser.GetJsonAsync<List<TicketSummary>>("/api/projects/hub/tickets");
        Assert.Equal(Enumerable.Range(1, 20).Reverse(), list.Select(t => t.Number));
        Assert.All(list, t => Assert.Equal(2, t.CriteriaCount));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Projects_count_independently_and_numbers_are_never_reused()
    {
        using var browser = await AdminWithProjectAsync();
        (await browser.PostAsync("/api/projects", new { name = "Web", slug = "web", key = "WEB" })).EnsureSuccessStatusCode();

        Assert.Equal("HUB-1", (await CreateAsync(browser, "hub", "a")).Key);
        Assert.Equal("HUB-2", (await CreateAsync(browser, "hub", "b")).Key);
        Assert.Equal("WEB-1", (await CreateAsync(browser, "web", "c")).Key);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.DeleteAsync("/api/projects/hub/tickets/2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/api/projects/hub/tickets/2")).StatusCode);

        Assert.Equal("HUB-3", (await CreateAsync(browser, "hub", "d")).Key);
        Assert.Equal([3, 1], (await browser.GetJsonAsync<List<TicketSummary>>("/api/projects/hub/tickets")).Select(t => t.Number));
    }

    [Fact]
    public async Task A_ticket_is_created_with_defaults_and_its_criteria_in_order()
    {
        using var browser = await AdminWithProjectAsync();

        using var created = await browser.PostAsync("/api/projects/hub/tickets", new
        {
            title = "  Login page  ",
            description = "# Goal\n\nPeople can **log in**.",
            criteria = new List<string> { " Wrong passwords are rejected ", "A session cookie is set" },
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/projects/hub/tickets/1", created.Headers.Location?.OriginalString);
        var ticket = (await created.Content.ReadFromJsonAsync<TicketInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
        Assert.Equal(("Login page", TicketStatus.Open, TicketPriority.Normal), (ticket.Title, ticket.Status, ticket.Priority));
        Assert.Equal("# Goal\n\nPeople can **log in**.", ticket.Description);
        Assert.Equal(["Wrong passwords are rejected", "A session cookie is set"], ticket.Criteria.Select(c => c.Text));
        Assert.Equal(ticket, await browser.GetJsonAsync<TicketInfo>("/api/projects/hub/tickets/1"), TicketComparer.Instance);
    }

    [Fact]
    public async Task Editing_adds_changes_reorders_and_removes_criteria_and_keeps_their_ids()
    {
        using var browser = await AdminWithProjectAsync();
        var ticket = await CreateAsync(browser, "hub", "Login page", "a", "b", "c");
        var (a, b, c) = (ticket.Criteria[0], ticket.Criteria[1], ticket.Criteria[2]);

        using var response = await browser.PutAsync("/api/projects/hub/tickets/1", new
        {
            title = "Login page v2",
            description = "Now with *details*.",
            status = "in_progress",
            priority = "high",
            criteria = new object[] { new { id = c.Id, text = "c" }, new { text = "new" }, new { id = a.Id, text = "a, edited" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await browser.GetJsonAsync<TicketInfo>("/api/projects/hub/tickets/1");
        Assert.Equal(("Login page v2", "Now with *details*.", TicketStatus.InProgress, TicketPriority.High),
            (updated.Title, updated.Description, updated.Status, updated.Priority));
        Assert.Equal(["c", "new", "a, edited"], updated.Criteria.Select(x => x.Text));
        Assert.Equal(c.Id, updated.Criteria[0].Id);
        Assert.Equal(a.Id, updated.Criteria[2].Id);
        Assert.DoesNotContain(updated.Criteria, x => x.Id == b.Id);
        Assert.True(updated.UpdatedAt > ticket.UpdatedAt);
    }

    [Fact]
    public async Task Changing_only_the_criteria_moves_the_ticket_timestamp()
    {
        using var browser = await AdminWithProjectAsync();
        var ticket = await CreateAsync(browser, "hub", "Same", "a", "b");

        (await browser.PutAsync("/api/projects/hub/tickets/1", Update("Same", [new { id = ticket.Criteria[1].Id, text = "b" }, new { id = ticket.Criteria[0].Id, text = "a" }])))
            .EnsureSuccessStatusCode();

        var updated = await browser.GetJsonAsync<TicketInfo>("/api/projects/hub/tickets/1");
        Assert.Equal(["b", "a"], updated.Criteria.Select(x => x.Text));
        Assert.True(updated.UpdatedAt > ticket.UpdatedAt);
    }

    [Fact]
    public async Task Invalid_tickets_are_rejected()
    {
        using var browser = await AdminWithProjectAsync();

        using var invalid = await browser.PostAsync("/api/projects/hub/tickets", new
        {
            title = "two\nlines",
            description = new string('x', TicketRules.MaxDescriptionLength + 1),
            criteria = new List<string> { "fine", " " },
        });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var body = await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"title\"", body, StringComparison.Ordinal);
        Assert.Contains("\"description\"", body, StringComparison.Ordinal);
        Assert.Contains("Criterion 2:", body, StringComparison.Ordinal);
        using var tooMany = await browser.PostAsync("/api/projects/hub/tickets", new { title = "x", criteria = Enumerable.Repeat("c", TicketRules.MaxCriteria + 1) });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Empty(await browser.GetJsonAsync<List<TicketSummary>>("/api/projects/hub/tickets"));
    }

    [Fact]
    public async Task Criteria_of_another_ticket_or_a_missing_list_are_rejected()
    {
        using var browser = await AdminWithProjectAsync();
        await CreateAsync(browser, "hub", "First", "mine");
        var other = await CreateAsync(browser, "hub", "Second", "theirs");

        using var foreign = await browser.PutAsync("/api/projects/hub/tickets/1", Update("First", [new { id = other.Criteria[0].Id, text = "stolen" }]));
        using var missing = await browser.PutAsync("/api/projects/hub/tickets/1", new { title = "First", description = "", status = "open", priority = "normal" });

        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Contains("not one of this ticket's criteria", await foreign.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(["theirs"], (await browser.GetJsonAsync<TicketInfo>("/api/projects/hub/tickets/2")).Criteria.Select(c => c.Text));
        Assert.Equal(["mine"], (await browser.GetJsonAsync<TicketInfo>("/api/projects/hub/tickets/1")).Criteria.Select(c => c.Text));
    }

    [Fact]
    public async Task Unknown_projects_and_numbers_are_404()
    {
        using var browser = await AdminWithProjectAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/api/projects/nope/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync("/api/projects/nope/tickets", new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/api/projects/hub/tickets/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PutAsync("/api/projects/hub/tickets/1", Update("x", []))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.DeleteAsync("/api/projects/hub/tickets/1")).StatusCode);
    }

    [Fact]
    public async Task A_project_with_tickets_cannot_be_deleted_but_can_be_renamed()
    {
        using var browser = await AdminWithProjectAsync();
        await CreateAsync(browser, "hub", "Keep me");

        using var delete = await browser.DeleteAsync("/api/projects/hub");
        using var rename = await browser.PutAsync("/api/projects/hub", new { name = "Renamed" });

        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Contains("still has tickets", await delete.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        Assert.Equal("HUB-2", (await CreateAsync(browser, "hub", "Still counting")).Key);
    }

    [Fact]
    public async Task A_rolled_back_creation_gives_its_number_back()
    {
        var ct = TestContext.Current.CancellationToken;
        using var browser = await AdminWithProjectAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
            var project = await db.Projects.SingleAsync(p => p.Slug == "hub", ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            Assert.Equal(1, await db.NextAsync(project.Id, ct));
            await transaction.RollbackAsync(ct);
        }

        Assert.Equal("HUB-1", (await CreateAsync(browser, "hub", "First")).Key);
    }

    [Fact]
    public async Task Overlapping_saves_of_one_ticket_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        using var browser = await AdminWithProjectAsync();
        var created = await CreateAsync(browser, "hub", "Shared", "a");
        await using var first = _factory.Services.CreateAsyncScope();
        await using var second = _factory.Services.CreateAsyncScope();
        var projectId = (await first.ServiceProvider.GetRequiredService<TikaiDbContext>().Projects.SingleAsync(p => p.Slug == "hub", ct)).Id;
        var firstService = first.ServiceProvider.GetRequiredService<TicketService>();
        var secondService = second.ServiceProvider.GetRequiredService<TicketService>();
        var firstCopy = (await firstService.FindAsync(projectId, 1, ct))!;
        var secondCopy = (await secondService.FindAsync(projectId, 1, ct))!;

        Assert.Null(await firstService.UpdateAsync(firstCopy, new UpdateTicketRequest("First wins", "", TicketStatus.Open, TicketPriority.Normal, [new("a", created.Criteria[0].Id)]), ct));
        // The second save changes only criteria and still conflicts: the ticket row is always written.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            secondService.UpdateAsync(secondCopy, new UpdateTicketRequest("Shared", "", TicketStatus.Open, TicketPriority.Normal, [new("a"), new("b")]), ct));

        var stored = await browser.GetJsonAsync<TicketInfo>("/api/projects/hub/tickets/1");
        Assert.Equal("First wins", stored.Title);
        Assert.Equal(["a"], stored.Criteria.Select(c => c.Text));
    }

    private static object Update(string title, object[] criteria) =>
        new { title, description = "", status = "open", priority = "normal", criteria };

    private static async Task<TicketInfo> CreateAsync(BrowserClient browser, string slug, string title, params string[] criteria)
    {
        using var response = await browser.PostAsync($"/api/projects/{slug}/tickets", new { title, criteria });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TicketInfo>(TikaiJson.Options))!;
    }

    private async Task<BrowserClient> AdminWithProjectAsync()
    {
        var browser = new BrowserClient(_factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        await browser.RefreshAntiforgeryAsync();
        await browser.SignUpAdminAsync();
        (await browser.PostAsync("/api/projects", new { name = "Tikai Hub", slug = "hub", key = "HUB" })).EnsureSuccessStatusCode();
        return browser;
    }

    /// <summary>Records with a list compare the list by reference; this compares the criteria by value.</summary>
    private sealed class TicketComparer : IEqualityComparer<TicketInfo>
    {
        public static readonly TicketComparer Instance = new();

        public bool Equals(TicketInfo? x, TicketInfo? y) =>
            x is not null && y is not null
            && x with { Criteria = null! } == y with { Criteria = null! }
            && x.Criteria.SequenceEqual(y.Criteria);

        public int GetHashCode(TicketInfo obj) => obj.Id.GetHashCode();
    }
}
