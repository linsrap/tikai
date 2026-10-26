using System.Text.Json;
using Anthropic.Exceptions;
using Tikai.Core.Llm;

namespace Tikai.Llm;

/// <summary>Turns what goes wrong when calling a provider into a message for people.</summary>
internal static class LlmErrors
{
    /// <returns>Null lets the exception through (the caller cancelled).</returns>
    public static string? Describe(Exception ex, LlmConnection connection, TimeSpan limit, CancellationToken cancellationToken) => ex switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => null,
        OperationCanceledException => $"No answer within {limit.TotalSeconds:0} seconds.",
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
}
