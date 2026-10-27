using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Tikai.Core.Agents;

namespace Tikai.Adapters.ClaudeCode;

/// <summary>Settings of the Claude Code adapter (configuration section Tikai:Agents).</summary>
public sealed class ClaudeCodeOptions
{
    public const string Section = "Tikai:Agents";

    /// <summary>The agent image (images/agent-claude-code); it has git, so the run's prep step uses it too.</summary>
    public string ClaudeCodeImage { get; set; } = "tikai/agent-claude-code:local";
}

/// <summary>
/// Runs Claude Code headless (spike Q8): <c>-p</c> with stream-json output, only the Tikai MCP server, permission mode
/// dontAsk with an explicit tool list, and its own commit attribution off (Tikai adds the Ticket trailer).
/// </summary>
public sealed class ClaudeCodeAdapter(IOptions<ClaudeCodeOptions> options) : IAgentAdapter
{
    public const string McpConfigFile = "mcp.json";
    public const string ConfigDirectory = "claude";
    public const string PromptVersion = "run-start.v1";

    /// <summary>Everything else is denied (dontAsk). request_permission (Phase 2) will widen this per run.</summary>
    public const string AllowedTools = "Read,Write,Edit,Glob,Grep,Bash,TodoWrite,mcp__tikai__*";

    private static readonly Lazy<string> _promptTemplate = new(() => ReadPrompt(PromptVersion));

    public string Kind => Core.Runs.RunRules.ClaudeCodeAdapter;

    public string Image => options.Value.ClaudeCodeImage;

    public IReadOnlyList<RunFile> RunFiles(AgentRunContext context) =>
    [
        RunFile.ReadOnly(McpConfigFile, Encoding.UTF8.GetBytes(McpConfig(context))),
        // Claude Code's sessions live here, so a later container can --resume them (spike Q4).
        RunFile.Directory(ConfigDirectory, agentOwned: true, RunFile.Modes.Private),
    ];

    public ContainerSpec BuildContainer(AgentRunContext context)
    {
        List<ContainerEnv> environment =
        [
            new("ANTHROPIC_API_KEY", context.Llm.ApiKey, IsSecret: true),
            new("CLAUDE_CONFIG_DIR", $"{RunLayout.Root}/{ConfigDirectory}"),
        ];
        if (context.Llm.BaseUrl is { } baseUrl)
        {
            environment.Add(new("ANTHROPIC_BASE_URL", baseUrl));
        }
        return new ContainerSpec(
            Image,
            Entrypoint: ["claude"],
            Command:
            [
                "-p", Prompt(context),
                "--mcp-config", $"{RunLayout.Root}/{McpConfigFile}",
                "--strict-mcp-config",
                "--permission-mode", "dontAsk",
                "--allowedTools", AllowedTools,
                "--settings", """{"attribution":{"commit":"","pr":""}}""",
                "--output-format", "stream-json",
                "--verbose",
                "--model", context.Llm.Model,
            ],
            environment,
            WorkingDirectory: RunLayout.Work);
    }

    public IAgentOutputParser CreateOutputParser() => new StreamJsonParser();

    /// <summary>
    /// Only the Tikai server, with the run token. The per-server timeout is the hard limit of one tool call, and it also
    /// lifts Claude Code's 60 s request timer and 5 min idle timer for this server (spike Q1).
    /// </summary>
    public static string McpConfig(AgentRunContext context)
    {
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["tikai"] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = context.McpUrl,
                    ["timeout"] = (long)context.ToolCallTimeout.TotalMilliseconds,
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + context.RunToken },
                },
            },
        };
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    public static string Prompt(AgentRunContext context) =>
        _promptTemplate.Value
            .Replace("{{ticket_key}}", context.TicketKey, StringComparison.Ordinal)
            .Replace("{{ticket_title}}", context.TicketTitle, StringComparison.Ordinal)
            .Replace("{{branch}}", context.Branch, StringComparison.Ordinal);

    private static string ReadPrompt(string version)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"Tikai.Adapters.ClaudeCode.Prompts.{version}.md");
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Prompt {name} is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }
}
