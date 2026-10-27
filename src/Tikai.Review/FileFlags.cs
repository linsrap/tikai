using System.Text.RegularExpressions;

namespace Tikai.Review;

/// <summary>What a reviewer can skim or skip: lock files, generated files, files too large to show.</summary>
public sealed record FileFlags(bool Lock, bool Generated, bool Large);

/// <summary>
/// Flags a changed file by its path (and its size and line counts). Generated and lock files are collapsed in the review
/// screen, not hidden: they still count. The repository can name more generated files with <c>linguist-generated</c>
/// in its .gitattributes.
/// </summary>
public sealed class FileClassifier
{
    /// <summary>Above this a file side is not shown in the diff.</summary>
    public const long MaxShownBytes = 1024 * 1024;

    public const int MaxShownLines = 5_000;

    private static readonly HashSet<string> _lockFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lockb", "packages.lock.json",
        "Cargo.lock", "poetry.lock", "uv.lock", "Pipfile.lock", "Gemfile.lock", "composer.lock", "go.sum", "flake.lock",
    };

    private static readonly PathPattern[] _generated =
    [
        .. new[]
        {
            "*.Designer.cs", "*.g.cs", "*.g.i.cs", "*ModelSnapshot.cs", "*.min.js", "*.min.css", "*.map",
            "dist/**", "build/**", "obj/**", "bin/**", "**/dist/**", "**/obj/**", "**/bin/**", "node_modules/**", "**/node_modules/**",
        }.Select(PathPattern.Parse),
    ];

    private readonly IReadOnlyList<(PathPattern Pattern, bool Generated)> _attributes;

    /// <param name="gitAttributes">The head's .gitattributes, if it has one.</param>
    public FileClassifier(string? gitAttributes = null) => _attributes = GeneratedAttributes(gitAttributes);

    public FileFlags Classify(string path, long? size, int? additions, int? deletions)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var isLock = _lockFiles.Contains(name);
        // The last matching line wins, as in git; "-linguist-generated" turns a default off.
        bool? fromAttributes = null;
        foreach (var (pattern, generated) in _attributes)
        {
            if (pattern.Matches(path))
            {
                fromAttributes = generated;
            }
        }
        var generatedFile = fromAttributes ?? _generated.Any(p => p.Matches(path));
        var large = size > MaxShownBytes || additions + deletions > MaxShownLines;
        return new FileFlags(isLock, generatedFile, large);
    }

    /// <summary>Lines like "<c>src/gen/** linguist-generated</c>", "<c>=true</c>", "<c>-linguist-generated</c>" or "<c>=false</c>".</summary>
    private static List<(PathPattern, bool)> GeneratedAttributes(string? text)
    {
        var list = new List<(PathPattern, bool)>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var parts = line.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].StartsWith('#') || parts[0].StartsWith('"'))
            {
                continue;
            }
            foreach (var attribute in parts.Skip(1))
            {
                bool? value = attribute switch
                {
                    "linguist-generated" or "linguist-generated=true" => true,
                    "-linguist-generated" or "!linguist-generated" or "linguist-generated=false" => false,
                    _ => null,
                };
                if (value is { } generated)
                {
                    list.Add((PathPattern.Parse(parts[0]), generated));
                }
            }
        }
        return list;
    }
}

/// <summary>
/// A gitattributes-style path pattern: without a slash it matches the file name in any directory; with one it matches
/// from the root. <c>*</c> stays within a directory, <c>**</c> crosses directories.
/// </summary>
internal sealed class PathPattern
{
    private readonly Regex _regex;
    private readonly bool _nameOnly;

    private PathPattern(Regex regex, bool nameOnly)
    {
        _regex = regex;
        _nameOnly = nameOnly;
    }

    public static PathPattern Parse(string pattern)
    {
        var trimmed = pattern.TrimStart('/');
        var nameOnly = !pattern.Contains('/', StringComparison.Ordinal);
        var regex = "^" + Translate(trimmed) + "$";
        return new PathPattern(new Regex(regex, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), nameOnly);
    }

    public bool Matches(string path) => _regex.IsMatch(_nameOnly ? path[(path.LastIndexOf('/') + 1)..] : path);

    private static string Translate(string pattern)
    {
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                // "**/" matches zero or more directories, a trailing "**" everything below.
                var slash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                result.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else
            {
                result.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString()),
                });
            }
        }
        return result.ToString();
    }
}
