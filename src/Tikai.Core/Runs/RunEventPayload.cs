using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tikai.Core.Runs;

/// <summary>
/// Keeps run event payloads small: long strings are cut; an object that is still too big keeps its keys and has its
/// strings cut harder (marked <c>truncated</c>), so the browser can still show it as a tool call; only one that does
/// not fit even then becomes a flat preview.
/// </summary>
public static class RunEventPayload
{
    public const int MaxStringLength = 4_000;
    public const int MaxPayloadBytes = 16 * 1024;
    private const int PreviewLength = 2_000;
    private static readonly int[] _harderLengths = [2_000, 1_000, 500, 200];

    /// <summary>Cuts every string in the object to <see cref="MaxStringLength"/>, then the whole to <see cref="MaxPayloadBytes"/>.</summary>
    public static JsonObject Trim(JsonObject payload)
    {
        var trimmed = (JsonObject)TrimNode(payload, MaxStringLength)!;
        if (Fits(trimmed))
        {
            return trimmed;
        }
        foreach (var length in _harderLengths)
        {
            var harder = (JsonObject)TrimNode(payload, length)!;
            harder["truncated"] = true;
            if (Fits(harder))
            {
                return harder;
            }
        }
        return new JsonObject
        {
            ["truncated"] = true,
            ["preview"] = Cut(trimmed.ToJsonString(), PreviewLength),
        };
    }

    private static bool Fits(JsonObject payload) => Encoding.UTF8.GetByteCount(payload.ToJsonString()) <= MaxPayloadBytes;

    /// <summary>The text itself, or its start with a note how much was left out.</summary>
    public static string Cut(string text, int maxLength = MaxStringLength) =>
        text.Length <= maxLength
            ? text
            : string.Create(CultureInfo.InvariantCulture, $"{text[..maxLength]}… [{text.Length - maxLength} more characters]");

    private static JsonNode? TrimNode(JsonNode? node, int maxLength) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, TrimNode(p.Value, maxLength)))),
        JsonArray array => new JsonArray([.. array.Select(n => TrimNode(n, maxLength))]),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(Cut(text, maxLength)),
        _ => node?.DeepClone(),
    };
}

/// <summary>
/// Removes secrets from agent output before it is stored: the run's own API key and token exactly, plus anything that
/// looks like an Anthropic key or a run token. Secrets that are part of the repository itself are not detected.
/// </summary>
public sealed partial class SecretRedactor
{
    public const string Replacement = "[redacted]";

    private readonly string[] _secrets;

    public SecretRedactor(IEnumerable<string?> secrets)
    {
        // Longest first, so a secret that contains another one is replaced as a whole.
        _secrets = [.. secrets.OfType<string>().Where(s => s.Length >= 8).Distinct().OrderByDescending(s => s.Length)];
    }

    public string Redact(string text)
    {
        foreach (var secret in _secrets)
        {
            text = text.Replace(secret, Replacement, StringComparison.Ordinal);
        }
        return LooksLikeSecret().Replace(text, Replacement);
    }

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{8,}|tikai_run_[A-Za-z0-9_\-]{16,}")]
    private static partial Regex LooksLikeSecret();
}
