using Tikai.Core.Llm;

namespace Tikai.Api.Llm;

/// <summary>A provider as the API shows it. The key is write-only: only its last characters (<see cref="KeyHint"/>) are returned.</summary>
public sealed record LlmProviderInfo(
    Guid Id,
    string Name,
    LlmProviderKind Kind,
    string? BaseUrl,
    string DefaultModel,
    string KeyHint,
    Guid? OwnerProjectId,
    DateTimeOffset CreatedAt)
{
    internal static LlmProviderInfo From(LlmProvider p) =>
        new(p.Id, p.Name, p.Kind, p.BaseUrl, p.DefaultModel, p.KeyHint, p.OwnerProjectId, p.CreatedAt);
}

/// <param name="BaseUrl">Null: the provider's public endpoint.</param>
public sealed record CreateLlmProviderRequest(
    string Name,
    string DefaultModel,
    string ApiKey,
    LlmProviderKind Kind = LlmProviderKind.Anthropic,
    string? BaseUrl = null);

/// <param name="ApiKey">Null keeps the stored key.</param>
public sealed record UpdateLlmProviderRequest(string Name, string DefaultModel, string? BaseUrl = null, string? ApiKey = null);

public sealed record LlmTestResponse(bool Ok, string Message, string? Reply, int DurationMs);
