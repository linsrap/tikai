using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tikai.Core.Previews;
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
            table.HasCheckConstraint("ck_previews_ended", "(state IN ('stopped', 'failed')) = (stopped_at IS NOT NULL)");
            table.HasCheckConstraint("ck_previews_failed", "(state = 'failed') = (error IS NOT NULL)");
        });
        builder.Property(p => p.State).HasConversion(SnakeCaseEnum.Converter<PreviewState>()).HasMaxLength(20);
        builder.Property(p => p.HeadSha).HasMaxLength(64);
        builder.Property(p => p.Environment).HasMaxLength(40);
        builder.Property(p => p.Error).HasMaxLength(PreviewRules.MaxErrorLength);
        builder.Property(p => p.StopReason).HasMaxLength(200);
        builder.Property(p => p.Upstream).HasMaxLength(200);
        builder.Property(p => p.AppContainerId).HasMaxLength(100);
        builder.Ignore(p => p.IsActive);

        builder.HasOne<ChangeSet>().WithMany().HasForeignKey(p => p.ChangeSetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TikaiUser>().WithMany().HasForeignKey(p => p.StartedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => p.ChangeSetId);
        // One active preview per change set; a new start stops the old one first.
        var active = $"state IN ({string.Join(", ", PreviewRules.Active.Select(s => $"'{SnakeCaseEnum.ToName(s)}'"))})";
        builder.HasIndex(p => p.ChangeSetId, "ux_previews_active_change_set").IsUnique().HasFilter(active).HasDatabaseName("ux_previews_active_change_set");
        builder.HasIndex(p => p.State);
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
