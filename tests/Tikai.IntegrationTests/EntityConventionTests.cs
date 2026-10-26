using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Tikai.Core.Common;
using Tikai.Data;

namespace Tikai.IntegrationTests;

/// <summary>
/// The conventions apply to every Entity. No real entity exists yet, so a test-only context adds one,
/// in its own database created with EnsureCreated.
/// </summary>
public sealed class EntityConventionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
    private NpgsqlDataSource _dataSource = null!;

    public async ValueTask InitializeAsync()
    {
        var database = PostgresFixture.UniqueName("conventions");
        await using (var admin = postgres.DataSource.CreateCommand($"CREATE DATABASE {database}"))
        {
            await admin.ExecuteNonQueryAsync();
        }
        _dataSource = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString).Build();
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();

    [Fact]
    public async Task New_entities_get_a_version_7_id_and_timestamps_from_the_time_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = CreateContext();
        var widget = new Widget { Name = "first" };
        db.Add(widget);
        await db.SaveChangesAsync(ct);

        Assert.Equal(7, widget.Id.Version);
        Assert.Equal(_time.GetUtcNow(), widget.CreatedAt);
        Assert.Equal(_time.GetUtcNow(), widget.UpdatedAt);

        await using var other = CreateContext();
        var loaded = await other.Set<Widget>().SingleAsync(w => w.Id == widget.Id, ct);
        Assert.Equal(_time.GetUtcNow(), loaded.CreatedAt);
    }

    [Fact]
    public async Task Updates_move_UpdatedAt_but_keep_CreatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = CreateContext();
        var widget = new Widget { Name = "before" };
        db.Add(widget);
        await db.SaveChangesAsync(ct);
        var created = widget.CreatedAt;

        _time.Advance(TimeSpan.FromMinutes(5));
        widget.Name = "after";
        widget.CreatedAt = DateTimeOffset.MinValue; // must not be written
        await db.SaveChangesAsync(ct);

        await using var other = CreateContext();
        var loaded = await other.Set<Widget>().SingleAsync(w => w.Id == widget.Id, ct);
        Assert.Equal(created, loaded.CreatedAt);
        Assert.Equal(created + TimeSpan.FromMinutes(5), loaded.UpdatedAt);
    }

    [Fact]
    public async Task Concurrent_updates_are_detected_with_xmin()
    {
        var ct = TestContext.Current.CancellationToken;
        Guid id;
        await using (var db = CreateContext())
        {
            var widget = new Widget { Name = "original" };
            db.Add(widget);
            await db.SaveChangesAsync(ct);
            id = widget.Id;
        }

        await using var first = CreateContext();
        await using var second = CreateContext();
        var a = await first.Set<Widget>().SingleAsync(w => w.Id == id, ct);
        var b = await second.Set<Widget>().SingleAsync(w => w.Id == id, ct);

        a.Name = "first writer";
        await first.SaveChangesAsync(ct);

        b.Name = "second writer";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(ct));
    }

    private ConventionsContext CreateContext() =>
        new(new DbContextOptionsBuilder<TikaiDbContext>()
            .UseTikai(_dataSource, new EntityTimestampsInterceptor(_time))
            .Options);

    internal sealed class Widget : Entity
    {
        public required string Name { get; set; }
    }

    private sealed class ConventionsContext(DbContextOptions options) : TikaiDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>().ToTable("widgets");
            base.OnModelCreating(modelBuilder);
        }
    }
}
