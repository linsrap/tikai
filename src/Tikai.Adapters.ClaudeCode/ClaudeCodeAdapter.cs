using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Tikai.Core.Agents;
using Tikai.Core.Attention;

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
public sealed partial class ClaudeCodeAdapter(IOptions<ClaudeCodeOptions> options) : IAgentAdapter
{
    public const string McpConfigFile = "mcp.json";
    public const string ConfigDirectory = "claude";
    public const string PromptVersion = "run-start.v1";
    public const string ResumePromptVersion = "run-resume.v1";
    public const string SummaryPromptVersion = "run-summary.v1";

    /// <summary>Everything else is denied (dontAsk). request_permission (Phase 2) will widen this per run.</summary>
    public const string AllowedTools = "Read,Write,Edit,Glob,Grep,Bash,TodoWrite,mcp__tikai__*";

    private static readonly Lazy<string> _promptTemplate = new(() => ReadPrompt(PromptVersion));
    private static readonly Lazy<Dictionary<string, string>> _resumeTemplates = new(() => Sections(ReadPrompt(ResumePromptVersion)));
    private static readonly Lazy<Dictionary<string, string>> _summaryTemplates = new(() => Sections(ReadPrompt(SummaryPromptVersion)));

    public string Kind => Core.Runs.RunRules.ClaudeCodeAdapter;

    public string Image => options.Value.ClaudeCodeImage;

    public IReadOnlyList<RunFile> RunFiles(AgentRunContext context) =>
    [
        RunFile.ReadOnly(McpConfigFile, Encoding.UTF8.GetBytes(McpConfig(context))),
        // Claude Code's sessions live here, so a later container can --resume them (spike Q4).
        RunFile.Directory(ConfigDirectory, agentOwned: true, RunFile.Modes.Private),
    ];

    public ContainerSpec BuildContainer(AgentRunContext context) => Container(context, Prompt(context), resume: null);

    public ContainerSpec BuildResumeContainer(AgentRunContext context, RunContinuation continuation) => continuation switch
    {
        ResumeSession session => Container(context, ResumePrompt(session.Answers), session.SessionId),
        FreshSession fresh => Container(context, SummaryPrompt(context, fresh), resume: null),
        _ => throw new ArgumentOutOfRangeException(nameof(continuation), continuation, "Unknown continuation."),
    };

    /// <summary>Claude Code names a project's session directory after its working directory, with "/" replaced by "-".</summary>
    public string? SessionFile(string sessionId) =>
        $"{ConfigDirectory}/projects/{RunLayout.Work.Replace('/', '-')}/{sessionId}.jsonl";

    private ContainerSpec Container(AgentRunContext context, string prompt, string? resume)
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
        List<string> command = ["-p", prompt];
        if (resume is not null)
        {
            command.AddRange(["--resume", resume]);
        }
        command.AddRange(
        [
            "--mcp-config", $"{RunLayout.Root}/{McpConfigFile}",
            "--strict-mcp-config",
            "--permission-mode", "dontAsk",
            "--allowedTools", AllowedTools,
            "--settings", """{"attribution":{"commit":"","pr":""}}""",
            "--output-format", "stream-json",
            "--verbose",
            "--model", context.Llm.Model,
        ]);
        return new ContainerSpec(Image, Entrypoint: ["claude"], Command: command, environment, WorkingDirectory: RunLayout.Work);
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

    /// <summary>The message for a resumed session: each answer the agent waited for, then "continue" (spike Q4).</summary>
    public static string ResumePrompt(IReadOnlyList<AnsweredRequest> answers)
    {
        var t = _resumeTemplates.Value;
        var lines = answers.Select(a => a switch
        {
            { Question: { } q } => Fill(t["question"], ("question", q.Question), ("answer", a.Answer.Text ?? "")),
            { Answer.Decision: PlanDecision.Revise } => Fill(t["revise"], ("notes", a.Answer.Text ?? "")),
            { Answer.Text: { Length: > 0 } notes } => Fill(t["approve_notes"], ("notes", notes)),
            _ => t["approve"],
        });
        return Fill(t["message"], ("answers", string.Join("\n\n", lines)));
    }

    /// <summary>The prompt for a fresh session that takes over a run whose session could not be resumed (spike Q5).</summary>
    public static string SummaryPrompt(AgentRunContext context, FreshSession summary)
    {
        var t = _summaryTemplates.Value;
        var plan = summary.ApprovedPlan is { } approved ? Steps(approved) : t["no_plan"];
        var answers = summary.Answers.Count == 0 ? t["no_answers"] : string.Join("\n", summary.Answers.Select(a => a switch
        {
            { Question: { } q } => Fill(t["question"], ("question", q.Question), ("answer", a.Answer.Text ?? "")),
            _ => Fill(t["plan"],
                ("steps", a.Plan is { } p ? Steps(p, "    ") : ""),
                ("decision", a.Answer.Decision == PlanDecision.Revise ? "revise" : "approve"),
                ("notes", string.IsNullOrWhiteSpace(a.Answer.Text) ? "" : " (" + a.Answer.Text + ")")),
        }));
        return Fill(t["prompt"],
            ("ticket_key", context.TicketKey),
            ("ticket_title", context.TicketTitle),
            ("branch", context.Branch),
            ("plan", plan),
            ("answers", answers),
            ("git_log", string.IsNullOrWhiteSpace(summary.GitLog) ? t["no_commits"] : summary.GitLog.Trim()),
            ("git_status", string.IsNullOrWhiteSpace(summary.GitStatus) ? t["clean"] : summary.GitStatus.Trim()));
    }

    private static string Steps(PlanPayload plan, string indent = "") =>
        string.Join("\n", plan.Steps.Select((step, i) => string.Create(CultureInfo.InvariantCulture, $"{indent}{i + 1}. {step}")));

    /// <summary>Each placeholder once, in one pass, so text from an answer is never filled in again.</summary>
    private static string Fill(string template, params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(v => "{{" + v.Name + "}}", v => v.Value, StringComparer.Ordinal);
        return Placeholder().Replace(template, m => map.TryGetValue(m.Value, out var v) ? v : m.Value);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\{\{[a-z_]+\}\}")]
    private static partial System.Text.RegularExpressions.Regex Placeholder();

    /// <summary>A prompt file with several parts, each under a "## name" line.</summary>
    private static Dictionary<string, string> Sections(string text)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? name = null;
        var body = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (name is not null)
                {
                    sections[name] = body.ToString().Trim();
                }
                name = line[3..].Trim();
                body.Clear();
            }
            else
            {
                body.Append(line).Append('\n');
            }
        }
        if (name is not null)
        {
            sections[name] = body.ToString().Trim();
        }
        return sections;
    }

    private static string ReadPrompt(string version)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"Tikai.Adapters.ClaudeCode.Prompts.{version}.md");
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Prompt {name} is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }
}
