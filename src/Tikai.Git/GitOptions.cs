namespace Tikai.Git;

/// <summary>Settings of the git CLI wrapper (configuration section Tikai:Git).</summary>
public sealed class GitOptions
{
    public const string Section = "Tikai:Git";

    public static readonly string[] KnownProtocols = ["https", "ssh", "http", "file"];

    /// <summary>The git executable; found on PATH by default.</summary>
    public string GitPath { get; set; } = "git";

    /// <summary>
    /// Comma-separated transports remotes may use. http sends tokens in clear text and file reads paths
    /// on the hub, so both are meant for tests only.
    /// </summary>
    public string AllowedProtocols { get; set; } = "https,ssh";

    /// <summary>Where SSH keys are written for the length of one git call. The system temp dir by default (not a volume).</summary>
    public string? TempPath { get; set; }

    /// <summary>Limit for commands that only touch the local repository.</summary>
    public TimeSpan LocalTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Limit for clone and fetch.</summary>
    public TimeSpan RemoteTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public IReadOnlyList<string> AllowedProtocolList =>
        AllowedProtocols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .ToList();

    public bool IsAllowed(string protocol) => AllowedProtocolList.Contains(protocol);
}
