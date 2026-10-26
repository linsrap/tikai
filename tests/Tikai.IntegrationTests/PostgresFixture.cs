using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Tikai.Data;
using Tikai.Data.Jobs;

[assembly: AssemblyFixture(typeof(Tikai.IntegrationTests.PostgresFixture))]

namespace Tikai.IntegrationTests;

/// <summary>One Postgres 17 container for the whole test run, migrated once. Tests isolate themselves by unique queue and topic names.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    public TikaiDbContext CreateContext(TimeProvider? timeProvider = null) =>
        new(new DbContextOptionsBuilder<TikaiDbContext>()
            .UseTikai(DataSource, new EntityTimestampsInterceptor(timeProvider ?? TimeProvider.System))
            .Options);

    internal static PostgresJobQueue CreateQueue(TikaiDbContext db, JobQueueOptions? options = null) =>
        new(db, Options.Create(options ?? new JobQueueOptions()), TimeProvider.System);

    /// <summary>A unique queue or topic name: prefix plus 32 letters (hex digits mapped to g–p, so it is also a valid topic).</summary>
    public static string UniqueName(string prefix) =>
        prefix + "_" + string.Concat(Guid.NewGuid().ToString("N").Select(c => char.IsDigit(c) ? (char)('g' + (c - '0')) : c));
}
