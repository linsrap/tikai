using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Tikai.Adapters.ClaudeCode;
using Tikai.Core.Agents;
using Tikai.Core.Attention;
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

    private static AnsweredRequest Question(string question, string answer) =>
        new(AttentionKind.Question, new QuestionPayload(question, null, []), null, new AttentionAnswer(answer, null));

    private static AnsweredRequest Plan(PlanDecision decision, string? notes, params string[] steps) =>
        new(AttentionKind.PlanApproval, null, new PlanPayload(steps, [], []), new AttentionAnswer(notes, decision));

    [Fact]
    public void A_resume_continues_the_session_with_the_answer_as_the_message_and_the_same_flags()
    {
        var start = _adapter.BuildContainer(Context()).Command;
        var resume = _adapter.BuildResumeContainer(Context(), new ResumeSession("c4dfd85f-61e4-4063-bc6a-9b114a060039", [Question("What word should I write?", "falcon")])).Command;

        Assert.Equal("-p", resume[0]);
        Assert.Equal("c4dfd85f-61e4-4063-bc6a-9b114a060039", After(resume, "--resume"));
        // Everything after the prompt is the start's command line, plus --resume.
        Assert.Equal(start.Skip(2), resume.Skip(4));
        Assert.StartsWith("The human answered your ask_human question \"What word should I write?\". The answer is: falcon", resume[1], StringComparison.Ordinal);
        Assert.Contains("Do not repeat the interrupted call", resume[1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_resume_message_has_a_sentence_per_answer_and_says_what_to_do_with_a_plan_decision()
    {
        var message = ClaudeCodeAdapter.ResumePrompt(
        [
            Plan(PlanDecision.Approve, null, "a"),
            Plan(PlanDecision.Approve, "keep it short", "a"),
            Plan(PlanDecision.Revise, "add tests", "a"),
            Question("Colour?", "{{answers}} blue"),
        ]);

        Assert.Contains("The human approved the plan you proposed with propose_plan.\n\n", message, StringComparison.Ordinal);
        Assert.Contains("with these notes: keep it short", message, StringComparison.Ordinal);
        Assert.Contains("Their notes: add tests\nChange the plan as the notes say and call propose_plan again", message, StringComparison.Ordinal);
        // Text from an answer is never filled in again.
        Assert.Contains("The answer is: {{answers}} blue", message, StringComparison.Ordinal);
        Assert.DoesNotContain("{{question}}", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fresh_session_gets_the_summary_prompt_with_plan_answers_and_git_state()
    {
        var summary = new FreshSession(
            new PlanPayload(["Read the code", "Write notes.txt"], [], []),
            [Plan(PlanDecision.Approve, null, "Read the code", "Write notes.txt"), Question("Which word?", "falcon")],
            "fda09dd Add notes.txt",
            "?? draft.txt");

        var command = _adapter.BuildResumeContainer(Context(), summary).Command;

        Assert.DoesNotContain("--resume", command);
        var prompt = command[1];
        Assert.StartsWith("You are continuing work on the Tikai ticket HUB-42: Add a login page", prompt, StringComparison.Ordinal);
        Assert.Contains("The approved plan:\n1. Read the code\n2. Write notes.txt\n", prompt, StringComparison.Ordinal);
        Assert.Contains("- You proposed a plan:\n    1. Read the code\n    2. Write notes.txt\n  Decision: approve\n", prompt, StringComparison.Ordinal);
        Assert.Contains("- You asked: Which word?\n  Answer: falcon", prompt, StringComparison.Ordinal);
        Assert.Contains("git log of the run branch, newest first:\nfda09dd Add notes.txt\n", prompt, StringComparison.Ordinal);
        Assert.Contains("git status --short:\n?? draft.txt\n", prompt, StringComparison.Ordinal);
        Assert.Contains("tikai/HUB-42-4e5f6071", prompt, StringComparison.Ordinal);
        Assert.Contains("submit_for_review", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_summary_without_plan_answers_or_commits_says_so()
    {
        var prompt = ClaudeCodeAdapter.SummaryPrompt(Context(), new FreshSession(null, [], "", ""));

        Assert.Contains("The approved plan:\n(none yet)", prompt, StringComparison.Ordinal);
        Assert.Contains("oldest first (these answers are final):\n(none)", prompt, StringComparison.Ordinal);
        Assert.Contains("newest first:\n(no commits on the run branch yet)", prompt, StringComparison.Ordinal);
        Assert.Contains("--short:\n(clean)", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_session_file_is_where_claude_code_keeps_sessions_for_the_work_directory()
    {
        Assert.Equal("claude/projects/-run-work/abc.jsonl", _adapter.SessionFile("abc"));
    }

    private static string After(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];
}
