using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Tikai.Data.Identity;

namespace Tikai.Api.Auth;

internal static class AuthEndpoints
{
    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");

        auth.MapGet("/status", GetStatusAsync);
        auth.MapGet("/antiforgery", IssueAntiforgeryToken);
        auth.MapPost("/signup", SignUpAsync);
        auth.MapPost("/login", LoginAsync);
        auth.MapPost("/logout", LogoutAsync).RequireAuthorization();
        auth.MapGet("/me", GetMeAsync).RequireAuthorization();
    }

    private static async Task<Ok<AuthStatus>> GetStatusAsync(
        ClaimsPrincipal principal, SignUpService signUp, UserManager<TikaiUser> users, CancellationToken cancellationToken)
    {
        var user = await users.GetUserAsync(principal);
        return TypedResults.Ok(new AuthStatus(
            await signUp.IsOpenAsync(cancellationToken),
            user is null ? null : await ToCurrentUserAsync(user, users)));
    }

    /// <summary>Sets the XSRF-TOKEN cookie. Call again after login, sign-up and logout: the token is bound to the user.</summary>
    private static NoContent IssueAntiforgeryToken(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append(AuthSetup.AntiforgeryCookieForScripts, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false, // the web app reads it and echoes it in the X-XSRF-TOKEN header
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/",
        });
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<CurrentUser>, ValidationProblem, ProblemHttpResult>> SignUpAsync(
        SignUpRequest request, SignUpService signUp, SignInManager<TikaiUser> signIn, UserManager<TikaiUser> users, CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        TikaiUser? user;
        try
        {
            user = await signUp.SignUpFirstAdminAsync(request, cancellationToken);
        }
        catch (SignUpFailedException ex)
        {
            return TypedResults.ValidationProblem(ex.Errors
                .GroupBy(e => e.Code.Contains("Password", StringComparison.Ordinal) ? "password" : "email")
                .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
        }

        if (user is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Sign-up is closed",
                detail: "An admin account already exists. Ask the admin for access.");
        }

        await signIn.SignInAsync(user, isPersistent: true);
        return TypedResults.Ok(await ToCurrentUserAsync(user, users));
    }

    private static async Task<Results<Ok<CurrentUser>, ProblemHttpResult>> LoginAsync(
        LoginRequest request, SignInManager<TikaiUser> signIn, UserManager<TikaiUser> users)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            return InvalidLogin();
        }

        var result = await signIn.PasswordSignInAsync(user, request.Password, isPersistent: true, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Too many failed logins",
                detail: "This account is locked for a few minutes. Try again later.");
        }
        return result.Succeeded ? TypedResults.Ok(await ToCurrentUserAsync(user, users)) : InvalidLogin();
    }

    private static async Task<NoContent> LogoutAsync(SignInManager<TikaiUser> signIn)
    {
        await signIn.SignOutAsync();
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<CurrentUser>, UnauthorizedHttpResult>> GetMeAsync(ClaimsPrincipal principal, UserManager<TikaiUser> users)
    {
        var user = await users.GetUserAsync(principal);
        return user is null ? TypedResults.Unauthorized() : TypedResults.Ok(await ToCurrentUserAsync(user, users));
    }

    private static ProblemHttpResult InvalidLogin() =>
        TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Wrong email or password");

    private static Dictionary<string, string[]> Validate(SignUpRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@', StringComparison.Ordinal))
        {
            errors["email"] = ["Enter a valid email address."];
        }
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            errors["displayName"] = ["Enter a display name."];
        }
        return errors;
    }

    private static async Task<CurrentUser> ToCurrentUserAsync(TikaiUser user, UserManager<TikaiUser> users) =>
        new(user.Id, user.Email!, user.DisplayName, await users.IsInRoleAsync(user, TikaiRoles.Admin));
}
