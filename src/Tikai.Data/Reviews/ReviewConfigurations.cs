using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tikai.Core.Projects;
using Tikai.Core.Reviews;
using Tikai.Core.Runs;
using Tikai.Core.Tickets;
using Tikai.Data.Identity;

namespace Tikai.Data.Reviews;

internal sealed class ChangeSetConfiguration : IEntityTypeConfiguration<ChangeSet>
{
    public void Configure(EntityTypeBuilder<ChangeSet> builder)
    {
        builder.ToTable("change_sets", table => table.HasCheckConstraint("ck_change_sets_status", SnakeCaseEnum.CheckIn<ChangeSetStatus>("status")));
        builder.Property(c => c.Status).HasConversion(SnakeCaseEnum.Converter<ChangeSetStatus>()).HasMaxLength(20);
        builder.Property(c => c.BaseSha).HasMaxLength(64);
        builder.Property(c => c.HeadSha).HasMaxLength(64);

        // Reviews are the audit trail, like runs: nothing they point to can be deleted.
        builder.HasOne<Ticket>().WithMany().HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AgentRun>().WithMany().HasForeignKey(c => c.RunId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Repository>().WithMany().HasForeignKey(c => c.RepositoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => c.RunId).IsUnique();
        builder.HasIndex(c => c.TicketId);
        builder.HasIndex(c => c.RepositoryId);
    }
}

internal sealed class ReviewRoundConfiguration : IEntityTypeConfiguration<ReviewRound>
{
    public void Configure(EntityTypeBuilder<ReviewRound> builder)
    {
        builder.ToTable("review_rounds", table =>
        {
            table.HasCheckConstraint("ck_review_rounds_verdict", SnakeCaseEnum.CheckIn<ReviewVerdict>("verdict"));
            table.HasCheckConstraint("ck_review_rounds_round_no", "round_no > 0");
            table.HasCheckConstraint("ck_review_rounds_verdict_given", "(verdict IS NULL) = (verdict_at IS NULL)");
            table.HasCheckConstraint("ck_review_rounds_analysis_status", SnakeCaseEnum.CheckIn<AnalysisStatus>("analysis_status"));
            table.HasCheckConstraint("ck_review_rounds_analysis_done", "(analysis_status = 'done') = (analysis IS NOT NULL)");
        });
        builder.Property(r => r.Verdict).HasConversion(SnakeCaseEnum.Converter<ReviewVerdict>()).HasMaxLength(20);
        builder.Property(r => r.BaseSha).HasMaxLength(64);
        builder.Property(r => r.HeadSha).HasMaxLength(64);
        builder.Property(r => r.SelfReport).HasColumnType("jsonb");
        builder.Property(r => r.AnalysisStatus).HasConversion(SnakeCaseEnum.Converter<AnalysisStatus>()).HasMaxLength(20);
        builder.Property(r => r.Analysis).HasColumnType("jsonb");

        builder.HasOne<ChangeSet>().WithMany().HasForeignKey(r => r.ChangeSetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TikaiUser>().WithMany().HasForeignKey(r => r.VerdictById).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.ChangeSetId, r.RoundNo }).IsUnique();
    }
}

internal sealed class FileViewStateConfiguration : IEntityTypeConfiguration<FileViewState>
{
    public void Configure(EntityTypeBuilder<FileViewState> builder)
    {
        builder.ToTable("file_view_states");
        builder.Property(v => v.FilePath).HasMaxLength(ReviewRules.MaxFilePathLength);
        builder.Property(v => v.ViewedSha).HasMaxLength(64);

        builder.HasOne<TikaiUser>().WithMany().HasForeignKey(v => v.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ChangeSet>().WithMany().HasForeignKey(v => v.ChangeSetId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(v => new { v.UserId, v.ChangeSetId, v.FilePath }).IsUnique();
        builder.HasIndex(v => v.ChangeSetId);
    }
}
