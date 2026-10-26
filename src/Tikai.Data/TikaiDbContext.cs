using Microsoft.EntityFrameworkCore;
using Tikai.Core.Common;
using Tikai.Data.Jobs;

namespace Tikai.Data;

public class TikaiDbContext(DbContextOptions options) : DbContext(options)
{
    internal DbSet<JobRecord> Jobs => Set<JobRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TikaiDbContext).Assembly);
        ApplyEntityConventions(modelBuilder);
    }

    // Every Entity gets a client-generated uuid key, timestamps and xmin as concurrency token.
    private static void ApplyEntityConventions(ModelBuilder modelBuilder)
    {
        foreach (var type in modelBuilder.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)))
        {
            var entity = modelBuilder.Entity(type.ClrType);
            entity.HasKey(nameof(Entity.Id));
            entity.Property(nameof(Entity.Id)).ValueGeneratedNever();
            entity.Property<uint>("xmin").HasColumnType("xid").IsRowVersion();
        }
    }
}
