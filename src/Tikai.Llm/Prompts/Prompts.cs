using System.Collections.Concurrent;

namespace Tikai.Llm;

/// <summary>
/// Prompts are versioned markdown files in Prompts/, embedded in the assembly. A changed prompt is a new file
/// (refine-spec.v2.md), so every answer can be traced to the exact text that produced it.
/// </summary>
internal static class Prompts
{
    private static readonly ConcurrentDictionary<string, string> _cache = new();

    public static string Load(string name) => _cache.GetOrAdd(name, static name =>
    {
        using var stream = typeof(Prompts).Assembly.GetManifestResourceStream($"Tikai.Llm.Prompts.{name}.md")
            ?? throw new InvalidOperationException($"Prompt {name} is not embedded in Tikai.Llm.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
}
