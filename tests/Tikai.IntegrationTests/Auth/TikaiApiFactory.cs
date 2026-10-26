using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Tikai.Data.Jobs;

namespace Tikai.IntegrationTests.Auth;

/// <summary>The real API host on its own empty database, so "first user" rules start from scratch.</summary>
public sealed class TikaiApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly IReadOnlyDictionary<string, string> _settings;

    private TikaiApiFactory(string connectionString, IReadOnlyDictionary<string, string> settings)
    {
        _connectionString = connectionString;
        _settings = settings;
    }

    /// <summary>The hub's data directory (keys, repository mirrors), deleted on dispose.</summary>
    public string DataPath { get; } = Path.Combine(Path.GetTempPath(), "tikai-tests", Guid.NewGuid().ToString("N"));

    public string ConnectionString => _connectionString;

    /// <param name="settings">Extra configuration, e.g. "Tikai:Git:FetchInterval".</param>
    public static async Task<TikaiApiFactory> CreateAsync(PostgresFixture postgres, IReadOnlyDictionary<string, string>? settings = null)
    {
        var database = PostgresFixture.UniqueName("api");
        await using (var create = postgres.DataSource.CreateCommand($"CREATE DATABASE {database}"))
        {
            await create.ExecuteNonQueryAsync();
        }
        return new TikaiApiFactory(
            new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString,
            settings ?? new Dictionary<string, string>());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Db", _connectionString);
        builder.UseSetting("Tikai:DataPath", DataPath);
        // Tests use file:// remotes (a second bare repo) and a plain-HTTP git server.
        builder.UseSetting("Tikai:Git:AllowedProtocols", "https,ssh,http,file");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureTestServices(services =>
            services.Configure<JobWorkerOptions>(o => o.PollInterval = TimeSpan.FromMilliseconds(100)));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(DataPath))
        {
            // Git writes read-only object files; on Windows they must be made writable before deleting.
            foreach (var file in Directory.EnumerateFiles(DataPath, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(DataPath, recursive: true);
        }
    }
}
