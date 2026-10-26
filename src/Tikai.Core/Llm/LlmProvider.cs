using Tikai.Core.Common;

namespace Tikai.Core.Llm;

/// <summary>
/// An LLM account someone brought (their own API key). Owned by exactly one user or one project.
/// The key is stored encrypted and never leaves the hub except to the provider itself.
/// </summary>
public class LlmProvider : Entity
{
    public Guid? OwnerUserId { get; set; }

    public Guid? OwnerProjectId { get; set; }

    public required string Name { get; set; }

    public LlmProviderKind Kind { get; set; }

    /// <summary>Null: the provider's public endpoint.</summary>
    public string? BaseUrl { get; set; }

    public required string DefaultModel { get; set; }

    /// <summary>Encrypted with <see cref="Security.ISecretProtector"/>.</summary>
    public required string EncryptedApiKey { get; set; }

    /// <summary>The key's last characters, to tell keys apart in the UI.</summary>
    public required string KeyHint { get; set; }
}

public enum LlmProviderKind
{
    Anthropic,
}

/// <summary>Everything needed to talk to a provider. ToString never shows the key, so logging one is safe.</summary>
public sealed record LlmConnection(LlmProviderKind Kind, string ApiKey, string Model, string? BaseUrl)
{
    public override string ToString() => $"LlmConnection {{ Kind = {Kind}, Model = {Model}, BaseUrl = {BaseUrl ?? "(default)"}, ApiKey = *** }}";
}
