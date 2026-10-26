using Microsoft.AspNetCore.Authentication;
using Tikai.Api.Attention;
using Tikai.Api.Runs;
using Tikai.Core.Attention;
using Tikai.Mcp;

namespace Tikai.Api.Mcp;

/// <summary>
/// The MCP endpoint agents use: stateless Streamable HTTP (the SDK's default, protocol 2026-07-28) at <c>/mcp</c>,
/// authenticated only by run tokens. It is outside <c>/api</c>: no cookies, no antiforgery.
/// </summary>
internal static class McpSetup
{
    public const string Path = "/mcp";

    public static IServiceCollection AddTikaiMcp(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<McpOptions>()
            .Bind(configuration.GetSection(McpOptions.Section))
            .Validate(o => o.HeartbeatInterval > TimeSpan.Zero, "Tikai:Mcp:HeartbeatInterval must be positive.")
            .ValidateOnStart();

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, RunTokenAuthenticationHandler>(RunTokenAuthenticationHandler.SchemeName, null);
        // Only the run token counts here: a login cookie sent along is ignored, so a browser cannot act as an agent.
        services.AddAuthorizationBuilder().AddPolicy(RunTokenAuthenticationHandler.PolicyName, policy => policy
            .AddAuthenticationSchemes(RunTokenAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireClaim(RunClaims.RunId));

        services.AddScoped<RunService>();
        services.AddScoped<AttentionService>();
        services.AddScoped<IRunTools, RunTools>();
        services.AddSingleton<AttentionWaiter>();
        services.AddSingleton<IAttentionWaiter>(sp => sp.GetRequiredService<AttentionWaiter>());
        services.AddHostedService(sp => sp.GetRequiredService<AttentionWaiter>());

        services.AddMcpServer(options => options.ServerInfo = new() { Name = "tikai", Version = "1" })
            .WithHttpTransport()
            .WithTikaiTools();
        return services;
    }

    public static void MapTikaiMcp(this IEndpointRouteBuilder app) =>
        app.MapMcp(Path)
            .RequireAuthorization(RunTokenAuthenticationHandler.PolicyName)
            .ExcludeFromDescription();
}
