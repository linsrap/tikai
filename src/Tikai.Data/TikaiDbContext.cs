using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Tikai.Core.Common;
using Tikai.Core.Projects;
using Tikai.Data.Identity;
using Tikai.Data.Jobs;

namespace Tikai.Data;

public class TikaiDbContext(DbContextOptions options)
    : IdentityDbContext<TikaiUser, IdentityRole<Guid>, Guid>(options)
{
    internal DbSet<JobRecord> Jobs => Set<JobRecord>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<Repository> Repositories => Set<Repository>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        IdentityTableNames.Apply(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(TikaiDbContext).Assembly);
        ApplyEntityConventions(builder);
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
