using System.Net;
using System.Text.Json;
using Tikai.IntegrationTests.Auth;
using Tikai.IntegrationTests.Mcp;
using Tikai.Orchestrator;

namespace Tikai.IntegrationTests.Runs;

/// <summary>A hub without Docker access (Tikai:Docker:Enabled false, e.g. Tikai previewing itself) starts and says why runs are off.</summary>
public sealed class DockerOffTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TikaiApiFactory _factory = null!;
    private BrowserClient _browser = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await TikaiApiFactory.CreateAsync(postgres, new Dictionary<string, string>
        {
            ["Tikai:Docker:Enabled"] = "false",
            // Nothing listens there: nothing may try.
            ["Tikai:Docker:Host"] = "tcp://127.0.0.1:1",
            [TikaiApiFactory.ReconcilerSetting] = "on",
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
    public async Task Assigning_a_ticket_says_that_agent_runs_are_off()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "nodocker", "ND", "T");
        (await _browser.PostAsync("/api/projects/nodocker/repositories", new { name = "app" })).EnsureSuccessStatusCode();

        using var response = await _browser.PostAsync($"/api/projects/nodocker/tickets/{ticket.Number}/runs", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("Agent runs are off", problem.GetProperty("title").GetString());
        Assert.Equal(DockerOptions.OffMessage, problem.GetProperty("detail").GetString());
        using var health = await _browser.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
