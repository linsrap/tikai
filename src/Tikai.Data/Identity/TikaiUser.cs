using Microsoft.AspNetCore.Identity;

namespace Tikai.Data.Identity;

/// <summary>A person who logs in to Tikai. Phase 1 has local accounts only; OIDC comes later.</summary>
public class TikaiUser : IdentityUser<Guid>
{
    public required string DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public static class TikaiRoles
{
    public const string Admin = "admin";
}
