namespace Tikai.Core.Git;

/// <summary>A name and email for a git commit.</summary>
public sealed record GitIdentity(string Name, string Email)
{
    /// <summary>The agent's identity in run clones and on merge commits, until agent profiles exist.</summary>
    public static GitIdentity Agent { get; } = new("Claude Code (Tikai)", "claude-code@agents.tikai.invalid");

    /// <summary>Without the characters git's ident format cannot hold (angle brackets, line breaks); never empty.</summary>
    public GitIdentity Clean() => new(Part(Name, "Tikai user"), Part(Email, "unknown@tikai.invalid"));

    private static string Part(string value, string fallback)
    {
        var cleaned = new string([.. value.Where(c => c is not ('<' or '>') && !char.IsControl(c))]).Trim();
        return cleaned.Length == 0 ? fallback : cleaned;
    }
}
