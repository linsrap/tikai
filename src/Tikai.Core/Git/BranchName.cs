using System.Buffers;

namespace Tikai.Core.Git;

/// <summary>Branch names people may enter: a strict subset of what git check-ref-format allows.</summary>
public static class BranchName
{
    public const int MaxLength = 200;

    private static readonly SearchValues<char> _forbidden = SearchValues.Create("~^:?*[\\ ");

    /// <returns>Null when valid, otherwise a message for people.</returns>
    public static string? ValidationError(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Enter a branch name.";
        }

        var valid = name.Length <= MaxLength
            && !name.StartsWith('-')
            && name != "@"
            && !name.AsSpan().ContainsAny(_forbidden)
            && !name.Any(char.IsControl)
            && !name.Contains("..", StringComparison.Ordinal)
            && !name.Contains("@{", StringComparison.Ordinal)
            && !name.EndsWith(".lock", StringComparison.Ordinal)
            && !name.EndsWith('.')
            && name.Split('/').All(part => part.Length > 0 && !part.StartsWith('.'));
        return valid ? null : $"\"{name}\" is not a valid branch name.";
    }
}
