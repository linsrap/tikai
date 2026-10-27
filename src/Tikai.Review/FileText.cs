using System.Text;

namespace Tikai.Review;

/// <summary>Whether a file side can be shown as text in the diff.</summary>
public static class FileText
{
    private static readonly UTF8Encoding _strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The text when the content is valid UTF-8 without NUL bytes (a leading BOM is dropped); null for binary.</summary>
    public static string? Decode(ReadOnlySpan<byte> content)
    {
        if (content.IndexOf((byte)0) >= 0)
        {
            return null;
        }
        if (content.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            content = content[3..];
        }
        try
        {
            return _strict.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
