using Microsoft.Extensions.DependencyInjection;

namespace Tikai.Llm;

public static class LlmServiceCollectionExtensions
{
    /// <summary>Registers the chat client factory and the connection tester.</summary>
    public static IServiceCollection AddTikaiLlm(this IServiceCollection services)
    {
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        services.AddSingleton<LlmConnectionTester>();
        return services;
    }
}
