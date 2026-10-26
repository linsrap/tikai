using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Tikai.IntegrationTests.Llm;

/// <summary>Stands in for api.anthropic.com: answers POST /v1/messages with a fixed status and body, and records each request.</summary>
internal sealed class FakeAnthropicServer : IAsyncDisposable
{
    public const string OkReply = """
        {"id":"msg_test","type":"message","role":"assistant","model":"claude-opus-5-5",
         "content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":12,"output_tokens":1}}
        """;

    private readonly WebApplication _app;

    private FakeAnthropicServer(WebApplication app) => _app = app;

    public string BaseUrl => _app.Urls.Single();

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public static async Task<FakeAnthropicServer> StartAsync(int status = StatusCodes.Status200OK, string body = OkReply)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new FakeAnthropicServer(app);
        app.MapPost("/v1/messages", async context =>
        {
            using var request = await JsonDocument.ParseAsync(context.Request.Body);
            server.Requests.Enqueue(new RecordedRequest(
                context.Request.Headers["x-api-key"].ToString(),
                context.Request.Headers.Authorization.ToString(),
                context.Request.Headers["anthropic-version"].ToString(),
                request.RootElement.GetProperty("model").GetString()!,
                request.RootElement.Clone()));
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(body);
        });
        await app.StartAsync();
        return server;
    }

    /// <summary>A successful reply whose only content is the given text.</summary>
    public static string TextReply(string text, string stopReason = "end_turn") =>
        JsonSerializer.Serialize(new
        {
            id = "msg_test",
            type = "message",
            role = "assistant",
            model = "claude-opus-5-5",
            content = new[] { new { type = "text", text } },
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            usage = new { input_tokens = 100, output_tokens = 50 },
        });

    public static string Error(string type, string message) =>
        JsonSerializer.Serialize(new { type = "error", error = new { type, message } });

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    internal sealed record RecordedRequest(string ApiKey, string Authorization, string AnthropicVersion, string Model, JsonElement Body);
}
