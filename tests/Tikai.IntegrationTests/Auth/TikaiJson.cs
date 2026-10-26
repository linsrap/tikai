using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tikai.IntegrationTests.Auth;

/// <summary>The API's JSON settings: web defaults with enums as snake_case strings.</summary>
internal static class TikaiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
