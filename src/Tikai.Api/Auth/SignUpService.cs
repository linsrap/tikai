using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Tikai.Data;
using Tikai.Data.Identity;

namespace Tikai.Api.Auth;

/// <summary>The first person to sign up becomes admin; after that, sign-up is closed.</summary>
internal sealed class SignUpService(TikaiDbContext db, UserManager<TikaiUser> users, RoleManager<IdentityRole<Guid>> roles, TimeProvider timeProvider)
{
    // Any constant works; it only has to be the same for every sign-up.
    private const long SignUpLockKey = 0x5469_6b61_6953_6967; // "TikaiSig"

    public async Task<bool> IsOpenAsync(CancellationToken cancellationToken) =>
        !await db.Users.AnyAsync(cancellationToken);

    /// <returns>The new user, or null when sign-up is closed. Throws <see cref="SignUpFailedException"/> on invalid input.</returns>
    public async Task<TikaiUser?> SignUpFirstAdminAsync(SignUpRequest request, CancellationToken cancellationToken)
    {
        // The advisory lock serializes parallel sign-ups, so two people cannot both become the first admin.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({SignUpLockKey})", cancellationToken);
        if (await db.Users.AnyAsync(cancellationToken))
        {
            return null;
        }

        var user = new TikaiUser
        {
            Id = Guid.CreateVersion7(timeProvider.GetUtcNow()),
            UserName = request.Email.Trim(),
            Email = request.Email.Trim(),
            DisplayName = request.DisplayName.Trim(),
            CreatedAt = timeProvider.GetUtcNow(),
        };
        Check(await users.CreateAsync(user, request.Password));
        if (!await roles.RoleExistsAsync(TikaiRoles.Admin))
        {
            Check(await roles.CreateAsync(new IdentityRole<Guid>(TikaiRoles.Admin) { Id = Guid.CreateVersion7(timeProvider.GetUtcNow()) }));
        }
        Check(await users.AddToRoleAsync(user, TikaiRoles.Admin));

        await transaction.CommitAsync(cancellationToken);
        return user;
    }

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new SignUpFailedException(result.Errors.ToList());
        }
    }
}

internal sealed class SignUpFailedException(IReadOnlyList<IdentityError> errors)
    : Exception(string.Join(" ", errors.Select(e => e.Description)))
{
    public IReadOnlyList<IdentityError> Errors { get; } = errors;
}
