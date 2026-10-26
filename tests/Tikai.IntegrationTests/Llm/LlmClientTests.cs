using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Tikai.Core.Llm;
using Tikai.Llm;

namespace Tikai.IntegrationTests.Llm;

/// <summary>The real Anthropic SDK through <see cref="ChatClientFactory"/>, against a fake Messages API (no key, no network).</summary>
public sealed class LlmClientTests
{
    private const string Key = "sk-ant-test-0123456789";
    private readonly LlmConnectionTester _tester = new(new ChatClientFactory(), NullLogger<LlmConnectionTester>.Instance);

    [Fact]
    public void The_chat_client_is_set_up_for_the_connections_model_and_endpoint()
    {
        using var chat = new ChatClientFactory().Create(new LlmConnection(LlmProviderKind.Anthropic, Key, "claude-haiku-5-5", "https://gateway.example.org"));

        var metadata = chat.GetService<ChatClientMetadata>();

        Assert.NotNull(metadata);
        Assert.Equal("claude-haiku-5-5", metadata.DefaultModelId);
        Assert.Equal(new Uri("https://gateway.example.org"), metadata.ProviderUri);
    }

    [Fact]
    public async Task A_successful_round_trip_returns_the_reply_and_sends_only_the_given_key()
    {
        await using var server = await FakeAnthropicServer.StartAsync();

        var result = await _tester.TestAsync(Connection(server.BaseUrl), TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("OK", result.Reply);
        var request = Assert.Single(server.Requests);
        // API version 2026-10-21 (SDK 13) sends the key as a bearer token, not as X-Api-Key.
        Assert.Equal("Bearer " + Key, request.Authorization);
        Assert.Equal("", request.ApiKey);
        Assert.Equal("claude-opus-5-5", request.Model);
        Assert.NotEqual("", request.AnthropicVersion);
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized, "authentication_error", "rejected the API key")]
    [InlineData(StatusCodes.Status404NotFound, "not_found_error", "\"claude-opus-5-5\" was not found")]
    [InlineData(StatusCodes.Status403Forbidden, "permission_error", "not allowed")]
    public async Task Provider_errors_become_plain_messages(int status, string type, string expected)
    {
        await using var server = await FakeAnthropicServer.StartAsync(status, FakeAnthropicServer.Error(type, "nope"));

        var result = await _tester.TestAsync(Connection(server.BaseUrl), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Contains(expected, result.Message, StringComparison.Ordinal);
        Assert.Null(result.Reply);
    }

    [Fact]
    public async Task An_unreachable_endpoint_is_reported()
    {
        var result = await _tester.TestAsync(Connection($"http://127.0.0.1:{FreePort()}"), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Contains("Could not reach the provider", result.Message, StringComparison.Ordinal);
    }

    private static LlmConnection Connection(string baseUrl) => new(LlmProviderKind.Anthropic, Key, "claude-opus-5-5", baseUrl);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
