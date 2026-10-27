using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tikai.Core.Attention;
using Tikai.Core.Llm;
using Tikai.Core.Projects;
using Tikai.Core.Runs;
using Tikai.Core.Tickets;
using Tikai.Data.Identity;

namespace Tikai.Data.Runs;

internal sealed class AgentRunConfiguration : IEntityTypeConfiguration<AgentRun>
{
    public void Configure(EntityTypeBuilder<AgentRun> builder)
    {
        builder.ToTable("agent_runs", table =>
        {
            table.HasCheckConstraint("ck_agent_runs_state", SnakeCaseEnum.CheckIn<AgentRunState>("state"));
            table.HasCheckConstraint("ck_agent_runs_token_hash", "octet_length(token_hash) = 32");
            table.HasCheckConstraint("ck_agent_runs_attempt_kind", SnakeCaseEnum.CheckIn<RunAttemptKind>("attempt_kind"));
        });
        builder.Property(r => r.State).HasConversion(SnakeCaseEnum.Converter<AgentRunState>()).HasMaxLength(20);
        builder.Property(r => r.AttemptKind).HasConversion(SnakeCaseEnum.Converter<RunAttemptKind>()).HasMaxLength(20)
            .HasDefaultValue(RunAttemptKind.Start).HasSentinel(RunAttemptKind.Start);
        builder.Property(r => r.ProgressStep).HasMaxLength(RunRules.MaxProgressStepLength);
        builder.Property(r => r.ProgressNote).HasMaxLength(RunRules.MaxProgressNoteLength);
        builder.Property(r => r.FailureReason).HasMaxLength(RunRules.MaxFailureReasonLength);
        builder.Property(r => r.Adapter).HasMaxLength(40);
        builder.Property(r => r.Model).HasMaxLength(100);
        builder.Property(r => r.Branch).HasMaxLength(200);
        builder.Property(r => r.BaseSha).HasMaxLength(64);
        builder.Property(r => r.ContainerId).HasMaxLength(64);
        builder.Property(r => r.SessionId).HasMaxLength(100);
        builder.Property(r => r.CostUsd).HasPrecision(12, 6);
        builder.Property(r => r.CostUsdBeforeSession).HasPrecision(12, 6);

        // Answers and decisions are the audit trail: a ticket with runs cannot be deleted.
        builder.HasOne<Ticket>().WithMany().HasForeignKey(r => r.TicketId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Repository>().WithMany().HasForeignKey(r => r.RepositoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<LlmProvider>().WithMany().HasForeignKey(r => r.LlmProviderId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<TikaiUser>().WithMany().HasForeignKey(r => r.StartedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => r.TokenHash).IsUnique();
        builder.HasIndex(r => r.TicketId);
        builder.HasIndex(r => r.RepositoryId);
        // Phase 1: one active run per ticket and per repository, also when two people press "assign" at once.
        var active = $"state NOT IN ({string.Join(", ", AgentRunStates.Ended.Select(s => $"'{SnakeCaseEnum.ToName(s)}'"))})";
        builder.HasIndex(r => r.TicketId, "ux_agent_runs_active_ticket").IsUnique().HasFilter(active).HasDatabaseName("ux_agent_runs_active_ticket");
        builder.HasIndex(r => r.RepositoryId, "ux_agent_runs_active_repository").IsUnique().HasFilter(active).HasDatabaseName("ux_agent_runs_active_repository");
    }
}

internal sealed class RunEventConfiguration : IEntityTypeConfiguration<RunEvent>
{
    public void Configure(EntityTypeBuilder<RunEvent> builder)
    {
        builder.ToTable("run_events", table =>
        {
            table.HasCheckConstraint("ck_run_events_kind", SnakeCaseEnum.CheckIn<RunEventKind>("kind"));
            table.HasCheckConstraint("ck_run_events_seq", "seq > 0");
        });
        builder.Property(e => e.Kind).HasConversion(SnakeCaseEnum.Converter<RunEventKind>()).HasMaxLength(20);
        builder.Property(e => e.Payload).HasColumnType("jsonb");
        builder.HasOne<AgentRun>().WithMany().HasForeignKey(e => e.RunId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(e => new { e.RunId, e.Seq }).IsUnique();
    }
}

internal sealed class AttentionItemConfiguration : IEntityTypeConfiguration<AttentionItem>
{
    public void Configure(EntityTypeBuilder<AttentionItem> builder)
    {
        builder.ToTable("attention_items", table =>
        {
            table.HasCheckConstraint("ck_attention_items_kind", SnakeCaseEnum.CheckIn<AttentionKind>("kind"));
            table.HasCheckConstraint("ck_attention_items_status", SnakeCaseEnum.CheckIn<AttentionStatus>("status"));
            table.HasCheckConstraint("ck_attention_items_answered",
                "(status = 'answered') = (answer IS NOT NULL AND answered_at IS NOT NULL)");
        });
        builder.Property(i => i.Kind).HasConversion(SnakeCaseEnum.Converter<AttentionKind>()).HasMaxLength(20);
        builder.Property(i => i.Status).HasConversion(SnakeCaseEnum.Converter<AttentionStatus>()).HasMaxLength(20);
        builder.Property(i => i.Payload).HasColumnType("jsonb");
        builder.Property(i => i.Answer).HasColumnType("jsonb");
        builder.Property(i => i.RequestHash).HasMaxLength(64);

        builder.HasOne<AgentRun>().WithMany().HasForeignKey(i => i.RunId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Ticket>().WithMany().HasForeignKey(i => i.TicketId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TikaiUser>().WithMany().HasForeignKey(i => i.AnsweredById).OnDelete(DeleteBehavior.Restrict);
        // Create-or-merge reads the run's latest item; the inbox lists open items.
        builder.HasIndex(i => new { i.RunId, i.CreatedAt });
        builder.HasIndex(i => i.CreatedAt).HasFilter("status = 'open'");
        builder.HasIndex(i => i.TicketId);
    }
}
