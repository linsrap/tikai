using System.Diagnostics;
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
        catch (Exception ex) when (LlmErrors.Describe(ex, connection, Limit, cancellationToken) is { } message)
        {
            LogTestFailed(connection.Kind, connection.Model, ex.GetType().Name, message);
            return new LlmTestResult(false, message, null, stopwatch.Elapsed);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection test for {Kind} model {Model} failed with {ErrorType}: {Reason}")]
    private partial void LogTestFailed(LlmProviderKind kind, string model, string errorType, string reason);
}
