using System.Reflection;

namespace Tikai.Api.Hosting;

internal static class BuildTimeTools
{
    /// <summary>
    /// True while the OpenAPI document is generated at build time. That tool runs Program up to app.Run,
    /// so startup work that needs a database (migrations) must be skipped.
    /// </summary>
    public static bool IsGeneratingOpenApiDocument =>
        Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
}
