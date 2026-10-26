using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tikai.Core.Projects;
using Tikai.Core.Tickets;

namespace Tikai.Data.Tickets;

internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("tickets", table =>
        {
            table.HasCheckConstraint("ck_tickets_status", SnakeCaseEnum.CheckIn<TicketStatus>("status"));
            table.HasCheckConstraint("ck_tickets_priority", SnakeCaseEnum.CheckIn<TicketPriority>("priority"));
            table.HasCheckConstraint("ck_tickets_number_positive", "number > 0");
        });
        builder.Property(t => t.Title).HasMaxLength(TicketRules.MaxTitleLength);
        builder.Property(t => t.Description).HasMaxLength(TicketRules.MaxDescriptionLength);
        builder.Property(t => t.Status).HasConversion(SnakeCaseEnum.Converter<TicketStatus>()).HasMaxLength(20);
        builder.Property(t => t.Priority).HasConversion(SnakeCaseEnum.Converter<TicketPriority>()).HasMaxLength(20);

        builder.HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => new { t.ProjectId, t.Number }).IsUnique();
        builder.HasMany(t => t.Criteria).WithOne().HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AcceptanceCriterionConfiguration : IEntityTypeConfiguration<AcceptanceCriterion>
{
    public void Configure(EntityTypeBuilder<AcceptanceCriterion> builder)
    {
        builder.ToTable("acceptance_criteria", table => table.HasCheckConstraint("ck_acceptance_criteria_position", "position >= 0"));
        builder.Property(c => c.Text).HasMaxLength(TicketRules.MaxCriterionLength);
        builder.HasIndex(c => new { c.TicketId, c.Position });
    }
}
