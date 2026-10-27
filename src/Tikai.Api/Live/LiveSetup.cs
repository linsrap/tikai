using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace Tikai.Api.Live;

/// <summary>The SignalR hub at <c>/hubs/live</c>: login cookie only, same-origin only.</summary>
internal static class LiveSetup
{
    public const string Path = "/hubs/live";

    public static IServiceCollection AddTikaiLive(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddHostedService<LiveUpdates>();
        return services;
    }

    /// <summary>
    /// Refuses hub requests from other origins. The auth cookie is SameSite=Lax, so a cross-site WebSocket would not
    /// carry it anyway; this is a second lock against cross-site WebSocket hijacking. Requests without an Origin
    /// (not from a browser) pass and still need the cookie.
    /// </summary>
    public static IApplicationBuilder UseLiveOriginCheck(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/hubs") && !SameOrigin(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });

    public static void MapTikaiLive(this IEndpointRouteBuilder app) =>
        app.MapHub<LiveHub>(Path)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = IdentityConstants.ApplicationScheme });

    /// <summary>No Origin header, or one whose host and port are the request's own (behind a proxy: the forwarded Host).</summary>
    internal static bool SameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            return true;
        }
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}", request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
