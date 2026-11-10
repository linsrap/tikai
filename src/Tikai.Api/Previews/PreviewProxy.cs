using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tikai.Core.Previews;
using Tikai.Data;
using Tikai.Orchestrator.Previews;
using Yarp.ReverseProxy.Forwarder;

namespace Tikai.Api.Previews;

/// <summary>
/// Serves preview hosts (<see cref="PreviewHosts"/>) before anything of the hub runs: a request for
/// <c>&lt;id&gt;.&lt;suffix&gt;</c> never reaches the hub's API, login or web app. The entry path trades a one-time token
/// from the review screen for a host-only session cookie; every other request needs that cookie and is forwarded (YARP,
/// also WebSockets) to the preview's app whose path prefix matches longest, path unchanged, without any of the hub's
/// cookies.
/// </summary>
internal sealed partial class PreviewProxy(
    RequestDelegate next,
    IHttpForwarder forwarder,
    IDataProtectionProvider dataProtection,
    PreviewUpstreamCache upstreamCache,
    IOptions<PreviewOptions> options,
    TimeProvider timeProvider,
    ILogger<PreviewProxy> logger)
{
    private const string SessionPurpose = "Tikai.Previews.Session";

    /// <summary>last_request_at is written at most this often per preview.</summary>
    private static readonly TimeSpan _touchInterval = TimeSpan.FromMinutes(1);

    private static readonly HttpMessageInvoker _client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        EnableMultipleHttp2Connections = true,
        ActivityHeadersPropagator = null,
        // The apps are on a local network: a connect that takes longer goes to a container that is being removed.
        ConnectTimeout = TimeSpan.FromSeconds(3),
    });

    private static readonly ForwarderRequestConfig _requestConfig = new() { ActivityTimeout = TimeSpan.FromMinutes(10) };

    private readonly ITimeLimitedDataProtector _sessions = dataProtection.CreateProtector(SessionPurpose).ToTimeLimitedDataProtector();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _touched = new();

    /// <summary>The suffix preview hosts are matched against (also when this request's hub host has no previews).</summary>
    public static string MatchSuffix(PreviewOptions options) => PreviewHosts.Suffix(options.Domain, "localhost")!;

    public async Task InvokeAsync(HttpContext context)
    {
        switch (PreviewHosts.Match(context.Request.Host.Host, MatchSuffix(options.Value), out var previewId))
        {
            case PreviewHostMatch.None:
                await next(context);
                return;
            case PreviewHostMatch.Unknown:
                await PageAsync(context, StatusCodes.Status404NotFound, "No such preview.");
                return;
        }
        if (context.Request.Path.Equals(PreviewHosts.EntryPath, StringComparison.Ordinal))
        {
            await EnterAsync(context, previewId);
            return;
        }
        if (!HasSession(context, previewId))
        {
            await PageAsync(context, StatusCodes.Status403Forbidden, "Open this preview from its review screen in Tikai.");
            return;
        }
        var apps = await UpstreamsAsync(context, previewId);
        if (apps.Count == 0)
        {
            await PageAsync(context, StatusCodes.Status503ServiceUnavailable, "This preview is not running. Start it again from its review screen.");
            return;
        }
        // The longest matching prefix; the path goes to the app unchanged.
        if (PreviewHosts.Route(apps, context.Request.Path.Value ?? "/") is not { } app)
        {
            await PageAsync(context, StatusCodes.Status404NotFound, "No app of this preview serves this path.");
            return;
        }
        await TouchAsync(context, previewId);
        if (context.Request.Headers.Cookie.ToString() is { Length: > 0 } cookies)
        {
            context.Request.Headers.Cookie = PreviewHosts.StripHubCookies(cookies);
        }
        var error = await forwarder.SendAsync(context, app.Url, _client, _requestConfig, HttpTransformer.Default);
        if (error != ForwarderError.None && !context.Response.HasStarted)
        {
            LogForwardFailed(previewId, app.Name, error);
            await PageAsync(context, StatusCodes.Status502BadGateway, $"The preview's app {app.Name} did not answer.");
        }
    }

    /// <summary>The entry link: a valid, unused, unexpired token for this preview sets the session cookie once.</summary>
    private async Task EnterAsync(HttpContext context, Guid previewId)
    {
        var token = context.Request.Query["t"].ToString();
        if (!HttpMethods.IsGet(context.Request.Method) || token.Length is 0 or > 200)
        {
            await PageAsync(context, StatusCodes.Status400BadRequest, "This is not a preview link.");
            return;
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var now = timeProvider.GetUtcNow();
        var db = context.RequestServices.GetRequiredService<TikaiDbContext>();
        var used = await db.PreviewEntries
            .Where(e => e.TokenHash == hash && e.PreviewId == previewId && e.UsedAt == null && e.ExpiresAt > now)
            .Where(e => db.Previews.Any(p => p.Id == previewId && p.State == PreviewState.Running))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.UsedAt, now).SetProperty(e => e.UpdatedAt, now), context.RequestAborted);
        if (used == 0)
        {
            await PageAsync(context, StatusCodes.Status403Forbidden, "This preview link was used already, has expired, or the preview stopped. Open the preview from its review screen again.");
            return;
        }
        var lifetime = options.Value.MaxLifetime;
        context.Response.Cookies.Append(PreviewHosts.SessionCookie, _sessions.Protect(previewId.ToString("N"), lifetime), new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = lifetime,
            IsEssential = true,
        });
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Redirect("/");
    }

    private bool HasSession(HttpContext context, Guid previewId)
    {
        if (context.Request.Cookies[PreviewHosts.SessionCookie] is not { Length: > 0 } cookie)
        {
            return false;
        }
        try
        {
            return _sessions.Unprotect(cookie) == previewId.ToString("N");
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The running preview's apps; empty when it does not run.</summary>
    private async Task<IReadOnlyList<PreviewAppUpstream>> UpstreamsAsync(HttpContext context, Guid previewId)
    {
        var now = timeProvider.GetUtcNow();
        if (upstreamCache.TryGet(previewId, now, out var apps))
        {
            return apps;
        }
        var db = context.RequestServices.GetRequiredService<TikaiDbContext>();
        var upstreams = await db.Previews.Where(p => p.Id == previewId && p.State == PreviewState.Running)
            .Select(p => p.Upstreams).SingleOrDefaultAsync(context.RequestAborted);
        apps = PreviewUpstreams.FromJson(upstreams);
        upstreamCache.Set(previewId, apps, now);
        return apps;
    }

    private async Task TouchAsync(HttpContext context, Guid previewId)
    {
        var now = timeProvider.GetUtcNow();
        if (_touched.TryGetValue(previewId, out var last) && now - last < _touchInterval)
        {
            return;
        }
        _touched[previewId] = now;
        var db = context.RequestServices.GetRequiredService<TikaiDbContext>();
        await db.Previews.Where(p => p.Id == previewId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastRequestAt, now), context.RequestAborted);
    }

    private static async Task PageAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(message, context.RequestAborted);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Preview {PreviewId}: forwarding to {App} failed ({Error})")]
    private partial void LogForwardFailed(Guid previewId, string app, ForwarderError error);
}
