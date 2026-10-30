namespace Tikai.Api.Llm;

/// <summary>Settings for LLM providers (section Tikai:Llm).</summary>
public sealed class LlmOptions
{
    public const string Section = "Tikai:Llm";

    /// <summary>
    /// Lets a provider's base URL use plain http, for a gateway on the same host or network (and the browser test's fake
    /// API). Off by default: the API key would cross the network in clear text.
    /// </summary>
    public bool AllowHttpBaseUrls { get; set; }
}
