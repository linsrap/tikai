using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tikai.Core.Llm;
using Tikai.Core.Projects;
using Tikai.Data.Identity;

namespace Tikai.Data.Llm;

internal sealed class LlmProviderConfiguration : IEntityTypeConfiguration<LlmProvider>
{
    public void Configure(EntityTypeBuilder<LlmProvider> builder)
    {
        builder.ToTable("llm_providers", table =>
        {
            // The owner is a user or a project: two nullable foreign keys, exactly one set.
            table.HasCheckConstraint("ck_llm_providers_one_owner", "(owner_user_id IS NULL) <> (owner_project_id IS NULL)");
            table.HasCheckConstraint("ck_llm_providers_kind", SnakeCaseEnum.CheckIn<LlmProviderKind>("kind"));
        });
        builder.Property(p => p.Name).HasMaxLength(LlmProviderRules.MaxNameLength);
        builder.Property(p => p.Kind).HasConversion(SnakeCaseEnum.Converter<LlmProviderKind>()).HasMaxLength(20);
        builder.Property(p => p.BaseUrl).HasMaxLength(LlmProviderRules.MaxBaseUrlLength);
        builder.Property(p => p.DefaultModel).HasMaxLength(LlmProviderRules.MaxModelLength);
        builder.Property(p => p.KeyHint).HasMaxLength(LlmProviderRules.KeyHintLength);

        builder.HasOne<TikaiUser>().WithMany().HasForeignKey(p => p.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Project>().WithMany().HasForeignKey(p => p.OwnerProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(p => new { p.OwnerUserId, p.Name }).IsUnique().HasFilter("owner_user_id IS NOT NULL");
        builder.HasIndex(p => new { p.OwnerProjectId, p.Name }).IsUnique().HasFilter("owner_project_id IS NOT NULL");
    }
}
