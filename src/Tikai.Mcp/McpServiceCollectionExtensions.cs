using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;

namespace Tikai.Mcp;

public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Arguments and results in snake_case with enums as strings ("plan_approval"), like the rest of Tikai's JSON.
    /// Based on the SDK's defaults, which carry the type info for its own protocol types.
    /// </summary>
    internal static JsonSerializerOptions ToolJson { get; } = CreateToolJson();

    private static JsonSerializerOptions CreateToolJson()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        // First, because the SDK's defaults already hold an enum converter without a naming policy ("Revise").
        options.Converters.Insert(0, new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.MakeReadOnly();
        return options;
    }

    /// <summary>Adds Tikai's agent tools. The host adds the transport and implements <see cref="IRunTools"/> and the attention waiter.</summary>
    public static IMcpServerBuilder WithTikaiTools(this IMcpServerBuilder builder) =>
        builder.WithTools<TikaiTools>(ToolJson);
}
