using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tikai.Core.Previews;
using Tikai.Core.Projects;
using Tikai.Core.Reviews;
using Tikai.Data.Identity;

namespace Tikai.Data.Previews;

internal sealed class PreviewConfiguration : IEntityTypeConfiguration<Preview>
{
    public void Configure(EntityTypeBuilder<Preview> builder)
    {
        builder.ToTable("previews", table =>
        {
            table.HasCheckConstraint("ck_previews_state", SnakeCaseEnum.CheckIn<PreviewState>("state"));
            table.HasCheckConstraint("ck_previews_round_no", "round_no > 0");
            // A round's preview (change set and round) or a branch preview (branch), never both.
            table.HasCheckConstraint("ck_previews_target",
                "(change_set_id IS NOT NULL AND round_no IS NOT NULL AND branch IS NULL) OR (change_set_id IS NULL AND round_no IS NULL AND branch IS NOT NULL)");
            table.HasCheckConstraint("ck_previews_ended", "(state IN ('stopped', 'failed')) = (stopped_at IS NOT NULL)");
            table.HasCheckConstraint("ck_previews_failed", "(state = 'failed') = (error IS NOT NULL)");
        });
        builder.Property(p => p.State).HasConversion(SnakeCaseEnum.Converter<PreviewState>()).HasMaxLength(20);
        builder.Property(p => p.HeadSha).HasMaxLength(64);
        builder.Property(p => p.Branch).HasMaxLength(255);
        builder.Property(p => p.Environment).HasMaxLength(40);
        builder.Property(p => p.Error).HasMaxLength(PreviewRules.MaxErrorLength);
        builder.Property(p => p.StopReason).HasMaxLength(200);
        builder.Property(p => p.Upstreams).HasColumnType("jsonb");
        builder.Ignore(p => p.IsActive);

        builder.HasOne<ChangeSet>().WithMany().HasForeignKey(p => p.ChangeSetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Repository>().WithMany().HasForeignKey(p => p.RepositoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TikaiUser>().WithMany().HasForeignKey(p => p.StartedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => p.ChangeSetId);
        // One active preview per change set; a new start stops the old one first.
        var active = $"state IN ({string.Join(", ", PreviewRules.Active.Select(s => $"'{SnakeCaseEnum.ToName(s)}'"))})";
        builder.HasIndex(p => p.ChangeSetId, "ux_previews_active_change_set").IsUnique().HasFilter(active).HasDatabaseName("ux_previews_active_change_set");
        builder.HasIndex(p => p.RepositoryId);
        // One active branch preview per repository.
        builder.HasIndex(p => p.RepositoryId, "ux_previews_active_branch").IsUnique()
            .HasFilter($"branch IS NOT NULL AND {active}").HasDatabaseName("ux_previews_active_branch");
        builder.HasIndex(p => p.State);
    }
}

internal sealed class PreviewEntryConfiguration : IEntityTypeConfiguration<PreviewEntry>
{
    public void Configure(EntityTypeBuilder<PreviewEntry> builder)
    {
        builder.ToTable("preview_entries");
        builder.Property(e => e.TokenHash).HasMaxLength(64);
        builder.HasOne<Preview>().WithMany().HasForeignKey(e => e.PreviewId).OnDelete(DeleteBehavior.Cascade);
        // Who opened a preview stays as long as the preview.
        builder.HasOne<TikaiUser>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => e.TokenHash).IsUnique();
        builder.HasIndex(e => e.PreviewId);
    }
}

internal sealed class PreviewLogLineConfiguration : IEntityTypeConfiguration<PreviewLogLine>
{
    public void Configure(EntityTypeBuilder<PreviewLogLine> builder)
    {
        builder.ToTable("preview_log_lines");
        builder.Property(l => l.Source).HasMaxLength(PreviewRules.MaxSourceLength);
        builder.HasOne<Preview>().WithMany().HasForeignKey(l => l.PreviewId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(l => new { l.PreviewId, l.Seq }).IsUnique();
    }
}
