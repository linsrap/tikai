using System.Diagnostics;
using System.Text.Json;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Tikai.Core.Llm;

namespace Tikai.Llm;

/// <param name="Message">For people: "Connected." or what went wrong.</param>
/// <param name="Reply">What the model answered, when it answered.</param>
public sealed record LlmTestResult(bool Ok, string Message, string? Reply, TimeSpan Duration);

/// <summary>"Test connection": one short chat round trip through the same IChatClient the features use.</summary>
public sealed partial class LlmConnectionTester(IChatClientFactory factory, ILogger<LlmConnectionTester> logger)
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);
    internal const string Prompt = "This is a connection test. Reply with the single word OK.";

    public async Task<LlmTestResult> TestAsync(LlmConnection connection, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Limit);
        try
        {
            using var chat = factory.Create(connection);
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.User, Prompt)],
                new ChatOptions { MaxOutputTokens = 1024 },
                timeout.Token);
            return new LlmTestResult(true, "Connected.", response.Text.Trim(), stopwatch.Elapsed);
        }
        catch (Exception ex) when (Describe(ex, connection, cancellationToken) is { } message)
        {
            LogTestFailed(connection.Kind, connection.Model, ex.GetType().Name, message);
            return new LlmTestResult(false, message, null, stopwatch.Elapsed);
        }
    }

    // Null lets the exception through (the caller cancelled).
    private static string? Describe(Exception ex, LlmConnection connection, CancellationToken cancellationToken) => ex switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => null,
        OperationCanceledException => $"No answer within {Limit.TotalSeconds:0} seconds.",
        AnthropicApiException api => Summary(api, connection) + ProviderSays(api),
        AnthropicIOException io => $"Could not reach the provider: {io.Message}",
        _ => $"Unexpected error: {ex.Message}",
    };

    private static string Summary(AnthropicApiException ex, LlmConnection connection) => ex switch
    {
        AnthropicUnauthorizedException => "The provider rejected the API key.",
        AnthropicForbiddenException => "The API key is not allowed to use this model or endpoint.",
        AnthropicNotFoundException => $"The model \"{connection.Model}\" was not found (or the base URL is wrong).",
        AnthropicRateLimitException => "The provider is rate limiting this key, or it is out of credit.",
        AnthropicBadRequestException => "The provider refused the request.",
        Anthropic5xxException => "The provider had an internal error. Try again later.",
        _ => $"The provider answered with status {(int)ex.StatusCode}.",
    };

    /// <summary>The provider's own explanation (error.message in the JSON body), which says what to do, e.g. buy credit.</summary>
    private static string ProviderSays(AnthropicApiException ex)
    {
        try
        {
            using var body = JsonDocument.Parse(ex.ResponseBody);
            return body.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message)
                && message.GetString() is { Length: > 0 } text
                    ? $" Anthropic says: {text}"
                    : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection test for {Kind} model {Model} failed with {ErrorType}: {Reason}")]
    private partial void LogTestFailed(LlmProviderKind kind, string model, string errorType, string reason);
}
