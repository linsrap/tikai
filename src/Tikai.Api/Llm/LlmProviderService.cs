using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Tikai.Core.Llm;
using Tikai.Core.Security;
using Tikai.Data;
using Tikai.Llm;

namespace Tikai.Api.Llm;

/// <summary>Stores providers with their key encrypted, and runs the connection test.</summary>
internal sealed class LlmProviderService(TikaiDbContext db, ISecretProtector protector, LlmConnectionTester tester)
{
    private const string Purpose = "Tikai.LlmKeys.v1";

    /// <summary>The user's own providers and every project's providers; other users' own providers are invisible.</summary>
    public IQueryable<LlmProvider> Visible(Guid userId) =>
        db.LlmProviders.Where(p => p.OwnerUserId == userId || p.OwnerProjectId != null);

    /// <summary>
    /// Providers a feature of this project may use for this user: the project's own, then the user's own.
    /// Other projects' providers are not used, even though they are visible.
    /// </summary>
    public IQueryable<LlmProvider> UsableFor(Guid userId, Guid projectId) =>
        db.LlmProviders
            .Where(p => p.OwnerProjectId == projectId || p.OwnerUserId == userId)
            .OrderBy(p => p.OwnerProjectId == null)
            .ThenBy(p => p.Name);

    public static Guid UserId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("No user id in the session."));

    /// <summary>Throws <see cref="DbUpdateException"/> for a duplicate name.</summary>
    public async Task<LlmProvider> CreateAsync(CreateLlmProviderRequest request, Guid? userId, Guid? projectId, CancellationToken cancellationToken)
    {
        var apiKey = request.ApiKey.Trim();
        var provider = new LlmProvider
        {
            OwnerUserId = userId,
            OwnerProjectId = projectId,
            Name = request.Name.Trim(),
            Kind = request.Kind,
            BaseUrl = request.BaseUrl,
            DefaultModel = request.DefaultModel,
            EncryptedApiKey = protector.Protect(Purpose, apiKey),
            KeyHint = LlmProviderRules.KeyHint(apiKey),
        };
        db.LlmProviders.Add(provider);
        await db.SaveChangesAsync(cancellationToken);
        return provider;
    }

    /// <summary>Throws <see cref="DbUpdateException"/> for a duplicate name.</summary>
    public async Task UpdateAsync(LlmProvider provider, UpdateLlmProviderRequest request, CancellationToken cancellationToken)
    {
        provider.Name = request.Name.Trim();
        provider.DefaultModel = request.DefaultModel;
        provider.BaseUrl = request.BaseUrl;
        if (request.ApiKey is { } apiKey)
        {
            provider.EncryptedApiKey = protector.Protect(Purpose, apiKey.Trim());
            provider.KeyHint = LlmProviderRules.KeyHint(apiKey);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<LlmTestResult> TestAsync(LlmProvider provider, CancellationToken cancellationToken)
    {
        LlmConnection connection;
        try
        {
            connection = Connection(provider);
        }
        catch (CryptographicException)
        {
            return new LlmTestResult(false, "The stored key cannot be decrypted (the hub's keys changed). Enter the key again.", null, TimeSpan.Zero);
        }
        return await tester.TestAsync(connection, cancellationToken);
    }

    /// <summary>The decrypted connection, for the features that call the provider (refine spec, agent runs).</summary>
    public LlmConnection Connection(LlmProvider provider) =>
        new(provider.Kind, protector.Unprotect(Purpose, provider.EncryptedApiKey), provider.DefaultModel, provider.BaseUrl);
}
