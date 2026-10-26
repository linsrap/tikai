using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Tikai.Core.Git;

public enum GitTransport
{
    Https,
    Http,
    Ssh,
    File,
}

/// <summary>
/// A remote URL the hub may fetch from. Only plain transports are accepted: no local paths,
/// no "transport::address" helpers (ext:: runs commands), no secrets in the URL, nothing git could read as an option.
/// Which transports are allowed is a setting on top of this (git enforces it via GIT_ALLOW_PROTOCOL).
/// </summary>
public sealed partial record GitRemoteUrl
{
    public const int MaxLength = 2000;

    private GitRemoteUrl(string value, GitTransport transport, string host)
    {
        Value = value;
        Transport = transport;
        Host = host;
    }

    public string Value { get; }

    public GitTransport Transport { get; }

    /// <summary>Host name; empty for file URLs.</summary>
    public string Host { get; }

    /// <summary>The git protocol name, as used by GIT_ALLOW_PROTOCOL.</summary>
    public string Protocol => Transport.ToString().ToLowerInvariant();

    /// <summary>"https://host[:port]/" for HTTP(S) URLs: the scope for the token header, so it is sent to this server only.</summary>
    public string? HttpOrigin =>
        Transport is GitTransport.Https or GitTransport.Http ? new Uri(Value).GetLeftPart(UriPartial.Authority) + "/" : null;

    public override string ToString() => Value;

    public static GitRemoteUrl Parse(string input) =>
        TryParse(input, out var url, out var error) ? url : throw new ArgumentException(error, nameof(input));

    public static bool TryParse(string? input, [NotNullWhen(true)] out GitRemoteUrl? url, [NotNullWhen(false)] out string? error)
    {
        url = null;
        var value = input?.Trim() ?? "";
        error = Check(value, out var transport, out var host);
        if (error is null)
        {
            url = new GitRemoteUrl(value, transport, host);
        }
        return error is null;
    }

    private static string? Check(string value, out GitTransport transport, out string host)
    {
        transport = default;
        host = "";
        if (value.Length == 0)
        {
            return "Enter a remote URL.";
        }
        if (value.Length > MaxLength)
        {
            return $"The URL is longer than {MaxLength} characters.";
        }
        if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return "The URL must not contain spaces or control characters.";
        }
        if (value.StartsWith('-'))
        {
            return "The URL must not start with \"-\".";
        }
        if (RemoteHelper().IsMatch(value))
        {
            return "Remote helpers (\"transport::address\") are not supported. Use an https:// or ssh:// URL.";
        }

        return value.Contains("://", StringComparison.Ordinal)
            ? CheckUrl(value, out transport, out host)
            : CheckScpStyle(value, out transport, out host);
    }

    private static string? CheckUrl(string value, out GitTransport transport, out string host)
    {
        transport = default;
        host = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return "This is not a valid URL.";
        }

        switch (uri.Scheme)
        {
            case "https":
                transport = GitTransport.Https;
                break;
            case "http":
                transport = GitTransport.Http;
                break;
            case "ssh":
                transport = GitTransport.Ssh;
                break;
            case "file":
                transport = GitTransport.File;
                return uri.IsLoopback || uri.Host.Length == 0 ? null : "file:// URLs must point to this machine.";
            default:
                return $"The scheme \"{uri.Scheme}\" is not supported. Use https:// or ssh://.";
        }

        host = uri.Host;
        if (host.Length == 0 || host.StartsWith('-'))
        {
            return "The URL needs a valid host name.";
        }
        if (transport is GitTransport.Https or GitTransport.Http && uri.UserInfo.Length > 0)
        {
            return "Do not put a user name or token in the URL. Enter the token as the repository's credential.";
        }
        if (uri.UserInfo.Contains(':', StringComparison.Ordinal))
        {
            return "Do not put a password in the URL.";
        }
        if (uri.AbsolutePath is "" or "/")
        {
            return "The URL needs a repository path.";
        }
        return null;
    }

    // git's "[user@]host:path" form; git uses it only when there is no slash before the first colon.
    private static string? CheckScpStyle(string value, out GitTransport transport, out string host)
    {
        transport = GitTransport.Ssh;
        host = "";
        var match = ScpStyle().Match(value);
        if (!match.Success)
        {
            return "Use an https:// or ssh:// URL, or the form user@host:path.";
        }

        host = match.Groups["host"].Value;
        if (host.Length == 1)
        {
            // "C:/repos/x" is a Windows path, not host "C".
            return "Local paths are not allowed. Use an https:// or ssh:// URL.";
        }
        return match.Groups["path"].Value.StartsWith('-') ? "The repository path must not start with \"-\"." : null;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*::")]
    private static partial Regex RemoteHelper();

    [GeneratedRegex(@"^(?:(?<user>[A-Za-z0-9._-]+)@)?(?<host>[A-Za-z0-9][A-Za-z0-9.-]*):(?<path>[^/\\].*|/.+)$")]
    private static partial Regex ScpStyle();
}
