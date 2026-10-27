namespace Tikai.Review;

/// <summary>The Monaco language id for a file, guessed from its name; "plaintext" when unknown.</summary>
public static class Languages
{
    private static readonly Dictionary<string, string> _byExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".csx"] = "csharp",
        [".fs"] = "fsharp",
        [".vb"] = "vb",
        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".mts"] = "typescript",
        [".cts"] = "typescript",
        [".js"] = "javascript",
        [".jsx"] = "javascript",
        [".mjs"] = "javascript",
        [".cjs"] = "javascript",
        [".json"] = "json",
        [".jsonc"] = "json",
        [".html"] = "html",
        [".htm"] = "html",
        [".css"] = "css",
        [".scss"] = "scss",
        [".less"] = "less",
        [".md"] = "markdown",
        [".markdown"] = "markdown",
        [".xml"] = "xml",
        [".csproj"] = "xml",
        [".props"] = "xml",
        [".targets"] = "xml",
        [".slnx"] = "xml",
        [".svg"] = "xml",
        [".resx"] = "xml",
        [".yml"] = "yaml",
        [".yaml"] = "yaml",
        [".sql"] = "sql",
        [".py"] = "python",
        [".go"] = "go",
        [".rs"] = "rust",
        [".java"] = "java",
        [".kt"] = "kotlin",
        [".kts"] = "kotlin",
        [".rb"] = "ruby",
        [".php"] = "php",
        [".sh"] = "shell",
        [".bash"] = "shell",
        [".ps1"] = "powershell",
        [".psm1"] = "powershell",
        [".c"] = "c",
        [".h"] = "c",
        [".cpp"] = "cpp",
        [".hpp"] = "cpp",
        [".cc"] = "cpp",
        [".swift"] = "swift",
        [".lua"] = "lua",
        [".r"] = "r",
        [".ini"] = "ini",
        [".toml"] = "ini",
        [".bat"] = "bat",
        [".cmd"] = "bat",
        [".graphql"] = "graphql",
        [".proto"] = "proto",
        [".dart"] = "dart",
        [".scala"] = "scala",
    };

    private static readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = "dockerfile",
        ["Makefile"] = "plaintext",
        [".editorconfig"] = "ini",
        [".gitattributes"] = "plaintext",
    };

    public static string For(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (_byName.TryGetValue(name, out var byName))
        {
            return byName;
        }
        if (name.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dockerfile", StringComparison.OrdinalIgnoreCase))
        {
            return "dockerfile";
        }
        var dot = name.LastIndexOf('.');
        return dot > 0 && _byExtension.TryGetValue(name[dot..], out var language) ? language : "plaintext";
    }
}
