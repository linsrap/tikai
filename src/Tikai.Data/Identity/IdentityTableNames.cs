using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Tikai.Data.Identity;

internal static class IdentityTableNames
{
    /// <summary>
    /// Plain table names instead of Identity's AspNet* defaults (design doc: users), and snake_case
    /// names for the indexes Identity names itself (UserNameIndex, EmailIndex, RoleNameIndex).
    /// </summary>
    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TikaiUser>(user =>
        {
            user.ToTable("users");
            user.Property(u => u.DisplayName).HasMaxLength(200);
            user.HasIndex(u => u.NormalizedUserName).HasDatabaseName("ix_users_normalized_user_name");
            user.HasIndex(u => u.NormalizedEmail).HasDatabaseName("ix_users_normalized_email");
        });
        modelBuilder.Entity<IdentityRole<Guid>>(role =>
        {
            role.ToTable("roles");
            role.HasIndex(r => r.NormalizedName).HasDatabaseName("ix_roles_normalized_name");
        });
        modelBuilder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        modelBuilder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
    }
}
