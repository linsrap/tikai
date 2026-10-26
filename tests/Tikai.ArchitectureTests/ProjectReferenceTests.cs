using System.Xml.Linq;

namespace Tikai.ArchitectureTests;

// Enforces the allowed project references from the roadmap's solution layout.
// Checks the .csproj files rather than assembly metadata, because the compiler drops
// references that no code uses yet.
public class ProjectReferenceTests
{
    private static readonly Dictionary<string, string[]> _allowed = new()
    {
        ["Tikai.Core"] = [],
        ["Tikai.Data"] = ["Tikai.Core"],
        ["Tikai.Git"] = ["Tikai.Core"],
        ["Tikai.Llm"] = ["Tikai.Core"],
        ["Tikai.Review"] = ["Tikai.Core"],
        ["Tikai.Orchestrator"] = ["Tikai.Core"],
        ["Tikai.Mcp"] = ["Tikai.Core"],
        ["Tikai.Adapters.ClaudeCode"] = ["Tikai.Core"],
        // Composition root: may reference everything.
        ["Tikai.Api"] = ["*"],
    };

    public static TheoryData<string> SourceProjects()
    {
        var data = new TheoryData<string>();
        foreach (var name in Projects().Keys.Order())
        {
            data.Add(name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Project_has_a_reference_rule(string project)
    {
        Assert.True(_allowed.ContainsKey(project),
            $"{project} has no rule in {nameof(ProjectReferenceTests)}. Decide which projects it may reference and add it.");
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Project_references_only_allowed_projects(string project)
    {
        var allowed = _allowed.GetValueOrDefault(project, []);
        if (allowed is ["*"])
        {
            return;
        }

        var forbidden = ProjectReferences(Projects()[project]).Except(allowed).ToList();

        Assert.True(forbidden.Count == 0,
            $"{project} must not reference {string.Join(", ", forbidden)}. Allowed: {(allowed.Length == 0 ? "nothing" : string.Join(", ", allowed))}.");
    }

    [Fact]
    public void Core_has_no_package_references()
    {
        var packages = Load(Projects()["Tikai.Core"])
            .Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include"))
            .ToList();

        Assert.True(packages.Count == 0, $"Tikai.Core must stay free of packages, found: {string.Join(", ", packages)}.");
    }

    [Fact]
    public void Source_projects_are_found()
    {
        Assert.Contains("Tikai.Core", Projects().Keys);
        Assert.Contains("Tikai.Api", Projects().Keys);
    }

    private static Dictionary<string, string> Projects() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path), path => path);

    private static IEnumerable<string> ProjectReferences(string csproj) =>
        Load(csproj)
            .Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? "")
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')));

    private static XDocument Load(string csproj) => XDocument.Load(csproj);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tikai.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Tikai.slnx not found above " + AppContext.BaseDirectory);
    }
}
