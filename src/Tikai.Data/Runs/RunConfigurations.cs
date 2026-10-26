using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tikai.Core.Attention;
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
        });
        builder.Property(r => r.State).HasConversion(SnakeCaseEnum.Converter<AgentRunState>()).HasMaxLength(20);
        builder.Property(r => r.ProgressStep).HasMaxLength(RunRules.MaxProgressStepLength);
        builder.Property(r => r.ProgressNote).HasMaxLength(RunRules.MaxProgressNoteLength);

        // Answers and decisions are the audit trail: a ticket with runs cannot be deleted.
        builder.HasOne<Ticket>().WithMany().HasForeignKey(r => r.TicketId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => r.TokenHash).IsUnique();
        builder.HasIndex(r => r.TicketId);
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
