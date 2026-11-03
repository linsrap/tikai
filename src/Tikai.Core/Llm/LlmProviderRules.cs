using System.Text.RegularExpressions;

namespace Tikai.Core.Llm;

/// <summary>Checks for what people enter when they add a provider. Each returns null when valid, otherwise a message for people.</summary>
public static partial class LlmProviderRules
{
    public const int MaxNameLength = 100;
    public const int MaxModelLength = 100;
    public const int MaxBaseUrlLength = 500;
    public const int MaxApiKeyLength = 500;
    public const int KeyHintLength = 4;

    public static string? NameError(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength || name.Any(char.IsControl)
            ? $"Enter a name of at most {MaxNameLength} characters."
            : null;

    /// <summary>Model ids such as "claude-opus-5-5": lowercase letters, digits, and . _ : - (no spaces).</summary>
    public static string? ModelError(string? model) =>
        model is null || model.Length > MaxModelLength || !ModelPattern().IsMatch(model)
            ? "Enter a model id such as \"claude-opus-5-5\"."
            : null;

    public static string? ApiKeyError(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Trim().Length is < 8 or > MaxApiKeyLength || apiKey.Trim().Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return "Paste the API key (no spaces or line breaks).";
        }
        // "claude setup-token" makes OAuth tokens for Claude Code on a Pro/Max plan; the Messages API does not take them as keys.
        return apiKey.Trim().StartsWith("sk-ant-oat", StringComparison.Ordinal)
            ? "This is a Claude subscription token (from \"claude setup-token\"), not an API key. Create an API key at platform.claude.com (API usage is billed separately from Pro/Max)."
            : null;
    }

    /// <summary>Optional; only https, without user info, query or fragment (the key is sent there).</summary>
    /// <param name="allowHttp">The hub allows plain http (setting Tikai:Llm:AllowHttpBaseUrls), e.g. for a gateway next to it.</param>
    public static string? BaseUrlError(string? baseUrl, bool allowHttp = false)
    {
        if (baseUrl is null)
        {
            return null;
        }
        return baseUrl.Length <= MaxBaseUrlLength
            && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (allowHttp && uri.Scheme == Uri.UriSchemeHttp))
            && uri.UserInfo.Length == 0
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0
                ? null
                : $"Enter an {(allowHttp ? "http(s)" : "https")}:// URL without user name, query or fragment, or leave it empty for the provider's default.";
    }

    /// <summary>The last characters of the key, shown as "…abcd".</summary>
    public static string KeyHint(string apiKey)
    {
        var trimmed = apiKey.Trim();
        return trimmed.Length <= KeyHintLength * 2 ? "" : trimmed[^KeyHintLength..];
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._:-]*$")]
    private static partial Regex ModelPattern();
}
