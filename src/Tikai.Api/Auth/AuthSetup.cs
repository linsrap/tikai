using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Tikai.Api.Hosting;
using Tikai.Api.Security;
using Tikai.Core.Security;
using Tikai.Data;
using Tikai.Data.Identity;

namespace Tikai.Api.Auth;

internal static class AuthSetup
{
    public const string AuthCookie = "tikai.auth";
    public const string AntiforgeryCookie = "tikai.af";
    public const string AntiforgeryCookieForScripts = "XSRF-TOKEN";
    public const string AntiforgeryHeader = "X-XSRF-TOKEN";

    public static IServiceCollection AddTikaiAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Keys sign the auth and antiforgery cookies; persisting them keeps people logged in across restarts.
        // They also encrypt stored secrets (ISecretProtector). Encrypting the key ring with TIKAI_SECRET comes with step 06.
        services.AddDataProtection()
            .SetApplicationName("Tikai")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(DataPaths.Resolve(configuration, environment), "keys")));
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();

        services.AddIdentityCore<TikaiUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                // Length over composition rules (NIST SP 800-63B).
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<TikaiDbContext>()
            .AddSignInManager();

        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, options =>
            {
                options.Cookie.Name = AuthCookie;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromDays(14);
                options.SlidingExpiration = true;
                // The web app handles login; the API answers with status codes, never redirects.
                options.Events.OnRedirectToLogin = context => Reply(context.Response, StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = context => Reply(context.Response, StatusCodes.Status403Forbidden);
            });
        services.AddAuthorization();

        services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryHeader;
            options.Cookie.Name = AntiforgeryCookie;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

        services.AddScoped<SignUpService>();
        return services;
    }

    private static Task Reply(HttpResponse response, int statusCode)
    {
        response.StatusCode = statusCode;
        return Task.CompletedTask;
    }
}
