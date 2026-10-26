using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Tikai.Data.Jobs;

/// <summary>Row of the jobs table. Only migrations use this mapping; the queue works with SQL (FOR UPDATE SKIP LOCKED).</summary>
internal sealed class JobRecord
{
    public Guid Id { get; set; }
    public required string Queue { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset RunAt { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? DeadAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class JobRecordConfiguration : IEntityTypeConfiguration<JobRecord>
{
    public void Configure(EntityTypeBuilder<JobRecord> builder)
    {
        builder.ToTable("jobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();
        builder.Property(j => j.Queue).HasMaxLength(100);
        builder.Property(j => j.Payload).HasColumnType("jsonb");
        builder.Property(j => j.LockedBy).HasMaxLength(200);
        builder.Property(j => j.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(j => j.UpdatedAt).HasDefaultValueSql("now()");
        builder.ToTable(t => t.HasCheckConstraint("ck_jobs_attempts", "attempts >= 0 AND max_attempts >= 1"));

        // Dequeue scans open jobs of one queue by due time.
        builder.HasIndex(j => new { j.Queue, j.RunAt })
            .HasDatabaseName("ix_jobs_open_by_queue")
            .HasFilter("dead_at IS NULL");
    }
}
