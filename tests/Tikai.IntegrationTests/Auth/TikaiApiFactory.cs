using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Tikai.IntegrationTests.Auth;

/// <summary>The real API host on its own empty database, so "first user" rules start from scratch.</summary>
public sealed class TikaiApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "tikai-tests", Guid.NewGuid().ToString("N"));

    private TikaiApiFactory(string connectionString) => _connectionString = connectionString;

    public static async Task<TikaiApiFactory> CreateAsync(PostgresFixture postgres)
    {
        var database = PostgresFixture.UniqueName("api");
        await using (var create = postgres.DataSource.CreateCommand($"CREATE DATABASE {database}"))
        {
            await create.ExecuteNonQueryAsync();
        }
        return new TikaiApiFactory(new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Db", _connectionString);
        builder.UseSetting("Tikai:DataPath", _dataPath);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }
}
