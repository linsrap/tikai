using Microsoft.Extensions.DependencyInjection;
using Tikai.Llm.Refine;
using Tikai.Llm.Reviews;

namespace Tikai.Llm;

public static class LlmServiceCollectionExtensions
{
    /// <summary>Registers the chat client factory, the connection tester, the spec refiner and the review analyzer.</summary>
    public static IServiceCollection AddTikaiLlm(this IServiceCollection services)
    {
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        services.AddSingleton<LlmConnectionTester>();
        services.AddSingleton<SpecRefiner>();
        services.AddSingleton<ReviewAnalyzer>();
        return services;
    }
}
