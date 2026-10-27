using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Tikai.Data.Jobs;
using Tikai.Orchestrator;

namespace Tikai.IntegrationTests.Auth;

/// <summary>The real API host on its own empty database, so "first user" rules start from scratch.</summary>
public sealed class TikaiApiFactory : WebApplicationFactory<Program>
{
    public const string TestSecret = "test-secret-0123456789-abcdefghijklmnop";

    /// <summary>
    /// Set to "on" to run the reconciler. Off by default: other tests make runs straight in the database that no job
    /// or container will ever take, which the reconciler would rightly fail.
    /// </summary>
    public const string ReconcilerSetting = "Test:Reconciler";

    private readonly string _connectionString;
    private readonly IReadOnlyDictionary<string, string> _settings;
    private bool _ownsDataPath;

    private TikaiApiFactory(string connectionString, IReadOnlyDictionary<string, string> settings, string? dataPath = null, bool ownsDataPath = true)
    {
        _connectionString = connectionString;
        _settings = settings;
        _ownsDataPath = ownsDataPath;
        DataPath = dataPath ?? Path.Combine(Path.GetTempPath(), "tikai-tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>The hub's data directory (keys, repository mirrors), deleted on dispose by the factory that created it.</summary>
    public string DataPath { get; }

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

    /// <summary>
    /// A second hub on the same database and data directory, like a restart with other settings. The new hub takes
    /// over the data directory (keys included), so disposing this one first does not delete it.
    /// </summary>
    public TikaiApiFactory Restart(IReadOnlyDictionary<string, string>? settings = null)
    {
        var next = new TikaiApiFactory(_connectionString, settings ?? _settings, DataPath, _ownsDataPath);
        _ownsDataPath = false;
        return next;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Db", _connectionString);
        builder.UseSetting("Tikai:DataPath", DataPath);
        builder.UseSetting("Tikai:EncryptionSecret", TestSecret);
        // Tests use file:// remotes (a second bare repo) and a plain-HTTP git server.
        builder.UseSetting("Tikai:Git:AllowedProtocols", "https,ssh,http,file");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureTestServices(services =>
        {
            services.Configure<JobWorkerOptions>(o => o.PollInterval = TimeSpan.FromMilliseconds(100));
            if (_settings.GetValueOrDefault(ReconcilerSetting) != "on")
            {
                foreach (var reconciler in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RunReconciler)).ToList())
                {
                    services.Remove(reconciler);
                }
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _ownsDataPath && Directory.Exists(DataPath))
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
