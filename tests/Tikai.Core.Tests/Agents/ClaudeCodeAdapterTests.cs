using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Tikai.Adapters.ClaudeCode;
using Tikai.Core.Agents;
using Tikai.Core.Llm;

namespace Tikai.Core.Tests.Agents;

public class ClaudeCodeAdapterTests
{
    private const string Token = "tikai_run_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Key = "sk-ant-api03-secret-key";

    private static readonly ClaudeCodeAdapter _adapter = new(Options.Create(new ClaudeCodeOptions { ClaudeCodeImage = "tikai/agent-claude-code:test" }));

    private static AgentRunContext Context(string? baseUrl = null) => new(
        Guid.Parse("019a2b3c-4d5e-7f00-8a1b-2c3d4e5f6071"),
        "HUB-42",
        "Add a login page",
        "tikai/HUB-42-4e5f6071",
        new LlmConnection(LlmProviderKind.Anthropic, Key, "claude-opus-5-5", baseUrl),
        "http://hub:8080/mcp",
        Token,
        TimeSpan.FromHours(5));

    [Fact]
    public void The_command_line_is_the_headless_one_from_the_spike()
    {
        var spec = _adapter.BuildContainer(Context());

        Assert.Equal("tikai/agent-claude-code:test", spec.Image);
        Assert.Equal(["claude"], spec.Entrypoint);
        Assert.Equal("/run/work", spec.WorkingDirectory);
        var args = spec.Command;
        Assert.Equal("-p", args[0]);
        Assert.Equal("/run/mcp.json", After(args, "--mcp-config"));
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal("dontAsk", After(args, "--permission-mode"));
        Assert.Equal("Read,Write,Edit,Glob,Grep,Bash,TodoWrite,mcp__tikai__*", After(args, "--allowedTools"));
        Assert.Equal("""{"attribution":{"commit":"","pr":""}}""", After(args, "--settings"));
        Assert.Equal("stream-json", After(args, "--output-format"));
        Assert.Contains("--verbose", args);
        Assert.Equal("claude-opus-5-5", After(args, "--model"));
        Assert.DoesNotContain(args, a => a.Contains(Key, StringComparison.Ordinal) || a.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public void The_prompt_names_the_ticket_and_branch_and_asks_for_a_plan_first()
    {
        var prompt = ClaudeCodeAdapter.Prompt(Context());

        Assert.StartsWith("You are working on the Tikai ticket HUB-42: Add a login page", prompt, StringComparison.Ordinal);
        Assert.Contains("tikai/HUB-42-4e5f6071", prompt, StringComparison.Ordinal);
        Assert.True(prompt.IndexOf("get_ticket", StringComparison.Ordinal) < prompt.IndexOf("propose_plan", StringComparison.Ordinal));
        Assert.True(prompt.IndexOf("propose_plan", StringComparison.Ordinal) < prompt.IndexOf("submit_for_review", StringComparison.Ordinal));
        Assert.DoesNotContain("{{", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_key_is_secret_in_the_environment()
    {
        var env = _adapter.BuildContainer(Context()).Environment;

        Assert.Equal(new ContainerEnv("ANTHROPIC_API_KEY", Key, IsSecret: true), env.Single(e => e.Name == "ANTHROPIC_API_KEY"));
        Assert.Equal("/run/claude", env.Single(e => e.Name == "CLAUDE_CONFIG_DIR").Value);
        Assert.DoesNotContain(env, e => e.Name == "ANTHROPIC_BASE_URL");
        Assert.Equal("https://gateway.example.org", _adapter.BuildContainer(Context("https://gateway.example.org")).Environment.Single(e => e.Name == "ANTHROPIC_BASE_URL").Value);
    }

    [Fact]
    public void Printing_specs_and_contexts_shows_no_secrets()
    {
        var context = Context();
        var printed = string.Join("\n", context.ToString(), _adapter.BuildContainer(context).ToString(),
            string.Join(",", _adapter.BuildContainer(context).Environment), string.Join(",", _adapter.RunFiles(context)));

        Assert.DoesNotContain(Key, printed, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, printed, StringComparison.Ordinal);
        Assert.Contains("ANTHROPIC_API_KEY=***", printed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mcp_config_has_only_the_tikai_server_with_the_token_and_the_call_timeout()
    {
        var files = _adapter.RunFiles(Context());
        var config = files.Single(f => f.Path == "mcp.json");

        Assert.False(config.AgentOwned);
        Assert.Equal(RunFile.Modes.ReadOnly, config.Mode);
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(config.Content!));
        var servers = json.RootElement.GetProperty("mcpServers");
        Assert.Equal(["tikai"], servers.EnumerateObject().Select(p => p.Name));
        var tikai = servers.GetProperty("tikai");
        Assert.Equal("http", tikai.GetProperty("type").GetString());
        Assert.Equal("http://hub:8080/mcp", tikai.GetProperty("url").GetString());
        Assert.Equal(18_000_000, tikai.GetProperty("timeout").GetInt64());
        Assert.Equal("Bearer " + Token, tikai.GetProperty("headers").GetProperty("Authorization").GetString());

        var configDir = files.Single(f => f.Path == "claude");
        Assert.True(configDir.AgentOwned);
        Assert.Null(configDir.Content);
    }

    private static string After(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];
}
