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

internal sealed class ReviewThreadConfiguration : IEntityTypeConfiguration<ReviewThread>
{
    public void Configure(EntityTypeBuilder<ReviewThread> builder)
    {
        builder.ToTable("review_threads", table =>
        {
            table.HasCheckConstraint("ck_review_threads_side", SnakeCaseEnum.CheckIn<DiffSide>("side"));
            table.HasCheckConstraint("ck_review_threads_status", SnakeCaseEnum.CheckIn<ThreadStatus>("status"));
            // Anchored (path, side and lines) or general (none of them).
            table.HasCheckConstraint("ck_review_threads_anchor",
                "(file_path IS NULL AND side IS NULL AND line_start IS NULL AND line_end IS NULL) OR " +
                "(file_path IS NOT NULL AND side IS NOT NULL AND line_start >= 1 AND line_end >= line_start)");
            table.HasCheckConstraint("ck_review_threads_resolved", "(status = 'resolved') = (resolved_at IS NOT NULL)");
        });
        builder.Property(t => t.FilePath).HasMaxLength(ReviewRules.MaxFilePathLength);
        builder.Property(t => t.Side).HasConversion(SnakeCaseEnum.Converter<DiffSide>()).HasMaxLength(10);
        builder.Property(t => t.Status).HasConversion(SnakeCaseEnum.Converter<ThreadStatus>()).HasMaxLength(20);

        builder.HasOne<ChangeSet>().WithMany().HasForeignKey(t => t.ChangeSetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ReviewRound>().WithMany().HasForeignKey(t => t.ReviewRoundId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TikaiUser>().WithMany().HasForeignKey(t => t.CreatedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TikaiUser>().WithMany().HasForeignKey(t => t.ResolvedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => t.ChangeSetId);
        builder.HasIndex(t => t.ReviewRoundId);
    }
}

internal sealed class ReviewCommentConfiguration : IEntityTypeConfiguration<ReviewComment>
{
    public void Configure(EntityTypeBuilder<ReviewComment> builder)
    {
        builder.ToTable("review_comments", table =>
        {
            table.HasCheckConstraint("ck_review_comments_action", SnakeCaseEnum.CheckIn<CommentAction>("action"));
            table.HasCheckConstraint("ck_review_comments_author", "(author_user_id IS NULL) <> (author_run_id IS NULL)");
            table.HasCheckConstraint("ck_review_comments_sent", "sent_at IS NULL OR action = 'send_to_agent'");
        });
        builder.Property(c => c.Body).HasMaxLength(ThreadRules.MaxBodyLength);
        builder.Property(c => c.Action).HasConversion(SnakeCaseEnum.Converter<CommentAction>()).HasMaxLength(20);

        builder.HasOne<ReviewThread>().WithMany().HasForeignKey(c => c.ThreadId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<TikaiUser>().WithMany().HasForeignKey(c => c.AuthorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AgentRun>().WithMany().HasForeignKey(c => c.AuthorRunId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => c.ThreadId);
        builder.HasIndex(c => c.AuthorUserId);
        builder.HasIndex(c => c.AuthorRunId);
    }
}
