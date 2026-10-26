using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Tikai.Api.Llm;
using Tikai.Core.Llm;
using Tikai.IntegrationTests.Auth;
using Tikai.Llm;

namespace Tikai.IntegrationTests.Llm;

public sealed class LlmProviderApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Key = "sk-ant-api03-secretsecret-WxYz";
    private readonly FakeChatClientFactory _chat = new();
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
    public async Task Provider_endpoints_need_a_login()
    {
        using var anonymous = new BrowserClient(_app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        await anonymous.RefreshAntiforgeryAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/llm-providers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/llm-providers", NewProvider())).StatusCode);
    }

    [Fact]
    public async Task The_key_is_write_only_and_stored_encrypted()
    {
        using var browser = await AdminAsync();

        using var created = await browser.PostAsync("/api/llm-providers", NewProvider());

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var provider = (await created.Content.ReadFromJsonAsync<LlmProviderInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
        Assert.Equal(("Anthropic", LlmProviderKind.Anthropic, "claude-opus-5-5", "WxYz", null), (provider.Name, provider.Kind, provider.DefaultModel, provider.KeyHint, provider.BaseUrl));
        foreach (var path in new[] { "/api/llm-providers", $"/api/llm-providers/{provider.Id}" })
        {
            var body = await (await browser.GetAsync(path)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("WxYz", body, StringComparison.Ordinal);
            Assert.DoesNotContain("secretsecret", body, StringComparison.Ordinal);
        }

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var query = new NpgsqlCommand("SELECT encrypted_api_key, key_hint FROM llm_providers", connection);
        await using var row = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await row.ReadAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("secretsecret", row.GetString(0), StringComparison.Ordinal);
        Assert.Equal("WxYz", row.GetString(1));
    }

    [Fact]
    public async Task Test_connection_uses_the_stored_key_model_and_endpoint()
    {
        using var browser = await AdminAsync();
        var provider = await CreateAsync(browser, NewProvider(baseUrl: "https://gateway.example.org"));

        var result = await TestAsync(browser, provider.Id);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("OK", result.Reply);
        Assert.Equal(new LlmConnection(LlmProviderKind.Anthropic, Key, "claude-opus-5-5", "https://gateway.example.org"), Assert.Single(_chat.Connections));
    }

    [Fact]
    public async Task A_failing_provider_gives_a_message_not_an_error()
    {
        using var browser = await AdminAsync();
        var provider = await CreateAsync(browser, NewProvider());
        _chat.Failure = new InvalidOperationException("boom");

        var result = await TestAsync(browser, provider.Id);

        Assert.False(result.Ok);
        Assert.Contains("boom", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Updating_keeps_the_key_unless_a_new_one_is_given()
    {
        using var browser = await AdminAsync();
        var provider = await CreateAsync(browser, NewProvider());

        using (var renamed = await browser.PutAsync($"/api/llm-providers/{provider.Id}", new { name = "Work", defaultModel = "claude-haiku-5-5" }))
        {
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        }
        await TestAsync(browser, provider.Id);
        using (var rekeyed = await browser.PutAsync($"/api/llm-providers/{provider.Id}", new { name = "Work", defaultModel = "claude-haiku-5-5", apiKey = "sk-ant-api03-another-key-AbCd" }))
        {
            Assert.Equal("AbCd", (await rekeyed.Content.ReadFromJsonAsync<LlmProviderInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!.KeyHint);
        }
        await TestAsync(browser, provider.Id);

        Assert.Equal(
            [new LlmConnection(LlmProviderKind.Anthropic, Key, "claude-haiku-5-5", null), new LlmConnection(LlmProviderKind.Anthropic, "sk-ant-api03-another-key-AbCd", "claude-haiku-5-5", null)],
            _chat.Connections);
    }

    [Theory]
    [InlineData("""{"name":"","defaultModel":"claude-opus-5-5","apiKey":"sk-ant-api03-0123456789"}""", "name")]
    [InlineData("""{"name":"A","defaultModel":"Claude Opus","apiKey":"sk-ant-api03-0123456789"}""", "defaultModel")]
    [InlineData("""{"name":"A","defaultModel":"claude-opus-5-5","apiKey":"short"}""", "apiKey")]
    [InlineData("""{"name":"A","defaultModel":"claude-opus-5-5","apiKey":"sk-ant-api03-0123456789","baseUrl":"http://10.0.0.1"}""", "baseUrl")]
    public async Task Invalid_providers_are_rejected(string json, string field)
    {
        using var browser = await AdminAsync();

        using var response = await browser.Http.PostAsync(new Uri("/api/llm-providers", UriKind.Relative), new StringContent(json, System.Text.Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Names_are_unique_per_owner()
    {
        using var browser = await AdminAsync();
        await CreateAsync(browser, NewProvider());

        Assert.Equal(HttpStatusCode.Conflict, (await browser.PostAsync("/api/llm-providers", NewProvider())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await browser.PostAsync("/api/projects/hub/llm-providers", NewProvider())).StatusCode);
    }

    [Fact]
    public async Task Project_providers_belong_to_the_project_and_go_with_it()
    {
        using var browser = await AdminAsync();
        using var created = await browser.PostAsync("/api/projects/hub/llm-providers", NewProvider());
        var provider = (await created.Content.ReadFromJsonAsync<LlmProviderInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;

        Assert.NotNull(provider.OwnerProjectId);
        Assert.Equal([provider.Id], (await browser.GetJsonAsync<List<LlmProviderInfo>>("/api/projects/hub/llm-providers")).Select(p => p.Id));
        Assert.Empty(await browser.GetJsonAsync<List<LlmProviderInfo>>("/api/llm-providers"));
        Assert.True((await TestAsync(browser, provider.Id)).Ok);

        Assert.Equal(HttpStatusCode.NoContent, (await browser.DeleteAsync("/api/projects/hub")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/api/llm-providers/{provider.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_provider_can_be_deleted()
    {
        using var browser = await AdminAsync();
        var provider = await CreateAsync(browser, NewProvider());

        Assert.Equal(HttpStatusCode.NoContent, (await browser.DeleteAsync($"/api/llm-providers/{provider.Id}")).StatusCode);
        Assert.Empty(await browser.GetJsonAsync<List<LlmProviderInfo>>("/api/llm-providers"));
    }

    private async Task<BrowserClient> AdminAsync()
    {
        var browser = new BrowserClient(_app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        await browser.RefreshAntiforgeryAsync();
        await browser.SignUpAdminAsync();
        (await browser.PostAsync("/api/projects", new { name = "Tikai Hub", slug = "hub", key = "HUB" })).EnsureSuccessStatusCode();
        return browser;
    }

    private static async Task<LlmProviderInfo> CreateAsync(BrowserClient browser, object request)
    {
        using var response = await browser.PostAsync("/api/llm-providers", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LlmProviderInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
    }

    private static async Task<LlmTestResponse> TestAsync(BrowserClient browser, Guid id)
    {
        using var response = await browser.PostAsync($"/api/llm-providers/{id}/test");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LlmTestResponse>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
    }

    private static object NewProvider(string? baseUrl = null) =>
        new { name = "Anthropic", defaultModel = "claude-opus-5-5", apiKey = Key, baseUrl };

    /// <summary>Records the connections it is asked for; its chat client answers "OK" or throws <see cref="Failure"/>.</summary>
    private sealed class FakeChatClientFactory : IChatClientFactory
    {
        public ConcurrentQueue<LlmConnection> Connections { get; } = new();

        public Exception? Failure { get; set; }

        public IChatClient Create(LlmConnection connection)
        {
            Connections.Enqueue(connection);
            return new FakeChatClient(Failure);
        }
    }

    private sealed class FakeChatClient(Exception? failure) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            failure is null ? Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK"))) : Task.FromException<ChatResponse>(failure);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
