using System.Text.RegularExpressions;

namespace Tikai.Core.Projects;

/// <summary>Project URL names: lowercase letters and digits in groups joined by single hyphens, e.g. "tikai-web".</summary>
public static partial class ProjectSlug
{
    public const int MinLength = 2;
    public const int MaxLength = 40;

    /// <returns>Null when valid, otherwise a message for people.</returns>
    public static string? ValidationError(string? slug) =>
        slug is null || slug.Length is < MinLength or > MaxLength || !Pattern().IsMatch(slug)
            ? $"Use {MinLength} to {MaxLength} lowercase letters, digits and single hyphens, e.g. \"tikai-web\"."
            : null;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Pattern();
}

/// <summary>Ticket key prefixes: an uppercase letter, then uppercase letters or digits, e.g. "HUB" in HUB-42.</summary>
public static partial class ProjectKey
{
    public const int MinLength = 2;
    public const int MaxLength = 10;

    /// <returns>Null when valid, otherwise a message for people.</returns>
    public static string? ValidationError(string? key) =>
        key is null || key.Length is < MinLength or > MaxLength || !Pattern().IsMatch(key)
            ? $"Use {MinLength} to {MaxLength} uppercase letters and digits, starting with a letter, e.g. \"HUB\"."
            : null;

    [GeneratedRegex("^[A-Z][A-Z0-9]+$")]
    private static partial Regex Pattern();
}
