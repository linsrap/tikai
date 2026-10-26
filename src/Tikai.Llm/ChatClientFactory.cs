using Anthropic;
using Microsoft.Extensions.AI;
using Tikai.Core.Llm;

namespace Tikai.Llm;

/// <summary>Builds a <see cref="IChatClient"/> for one provider connection. Dispose it after use.</summary>
public interface IChatClientFactory
{
    IChatClient Create(LlmConnection connection);
}

/// <summary>Anthropic through the official SDK (package Anthropic) and its Microsoft.Extensions.AI integration.</summary>
public sealed class ChatClientFactory : IChatClientFactory
{
    public const string AnthropicDefaultBaseUrl = "https://api.anthropic.com";

    public IChatClient Create(LlmConnection connection) => connection.Kind switch
    {
        LlmProviderKind.Anthropic => CreateAnthropic(connection),
        _ => throw new NotSupportedException($"LLM provider kind {connection.Kind} is not supported."),
    };

    private static OwningChatClient CreateAnthropic(LlmConnection connection)
    {
        // Every option is set explicitly: the SDK would otherwise pick up ANTHROPIC_* variables of the hub's environment.
        var client = new AnthropicClient
        {
            ApiKey = connection.ApiKey,
            AuthToken = null,
            BaseUrl = connection.BaseUrl ?? AnthropicDefaultBaseUrl,
            Timeout = TimeSpan.FromMinutes(10),
            MaxRetries = 2,
        };
        return new OwningChatClient(client.AsIChatClient(connection.Model), client);
    }

    /// <summary>Disposes the SDK client together with the chat client built on it.</summary>
    private sealed class OwningChatClient(IChatClient inner, IDisposable owned) : DelegatingChatClient(inner)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                owned.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
