using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Tikai.Api.Llm;
using Tikai.Api.Tickets;
using Tikai.Core.Llm;
using Tikai.Data;
using Tikai.Data.Identity;
using Tikai.IntegrationTests.Auth;
using Tikai.IntegrationTests.Llm;
using Tikai.Llm;

namespace Tikai.IntegrationTests.Tickets;

public sealed class RefineSpecApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Answer = """
        {"title":"Add a login page","description":"## Goal\n\nPeople can log in.","criteria":["Wrong passwords are rejected","A session cookie is set"],"questions":["How long does a session last?"]}
        """;

    private readonly RecordingFactory _chat = new();
    private TikaiApiFactory _factory = null!;
    private WebApplicationFactory<Program> _app = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await TikaiApiFactory.CreateAsync(postgres);
        _app = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IChatClientFactory>(_chat)));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Refining_needs_a_login()
    {
        using var anonymous = new BrowserClient(_app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        await anonymous.RefreshAntiforgeryAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/projects/hub/refine-spec", Draft())).StatusCode);
        Assert.Empty(_chat.Connections);
    }

    [Fact]
    public async Task The_proposal_comes_back_and_nothing_is_stored()
    {
        using var browser = await AdminWithProjectAsync();
        var provider = await AddProviderAsync(browser, "/api/projects/hub/llm-providers", "Team key", "sk-ant-api03-project-AAAA");

        using var response = await browser.PostAsync("/api/projects/hub/refine-spec", Draft());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var proposal = (await response.Content.ReadFromJsonAsync<SpecProposalInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
        Assert.Equal("Add a login page", proposal.Title);
        Assert.Equal("## Goal\n\nPeople can log in.", proposal.Description);
        Assert.Equal(["Wrong passwords are rejected", "A session cookie is set"], proposal.Criteria);
        Assert.Equal(["How long does a session last?"], proposal.Questions);
        Assert.Equal(("refine-spec.v1", provider.Id, "Team key", "claude-opus-5-5"), (proposal.PromptVersion, proposal.ProviderId, proposal.ProviderName, proposal.Model));
        Assert.Equal("sk-ant-api03-project-AAAA", Assert.Single(_chat.Connections).ApiKey);
        Assert.Contains("Project: Tikai Hub\nTitle: login", _chat.LastUserMessage!.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("1. works", _chat.LastUserMessage, StringComparison.Ordinal);
        Assert.Empty(await browser.GetJsonAsync<List<TicketSummary>>("/api/projects/hub/tickets"));
    }

    [Fact]
    public async Task The_projects_provider_comes_first_then_the_users_own()
    {
        using var browser = await AdminWithProjectAsync();
        var own = await AddProviderAsync(browser, "/api/llm-providers", "Mine", "sk-ant-api03-own-BBBB");

        Assert.Equal(own.Id, (await RefineAsync(browser, Draft())).ProviderId);

        var team = await AddProviderAsync(browser, "/api/projects/hub/llm-providers", "Zeta team", "sk-ant-api03-project-CCCC");
        Assert.Equal(team.Id, (await RefineAsync(browser, Draft())).ProviderId);
        Assert.Equal(own.Id, (await RefineAsync(browser, Draft(own.Id))).ProviderId);
        Assert.Equal(["sk-ant-api03-own-BBBB", "sk-ant-api03-project-CCCC", "sk-ant-api03-own-BBBB"], _chat.Connections.Select(c => c.ApiKey));
    }

    [Fact]
    public async Task Without_a_provider_the_answer_says_where_to_add_one()
    {
        using var browser = await AdminWithProjectAsync();

        using var response = await browser.PostAsync("/api/projects/hub/refine-spec", Draft());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Add an LLM provider to this project", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_projects_or_another_users_provider_cannot_be_used()
    {
        using var browser = await AdminWithProjectAsync();
        (await browser.PostAsync("/api/projects", new { name = "Web", slug = "web", key = "WEB" })).EnsureSuccessStatusCode();
        var otherProject = await AddProviderAsync(browser, "/api/projects/web/llm-providers", "Web key", "sk-ant-api03-web-DDDD");
        var otherUser = await AddOtherUsersProviderAsync();

        foreach (var id in new[] { otherProject.Id, otherUser, Guid.NewGuid() })
        {
            using var response = await browser.PostAsync("/api/projects/hub/refine-spec", Draft(id));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("providerId", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
        Assert.Empty(_chat.Connections);
    }

    [Fact]
    public async Task A_failing_provider_is_a_bad_gateway_with_its_message()
    {
        using var browser = await AdminWithProjectAsync();
        await AddProviderAsync(browser, "/api/llm-providers", "Mine", "sk-ant-api03-own-EEEE");
        _chat.Reply = "this is not json";

        using var response = await browser.PostAsync("/api/projects/hub/refine-spec", Draft());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("not a complete proposal", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_drafts_and_unknown_projects_are_rejected_before_any_call()
    {
        using var browser = await AdminWithProjectAsync();
        await AddProviderAsync(browser, "/api/llm-providers", "Mine", "sk-ant-api03-own-FFFF");

        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/api/projects/hub/refine-spec", new { title = " ", criteria = new List<string> { "" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync("/api/projects/nope/refine-spec", Draft())).StatusCode);
        Assert.Empty(_chat.Connections);
    }

    private static object Draft(Guid? providerId = null) =>
        new { title = "login", description = "users need to log in", criteria = new List<string> { "works" }, providerId };

    private static async Task<SpecProposalInfo> RefineAsync(BrowserClient browser, object draft)
    {
        using var response = await browser.PostAsync("/api/projects/hub/refine-spec", draft);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SpecProposalInfo>(TikaiJson.Options))!;
    }

    private static async Task<LlmProviderInfo> AddProviderAsync(BrowserClient browser, string path, string name, string apiKey)
    {
        using var response = await browser.PostAsync(path, new { name, defaultModel = "claude-opus-5-5", apiKey });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LlmProviderInfo>(TikaiJson.Options))!;
    }

    // Sign-up closes after the first account, so the second user is created directly.
    private async Task<Guid> AddOtherUsersProviderAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var bob = new TikaiUser { UserName = "bob@example.org", Email = "bob@example.org", DisplayName = "Bob" };
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<TikaiUser>>().CreateAsync(bob, "correct horse battery")).Succeeded);
        var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
        var provider = new LlmProvider { OwnerUserId = bob.Id, Name = "Bob's", DefaultModel = "claude-opus-5-5", EncryptedApiKey = "not used", KeyHint = "" };
        db.LlmProviders.Add(provider);
        await db.SaveChangesAsync();
        return provider.Id;
    }

    private async Task<BrowserClient> AdminWithProjectAsync()
    {
        var browser = new BrowserClient(_app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        await browser.RefreshAntiforgeryAsync();
        await browser.SignUpAdminAsync();
        (await browser.PostAsync("/api/projects", new { name = "Tikai Hub", slug = "hub", key = "HUB" })).EnsureSuccessStatusCode();
        return browser;
    }

    /// <summary>Records the connections and answers every refine call with <see cref="Reply"/>.</summary>
    private sealed class RecordingFactory : IChatClientFactory
    {
        public ConcurrentQueue<LlmConnection> Connections { get; } = new();

        public string Reply { get; set; } = Answer;

        public string? LastUserMessage { get; private set; }

        public IChatClient Create(LlmConnection connection)
        {
            Connections.Enqueue(connection);
            return new FakeChatClient(messages =>
            {
                LastUserMessage = messages.Last().Text;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply));
            });
        }
    }
}
