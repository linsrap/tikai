using System.Text.Json.Nodes;
using Tikai.Core.Attention;
using Tikai.Core.Llm;
using Tikai.Core.Runs;

namespace Tikai.Core.Agents;

/// <summary>
/// Knows how to run one kind of agent CLI: which files it needs in the run directory, its command line and environment,
/// and how to read its output. The orchestrator owns the sandbox (user, mounts, limits, network): an adapter cannot
/// loosen it.
/// </summary>
public interface IAgentAdapter
{
    /// <summary>e.g. "claude-code"; stored on the run.</summary>
    string Kind { get; }

    /// <summary>The agent image; the orchestrator also runs the run's prep step in it (it has git).</summary>
    string Image { get; }

    /// <summary>Files and directories the agent needs in the run directory besides the clone (e.g. its MCP config).</summary>
    IReadOnlyList<RunFile> RunFiles(AgentRunContext context);

    ContainerSpec BuildContainer(AgentRunContext context);

    /// <summary>
    /// A new container for a run that already had one: the agent's own session continued with the answers, or a fresh
    /// session with a summary when the session cannot be resumed.
    /// </summary>
    ContainerSpec BuildResumeContainer(AgentRunContext context, RunContinuation continuation);

    /// <summary>
    /// Where the agent keeps the session (relative to the run directory), so a helper container can check that it
    /// still exists before a resume; null when the agent has no resumable sessions.
    /// </summary>
    string? SessionFile(string sessionId);

    /// <summary>A parser for one container's output, fed line by line.</summary>
    IAgentOutputParser CreateOutputParser();
}

/// <summary>What an adapter needs to know about a run. ToString hides the token and the key.</summary>
/// <param name="McpUrl">The hub's MCP endpoint as the agent container reaches it.</param>
/// <param name="ToolCallTimeout">How long one blocking MCP call may stay open (the agent's own limit).</param>
public sealed record AgentRunContext(
    Guid RunId,
    string TicketKey,
    string TicketTitle,
    string Branch,
    LlmConnection Llm,
    string McpUrl,
    string RunToken,
    TimeSpan ToolCallTimeout)
{
    public override string ToString() =>
        $"AgentRunContext {{ RunId = {RunId}, TicketKey = {TicketKey}, Branch = {Branch}, Llm = {Llm}, McpUrl = {McpUrl}, RunToken = *** }}";
}

/// <summary>Where things are inside an agent container: the run's directory is mounted at /run.</summary>
public static class RunLayout
{
    public const string Root = "/run";
    public const string Work = "/run/work";
    public const string Home = "/run/home";
    public const string Prep = "/run/prep";
}

/// <summary>A file or directory written into the run directory by the hub.</summary>
/// <param name="Path">Relative to the run directory, with forward slashes.</param>
/// <param name="Content">Null for a directory.</param>
/// <param name="AgentOwned">Owned by the agent's uid (it may change it); otherwise by root, so the agent can only read it.</param>
public sealed record RunFile(string Path, byte[]? Content, bool AgentOwned, UnixFileMode Mode)
{
    public static RunFile Directory(string path, bool agentOwned, UnixFileMode mode) => new(path, null, agentOwned, mode);

    public static RunFile ReadOnly(string path, byte[] content) => new(path, content, AgentOwned: false, Modes.ReadOnly);

    public override string ToString() => $"RunFile {{ Path = {Path}, Bytes = {Content?.Length}, AgentOwned = {AgentOwned}, Mode = {Mode} }}";

    public static class Modes
    {
        public const UnixFileMode ReadOnly = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        public const UnixFileMode Executable = ReadOnly | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        public const UnixFileMode Directory = Executable | UnixFileMode.UserWrite;
        public const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    }
}

/// <summary>The agent-specific part of a container: image, command line, environment. ToString shows no values of secrets.</summary>
public sealed record ContainerSpec(
    string Image,
    IReadOnlyList<string> Entrypoint,
    IReadOnlyList<string> Command,
    IReadOnlyList<ContainerEnv> Environment,
    string WorkingDirectory)
{
    public override string ToString() =>
        $"ContainerSpec {{ Image = {Image}, Entrypoint = [{string.Join(", ", Entrypoint)}], Command = {Command.Count} arguments, " +
        $"Environment = [{string.Join(", ", Environment.Select(e => e.Name))}], WorkingDirectory = {WorkingDirectory} }}";
}

/// <param name="IsSecret">Never shown; the value is also removed from the agent's output.</param>
public sealed record ContainerEnv(string Name, string Value, bool IsSecret = false)
{
    public override string ToString() => $"{Name}={(IsSecret ? "***" : Value)}";
}

/// <summary>Turns an agent's stdout into run events and the facts the orchestrator acts on.</summary>
public interface IAgentOutputParser
{
    /// <summary>Events for one (already redacted) line of stdout.</summary>
    IReadOnlyList<ParsedEvent> ParseLine(string line);

    /// <summary>Set once the agent reported its start.</summary>
    AgentStart? Start { get; }

    /// <summary>Set once the agent printed its final report.</summary>
    AgentResult? Result { get; }

    /// <summary>Cost and tokens from the final report, when the agent gave them.</summary>
    AgentCost? Cost { get; }
}

/// <summary>How a new container continues a run.</summary>
public abstract record RunContinuation;

/// <summary>Continue the agent's own session; the answers it waited for come as a message.</summary>
public sealed record ResumeSession(string SessionId, IReadOnlyList<AnsweredRequest> Answers) : RunContinuation;

/// <summary>
/// A fresh session that knows only what the hub knows: the approved plan, the questions and answers so far, and the
/// state of the run clone (from a helper container: the hub never runs git there).
/// </summary>
public sealed record FreshSession(PlanPayload? ApprovedPlan, IReadOnlyList<AnsweredRequest> Answers, string GitLog, string GitStatus) : RunContinuation;

/// <summary>A question or plan of the agent and the human's answer.</summary>
/// <param name="Question">Set for a question.</param>
/// <param name="Plan">Set for a plan approval.</param>
public sealed record AnsweredRequest(AttentionKind Kind, QuestionPayload? Question, PlanPayload? Plan, AttentionAnswer Answer);

public sealed record ParsedEvent(RunEventKind Kind, JsonObject Payload);

/// <param name="ToolsConnected">The Tikai MCP server is connected; without it the agent cannot see its ticket or ask anything.</param>
/// <param name="Problem">Why the tools are not connected, for people.</param>
public sealed record AgentStart(string? SessionId, bool ToolsConnected, string? Problem);

public sealed record AgentCost(decimal? Usd, long? Tokens);
