using Microsoft.Extensions.DependencyInjection;
using Tikai.Llm.Refine;

namespace Tikai.Llm;

public static class LlmServiceCollectionExtensions
{
    /// <summary>Registers the chat client factory, the connection tester and the spec refiner.</summary>
    public static IServiceCollection AddTikaiLlm(this IServiceCollection services)
    {
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        services.AddSingleton<LlmConnectionTester>();
        services.AddSingleton<SpecRefiner>();
        return services;
    }
}
