using System.Globalization;
using System.Net;

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
