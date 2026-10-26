using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Tikai.Api.Attention;
using Tikai.Core.Attention;
using Tikai.IntegrationTests.Auth;
using Tikai.IntegrationTests.Mcp;

namespace Tikai.IntegrationTests.Attention;

public sealed class AttentionApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TikaiApiFactory _factory = null!;
    private BrowserClient _browser = null!;
    private ModelContextProtocol.Client.McpClient? _agent;

    public async ValueTask InitializeAsync()
    {
        _factory = await TikaiApiFactory.CreateAsync(postgres);
        _browser = new BrowserClient(_factory.CreateClient(new() { HandleCookies = true }));
        await _browser.RefreshAntiforgeryAsync();
        await _browser.SignUpAdminAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_agent is not null)
        {
            await _agent.DisposeAsync();
        }
        _browser.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Attention_endpoints_need_a_login()
    {
        using var anonymous = new BrowserClient(_factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        await anonymous.RefreshAntiforgeryAsync();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/attention-items")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/attention-items/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/attention-items/{id}/answer", new { text = "x" })).StatusCode);
    }

    [Fact]
    public async Task Answers_are_checked_per_kind()
    {
        var (question, plan) = await OpenQuestionAndPlanAsync();

        await AssertInvalidAsync(question, new { }, "text");
        await AssertInvalidAsync(question, new { text = "  " }, "text");
        await AssertInvalidAsync(question, new { text = "x", decision = "approve" }, "decision");
        await AssertInvalidAsync(plan, new { text = "looks fine" }, "decision");
        await AssertInvalidAsync(plan, new { decision = "revise" }, "text");
        await AssertInvalidAsync(question, new { text = new string('x', AttentionRules.MaxAnswerLength + 1) }, "text");
        Assert.Equal(HttpStatusCode.NotFound, (await _browser.PostAsync($"/api/attention-items/{Guid.NewGuid()}/answer", new { text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _browser.GetAsync($"/api/attention-items/{Guid.NewGuid()}")).StatusCode);

        using var answered = await _browser.PostAsync($"/api/attention-items/{question}/answer", new { text = "  yes  " });
        var info = (await answered.Content.ReadFromJsonAsync<AttentionItemInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
        Assert.Equal(AttentionStatus.Answered, info.Status);
        Assert.Equal(new AttentionAnswer("yes", null), info.Answer);
        Assert.Equal(HttpStatusCode.Conflict, (await _browser.PostAsync($"/api/attention-items/{question}/answer", new { text = "no" })).StatusCode);
        Assert.Equal("yes", (await _browser.GetJsonAsync<AttentionItemInfo>($"/api/attention-items/{question}")).Answer!.Text);
        Assert.Equal([plan], (await _browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items")).Select(i => i.Id));
    }

    [Fact]
    public async Task Two_answers_at_the_same_time_one_wins()
    {
        var (question, _) = await OpenQuestionAndPlanAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => _browser.PostAsync($"/api/attention-items/{question}/answer", new { text = $"answer {i}" })));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var winner = await responses.Single(r => r.IsSuccessStatusCode).Content.ReadFromJsonAsync<AttentionItemInfo>(TikaiJson.Options, TestContext.Current.CancellationToken);
        Assert.Equal(winner!.Answer, (await _browser.GetJsonAsync<AttentionItemInfo>($"/api/attention-items/{question}")).Answer);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task A_ticket_an_agent_worked_on_cannot_be_deleted()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        await McpTestKit.CreateTicketAsync(_browser, "web", "WEB", "Untouched");
        await McpTestKit.StartRunAsync(_factory, ticket.Id);

        using var refused = await _browser.DeleteAsync("/api/projects/hub/tickets/1");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("agent", (await refused.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken))!.Title!, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await _browser.GetAsync("/api/projects/hub/tickets/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _browser.DeleteAsync("/api/projects/web/tickets/1")).StatusCode);
    }

    /// <summary>An agent asks a question and proposes a plan; both calls are left waiting.</summary>
    private async Task<(Guid Question, Guid Plan)> OpenQuestionAndPlanAsync()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        var agent = _agent = await McpTestKit.ConnectAsync(_factory, token);
        _ = agent.CallAsync("ask_human", new { question = "Q?" });
        var question = (await McpTestKit.WaitForOpenItemsAsync(_browser, 1)).Single().Id;
        _ = agent.CallAsync("propose_plan", new { steps = new[] { "Step" } });
        var plan = (await McpTestKit.WaitForOpenItemsAsync(_browser, 2)).Single(i => i.Kind == AttentionKind.PlanApproval).Id;
        return (question, plan);
    }

    private async Task AssertInvalidAsync(Guid item, object body, string field)
    {
        using var response = await _browser.PostAsync($"/api/attention-items/{item}/answer", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(TestContext.Current.CancellationToken);
        Assert.True(problem!.Errors.ContainsKey(field), $"Expected an error for {field}: {string.Join(", ", problem.Errors.Keys)}");
    }
}
