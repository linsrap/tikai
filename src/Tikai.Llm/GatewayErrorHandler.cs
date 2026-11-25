using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tikai.Llm;

/// <summary>
/// Gives an error answer that is not shaped like Anthropic's (<c>{"error":{"type":…,"message":…}}</c>) that shape before the
/// SDK reads it. Gateways such as CLIProxyAPI answer <c>{"error":"Invalid API key"}</c>, and the SDK then throws an
/// <see cref="InvalidOperationException"/> instead of its status exception, so a rejected key looked like a crash.
/// </summary>
internal sealed class GatewayErrorHandler : DelegatingHandler
{
    private const int MaxMessageLength = 500;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }
        // ReadAsStringAsync buffers the content, so the SDK can still read a body that stays as it is.
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (Reshape(body) is { } reshaped)
        {
            response.Content.Dispose();
            response.Content = new StringContent(reshaped, Encoding.UTF8, "application/json");
        }
        return response;
    }

    /// <returns>Null when the SDK can read the body as it is: Anthropic's shape, an object without "error", or not JSON.</returns>
    internal static string? Reshape(string body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
        if (root is JsonObject obj && (!obj.TryGetPropertyValue("error", out var error) || error is JsonObject))
        {
            return null;
        }
        var message = root switch
        {
            JsonObject o when o["error"] is JsonValue v && v.TryGetValue(out string? text) => text,
            JsonObject o when o["message"] is JsonValue v && v.TryGetValue(out string? text) => text,
            _ => null,
        };
        var errorObject = new JsonObject { ["type"] = "api_error" };
        if (!string.IsNullOrWhiteSpace(message))
        {
            errorObject["message"] = message.Length <= MaxMessageLength ? message : message[..MaxMessageLength];
        }
        return new JsonObject { ["type"] = "error", ["error"] = errorObject }.ToJsonString();
    }
}
