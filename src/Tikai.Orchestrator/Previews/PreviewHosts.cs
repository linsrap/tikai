using System.Globalization;
using System.Net;
using Tikai.Core.Previews;

namespace Tikai.Orchestrator.Previews;

public enum PreviewHostMatch
{
    /// <summary>Not under the preview suffix: one of the hub's own hosts.</summary>
    None,

    /// <summary>Exactly <c>&lt;32 hex digits&gt;.&lt;suffix&gt;</c>.</summary>
    Preview,

    /// <summary>Under the suffix but no preview's name: never served by the hub.</summary>
    Unknown,
}

/// <summary>
/// Each preview has its own origin, <c>&lt;id&gt;.&lt;suffix&gt;</c>, never the hub's: cookies ignore ports, so on the hub's
/// host the app would get the hub's login cookie. Without a configured domain the suffix is <c>preview.localhost</c>,
/// which browsers resolve to the loopback without DNS; that only fits a hub opened on localhost.
/// </summary>
public static class PreviewHosts
{
    public const string LocalSuffix = "preview.localhost";

    /// <summary>The entry path on a preview host (<c>?t=&lt;one-time token&gt;</c>).</summary>
    public const string EntryPath = "/__tikai/enter";

    /// <summary>The cookie a preview host requires; set by the entry path, host-only, never forwarded to the app.</summary>
    public const string SessionCookie = "tikai_preview";

    /// <summary>The suffix of preview hosts, or null when this hub cannot serve previews (no domain, not on localhost).</summary>
    /// <param name="hubHost">The host the hub is opened on (without port).</param>
    public static string? Suffix(string? domain, string hubHost)
    {
        if (!string.IsNullOrWhiteSpace(domain))
        {
            return domain.Trim().Trim('.').ToLowerInvariant();
        }
        var host = hubHost.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))
            ? LocalSuffix
            : null;
    }

    /// <summary>The preview's origin with the hub's scheme and port.</summary>
    public static string Origin(Guid previewId, string suffix, string scheme, int? port) =>
        $"{scheme}://{previewId:N}.{suffix}{(port is { } p ? ":" + p.ToString(CultureInfo.InvariantCulture) : "")}";

    /// <param name="host">The request's host, without port.</param>
    public static PreviewHostMatch Match(string host, string suffix, out Guid previewId)
    {
        previewId = Guid.Empty;
        var name = host.TrimEnd('.');
        if (name.Equals(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return PreviewHostMatch.Unknown;
        }
        if (!name.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
        {
            return PreviewHostMatch.None;
        }
        var label = name[..^(suffix.Length + 1)];
        return label.Length == 32 && Guid.TryParseExact(label, "N", out previewId) ? PreviewHostMatch.Preview : PreviewHostMatch.Unknown;
    }

    /// <summary>
    /// The app that serves <paramref name="path"/>: the longest prefix that is the path itself or followed by a slash
    /// (<c>/api</c> serves <c>/api</c> and <c>/api/x</c>, not <c>/apix</c>), compared ordinally; <c>/</c> serves the
    /// rest. The path is forwarded unchanged. Null when no app is on <c>/</c> and nothing else matches.
    /// </summary>
    /// <param name="path">The request's path (decoded as ASP.NET Core gives it, so an encoded slash stays one segment).</param>
    public static PreviewAppUpstream? Route(IReadOnlyList<PreviewAppUpstream> apps, string path)
    {
        if (path.Length == 0)
        {
            path = "/";
        }
        PreviewAppUpstream? best = null;
        foreach (var app in apps)
        {
            var matches = app.Path == "/"
                || (path.StartsWith(app.Path, StringComparison.Ordinal) && (path.Length == app.Path.Length || path[app.Path.Length] == '/'));
            if (matches && (best is null || app.Path.Length > best.Path.Length))
            {
                best = app;
            }
        }
        return best;
    }

    /// <summary>
    /// The Cookie header without the hub's cookies (every name starting with "tikai", including the preview session);
    /// null when nothing is left. They are host-only on the hub's host and should never arrive here; this is the second
    /// line, should one ever be set for a parent domain.
    /// </summary>
    public static string? StripHubCookies(string header)
    {
        var kept = header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => !c.StartsWith("tikai", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return kept.Count == 0 ? null : string.Join("; ", kept);
    }
}
