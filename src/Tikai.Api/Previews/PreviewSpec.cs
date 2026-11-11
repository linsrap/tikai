using System.Reflection;
using System.Text;

namespace Tikai.Api.Previews;

/// <summary>
/// The preview file format for agents (get_preview_spec): docs/preview-file.md itself, embedded at build time, so
/// agents and people read the same text.
/// </summary>
internal static class PreviewSpec
{
    private const string Resource = "Tikai.Api.Previews.preview-file.md";

    private static readonly Lazy<string> _text = new(Read);

    public static string Text => _text.Value;

    private static string Read()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"{Resource} is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }
}
