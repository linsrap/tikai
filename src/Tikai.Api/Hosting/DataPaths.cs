namespace Tikai.Api.Hosting;

internal static class DataPaths
{
    /// <summary>
    /// The hub's data directory (Tikai:DataPath): /data in the hub image (the data volume); a relative path
    /// (development) is under the content root.
    /// </summary>
    public static string Resolve(IConfiguration configuration, IHostEnvironment environment) =>
        Path.Combine(environment.ContentRootPath, configuration["Tikai:DataPath"] ?? "/data");
}
