using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Tikai.Data;

/// <summary>
/// Used only by `dotnet ef` (migrations add/script). Adding a migration needs no running database;
/// set TIKAI_DESIGN_DB to point `dotnet ef database update` at one.
/// </summary>
internal sealed class TikaiDbContextFactory : IDesignTimeDbContextFactory<TikaiDbContext>
{
    public TikaiDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("TIKAI_DESIGN_DB")
            ?? "Host=localhost;Port=5432;Database=tikai;Username=tikai;Password=tikai";
        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        return new TikaiDbContext(new DbContextOptionsBuilder<TikaiDbContext>().UseTikai(dataSource).Options);
    }
}
