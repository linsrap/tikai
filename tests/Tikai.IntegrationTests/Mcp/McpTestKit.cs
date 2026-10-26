using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Tikai.Api.Attention;
using Tikai.Api.Runs;
using Tikai.Api.Tickets;
using Tikai.IntegrationTests.Auth;

namespace Tikai.IntegrationTests.Mcp;

/// <summary>What the orchestrator will do (create a run, hand its token to an agent) and what the agent does (MCP calls).</summary>
internal static class McpTestKit
{
    /// <summary>A heartbeat short enough that tests see progress notifications and re-checks.</summary>
    public static readonly Dictionary<string, string> FastHeartbeat = new() { ["Tikai:Mcp:HeartbeatInterval"] = "00:00:00.200" };

    public static CancellationToken Timeout(int seconds = 30) =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token).Token;

    /// <summary>A project with one ticket (two criteria), created through the API.</summary>
    public static async Task<TicketInfo> CreateTicketAsync(BrowserClient browser, string slug, string key, string title)
    {
        using var project = await browser.PostAsync("/api/projects", new { name = slug, slug, key });
        project.EnsureSuccessStatusCode();
        using var created = await browser.PostAsync($"/api/projects/{slug}/tickets", new
        {
            title,
            description = $"Description of *{title}*",
            criteria = new[] { $"{title}: first", $"{title}: second" },
        });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<TicketInfo>(TikaiJson.Options))!;
    }

    /// <summary>A queued run for the ticket and its token, the way the orchestrator gets them.</summary>
    public static async Task<(Guid RunId, string Token)> StartRunAsync(TikaiApiFactory factory, Guid ticketId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var (run, token) = await scope.ServiceProvider.GetRequiredService<RunService>().CreateAsync(ticketId, CancellationToken.None);
        return (run.Id, token);
    }

    public static async Task<McpClient> ConnectAsync(TikaiApiFactory factory, string token)
    {
        var http = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
            },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Timeout());
    }

    /// <summary>A raw JSON-RPC POST to /mcp, for checks below the SDK (status codes, headers).</summary>
    public static async Task<HttpResponseMessage> PostRawAsync(HttpClient http, string? authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }
        return await http.SendAsync(request);
    }

    public static Task<CallToolResult> CallAsync(
        this McpClient client, string tool, object? arguments = null, IProgress<ProgressNotificationValue>? progress = null, CancellationToken? cancellationToken = null) =>
        client.CallToolAsync(
            tool,
            arguments is null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments)),
            progress,
            cancellationToken: cancellationToken ?? Timeout()).AsTask();

    /// <summary>The tool's text result; a tool error fails the test with its message.</summary>
    public static string Text(this CallToolResult result)
    {
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.False(result.IsError == true, $"Tool error: {text}");
        return text;
    }

    public static JsonElement Json(this CallToolResult result) => JsonDocument.Parse(result.Text()).RootElement;

    public static string Error(this CallToolResult result)
    {
        Assert.True(result.IsError, "Expected a tool error.");
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }

    /// <summary>Polls the inbox until it lists <paramref name="count"/> open items (newest first).</summary>
    public static async Task<List<AttentionItemInfo>> WaitForOpenItemsAsync(BrowserClient browser, int count = 1)
    {
        var cancellationToken = Timeout();
        while (true)
        {
            var items = await browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items");
            if (items.Count >= count)
            {
                return items;
            }
            await Task.Delay(50, cancellationToken);
        }
    }

    public static async Task AnswerAsync(BrowserClient browser, Guid itemId, object answer)
    {
        using var response = await browser.PostAsync($"/api/attention-items/{itemId}/answer", answer);
        Assert.True(response.IsSuccessStatusCode, $"Answer failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }
}
