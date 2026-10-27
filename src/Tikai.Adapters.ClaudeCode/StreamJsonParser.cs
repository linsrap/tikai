using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tikai.Core.Agents;
using Tikai.Core.Runs;

namespace Tikai.Adapters.ClaudeCode;

/// <summary>
/// Reads Claude Code's <c>--output-format stream-json --verbose</c> output: one JSON object per line, of type
/// system (init and others), assistant (text, thinking, tool_use blocks), user (tool_result blocks) and result.
/// Keeps what a reviewer needs; the full transcript stays in the run's config directory.
/// </summary>
public sealed class StreamJsonParser : IAgentOutputParser
{
    public const string McpServerName = "tikai";

    private static readonly string[] _tokenFields = ["input_tokens", "output_tokens", "cache_creation_input_tokens", "cache_read_input_tokens"];

    public AgentStart? Start { get; private set; }

    public AgentResult? Result { get; private set; }

    /// <summary>
    /// The session's cost as Claude Code reports it at the end (after --resume it includes the session's earlier
    /// containers, also one stopped before it reported), and this container's tokens. Tokens are summed from each
    /// API message's usage, because the result's usage covers only this process, and a container stopped while it
    /// waits never prints a result.
    /// </summary>
    public AgentCost? Cost =>
        _messageTokens.Count > 0 ? new AgentCost(_usd, _messageTokens.Values.Sum())
        : Result is not null ? new AgentCost(_usd, _resultTokens)
        : null;

    // Per API message id: Claude Code prints one line per content block, each with the message's usage so far.
    private readonly Dictionary<string, long> _messageTokens = new(StringComparer.Ordinal);
    private decimal? _usd;
    private long? _resultTokens;

    public IReadOnlyList<ParsedEvent> ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }
        JsonObject message;
        try
        {
            message = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException)
        {
            return [Log("stdout", line)];
        }

        var type = Text(message["type"]);
        var subtype = Text(message["subtype"]);
        return (type, subtype) switch
        {
            ("system", "init") => [Init(message)],
            ("assistant", _) => Assistant(message),
            ("user", _) => User(message),
            ("result", _) => [ResultEvent(message)],
            // Claude Code's echo of the hub's 30 s progress notification while a blocking tool waits: one line per
            // heartbeat for hours says nothing new (the attention item shows the wait).
            ("tool_progress", _) when message["heartbeat"] is JsonValue h && h.TryGetValue<bool>(out var heartbeat) && heartbeat => [],
            _ => [new(RunEventKind.Log, new JsonObject
            {
                ["source"] = "agent",
                ["type"] = type,
                ["subtype"] = subtype,
                ["raw"] = RunEventPayload.Cut(line, 1_000),
            })],
        };
    }

    private ParsedEvent Init(JsonObject message)
    {
        var servers = message["mcp_servers"] as JsonArray ?? [];
        var tikai = servers.OfType<JsonObject>().FirstOrDefault(s => Text(s["name"]) == McpServerName);
        var status = tikai is null ? null : Text(tikai["status"]);
        var connected = status == "connected";
        Start = new AgentStart(
            Text(message["session_id"]),
            connected,
            connected ? null
                : status is null ? $"The MCP server \"{McpServerName}\" is missing from the agent's configuration."
                : $"The agent could not connect to the Tikai MCP server (status \"{status}\").");

        return new(RunEventKind.System, new JsonObject
        {
            ["subtype"] = "init",
            ["session_id"] = Text(message["session_id"]),
            ["model"] = Text(message["model"]),
            ["cwd"] = Text(message["cwd"]),
            ["claude_code_version"] = Text(message["claude_code_version"]),
            ["permission_mode"] = Text(message["permissionMode"]),
            ["api_key_source"] = Text(message["apiKeySource"]),
            ["tools"] = new JsonArray([.. (message["tools"] as JsonArray ?? []).Select(t => (JsonNode?)JsonValue.Create(Text(t)))]),
            ["mcp_servers"] = servers.DeepClone(),
        });
    }

    private List<ParsedEvent> Assistant(JsonObject message)
    {
        if (message["message"]?["id"] is JsonValue id && id.TryGetValue<string>(out var messageId) && message["message"]?["usage"] is JsonObject usage)
        {
            _messageTokens[messageId] = Tokens(usage);
        }
        var events = new List<ParsedEvent>();
        foreach (var block in Blocks(message))
        {
            switch (Text(block["type"]))
            {
                case "text":
                    events.Add(new(RunEventKind.Message, new JsonObject { ["role"] = "assistant", ["text"] = Text(block["text"]) }));
                    break;
                case "thinking":
                    events.Add(new(RunEventKind.Message, new JsonObject { ["role"] = "assistant", ["thinking"] = Text(block["thinking"]) }));
                    break;
                case "tool_use":
                    events.Add(new(RunEventKind.ToolCall, new JsonObject
                    {
                        ["id"] = Text(block["id"]),
                        ["name"] = Text(block["name"]),
                        ["input"] = block["input"]?.DeepClone(),
                        ["parent_tool_use_id"] = Text(message["parent_tool_use_id"]),
                    }));
                    break;
                default:
                    break; // redacted thinking and future block types carry nothing a reviewer can read
            }
        }
        return events;
    }

    private static List<ParsedEvent> User(JsonObject message)
    {
        var events = new List<ParsedEvent>();
        foreach (var block in Blocks(message))
        {
            switch (Text(block["type"]))
            {
                case "tool_result":
                    events.Add(new(RunEventKind.ToolResult, new JsonObject
                    {
                        ["tool_use_id"] = Text(block["tool_use_id"]),
                        ["is_error"] = block["is_error"] is JsonValue e && e.TryGetValue<bool>(out var isError) && isError,
                        ["content"] = Content(block["content"]),
                    }));
                    break;
                case "text":
                    events.Add(new(RunEventKind.Message, new JsonObject { ["role"] = "user", ["text"] = Text(block["text"]) }));
                    break;
                default:
                    break;
            }
        }
        return events;
    }

    private ParsedEvent ResultEvent(JsonObject message)
    {
        var isError = message["is_error"] is JsonValue e && e.TryGetValue<bool>(out var error) && error;
        var subtype = Text(message["subtype"]);
        Result = new AgentResult(isError, subtype);
        var usage = message["usage"] as JsonObject;
        _resultTokens = usage is null ? null : Tokens(usage);
        _usd = message["total_cost_usd"] is JsonValue c && c.TryGetValue<decimal>(out var cost) ? cost : null;

        return new(RunEventKind.Result, new JsonObject
        {
            ["subtype"] = subtype,
            ["is_error"] = isError,
            ["num_turns"] = message["num_turns"]?.DeepClone(),
            ["duration_ms"] = message["duration_ms"]?.DeepClone(),
            ["total_cost_usd"] = message["total_cost_usd"]?.DeepClone(),
            ["usage"] = usage?.DeepClone(),
            ["result"] = Text(message["result"]),
            ["permission_denials"] = message["permission_denials"]?.DeepClone(),
            ["session_id"] = Text(message["session_id"]),
        });
    }

    private static long Tokens(JsonObject usage) =>
        _tokenFields.Sum(name => usage[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0);

    public static ParsedEvent Log(string stream, string text) =>
        new(RunEventKind.Log, new JsonObject { ["source"] = "agent", ["stream"] = stream, ["text"] = text });

    private static IEnumerable<JsonObject> Blocks(JsonObject message) =>
        (message["message"]?["content"] as JsonArray ?? []).OfType<JsonObject>();

    /// <summary>A tool result is a string or a list of blocks; only text is kept.</summary>
    private static string Content(JsonNode? content)
    {
        if (content is JsonArray parts)
        {
            var text = new StringBuilder();
            foreach (var part in parts.OfType<JsonObject>())
            {
                text.Append(Text(part["type"]) == "text" ? Text(part["text"]) : $"[{Text(part["type"])}]").Append('\n');
            }
            return text.ToString().TrimEnd('\n');
        }
        return Text(content) ?? "";
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
