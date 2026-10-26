using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tikai.Core.Git;
using Tikai.Core.Projects;
using Tikai.Data.Tickets;

namespace Tikai.Data.Projects;

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("projects");
        builder.Property(p => p.Name).HasMaxLength(200);
        builder.Property(p => p.Slug).HasMaxLength(ProjectSlug.MaxLength);
        builder.Property(p => p.Key).HasMaxLength(ProjectKey.MaxLength);
        builder.HasIndex(p => p.Slug).IsUnique();
        builder.HasIndex(p => p.Key).IsUnique();
        // Only TicketNumbers changes it, with SQL; code never sets it.
        builder.Property<int>(TicketNumbers.CounterProperty).HasDefaultValue(0).ValueGeneratedNever();
    }
}

internal sealed class RepositoryConfiguration : IEntityTypeConfiguration<Repository>
{
    public void Configure(EntityTypeBuilder<Repository> builder)
    {
        builder.ToTable("repositories", table =>
        {
            table.HasCheckConstraint("ck_repositories_status", SnakeCaseEnum.CheckIn<RepositoryStatus>("status"));
            table.HasCheckConstraint("ck_repositories_credential_kind", SnakeCaseEnum.CheckIn<CredentialKind>("credential_kind"));
            table.HasCheckConstraint("ck_repositories_credential_set", "(credential_kind = 'none') = (encrypted_credential IS NULL)");
            table.HasCheckConstraint("ck_repositories_credential_needs_remote", "remote_url IS NOT NULL OR credential_kind = 'none'");
        });
        builder.Property(r => r.Name).HasMaxLength(100);
        builder.Property(r => r.RemoteUrl).HasMaxLength(GitRemoteUrl.MaxLength);
        builder.Property(r => r.DefaultBranch).HasMaxLength(BranchName.MaxLength);
        builder.Property(r => r.Status).HasConversion(SnakeCaseEnum.Converter<RepositoryStatus>()).HasMaxLength(20);
        builder.Property(r => r.CredentialKind).HasConversion(SnakeCaseEnum.Converter<CredentialKind>()).HasMaxLength(20);

        builder.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.ProjectId, r.Name }).IsUnique();
        // The fetch scheduler looks for remote repositories that are due.
        builder.HasIndex(r => r.NextFetchAt)
            .HasDatabaseName("ix_repositories_fetch_due")
            .HasFilter("remote_url IS NOT NULL");
    }
}
