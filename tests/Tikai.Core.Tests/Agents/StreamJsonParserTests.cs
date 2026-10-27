using Tikai.Adapters.ClaudeCode;
using Tikai.Core.Agents;
using Tikai.Core.Runs;

namespace Tikai.Core.Tests.Agents;

// Line shapes as Claude Code 2.1.x prints them with --output-format stream-json --verbose.
public class StreamJsonParserTests
{
    private const string Init = """{"type":"system","subtype":"init","cwd":"/run/work","session_id":"c4dfd85f-61e4-4063-bc6a-9b114a060039","tools":["Bash","Read","mcp__tikai__get_ticket"],"mcp_servers":[{"name":"tikai","status":"connected"}],"model":"claude-opus-5-5","permissionMode":"dontAsk","apiKeySource":"ANTHROPIC_API_KEY","claude_code_version":"2.1.310","uuid":"u1"}""";

    [Fact]
    public void Init_reports_the_session_and_a_connected_server()
    {
        var parser = new StreamJsonParser();

        var events = parser.ParseLine(Init);

        var e = Assert.Single(events);
        Assert.Equal(RunEventKind.System, e.Kind);
        Assert.Equal("2.1.310", (string?)e.Payload["claude_code_version"]);
        Assert.Equal("connected", (string?)e.Payload["mcp_servers"]![0]!["status"]);
        Assert.Equal(3, e.Payload["tools"]!.AsArray().Count);
        Assert.Equal(new Core.Agents.AgentStart("c4dfd85f-61e4-4063-bc6a-9b114a060039", true, null), parser.Start);
    }

    [Theory]
    [InlineData("""[{"name":"tikai","status":"failed"}]""", "status \"failed\"")]
    [InlineData("""[{"name":"other","status":"connected"}]""", "missing")]
    [InlineData("[]", "missing")]
    public void Init_without_a_connected_tikai_server_is_a_problem(string servers, string expected)
    {
        var parser = new StreamJsonParser();

        parser.ParseLine($$"""{"type":"system","subtype":"init","session_id":"s","mcp_servers":{{servers}}}""");

        Assert.False(parser.Start!.ToolsConnected);
        Assert.Contains(expected, parser.Start.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_assistant_message_becomes_text_thinking_and_tool_calls()
    {
        var events = new StreamJsonParser().ParseLine("""
            {"type":"assistant","message":{"id":"msg_1","role":"assistant","content":[{"type":"thinking","thinking":"Let me look.","signature":"x"},{"type":"text","text":"Reading the ticket."},{"type":"tool_use","id":"toolu_1","name":"mcp__tikai__get_ticket","input":{}},{"type":"redacted_thinking","data":"zzz"}]},"parent_tool_use_id":null,"session_id":"s"}
            """);

        Assert.Equal([RunEventKind.Message, RunEventKind.Message, RunEventKind.ToolCall], events.Select(e => e.Kind));
        Assert.Equal("Let me look.", (string?)events[0].Payload["thinking"]);
        Assert.Equal("Reading the ticket.", (string?)events[1].Payload["text"]);
        Assert.Equal("toolu_1", (string?)events[2].Payload["id"]);
        Assert.Equal("mcp__tikai__get_ticket", (string?)events[2].Payload["name"]);
    }

    [Fact]
    public void Tool_results_keep_their_text_as_a_string_or_from_blocks()
    {
        var parser = new StreamJsonParser();

        var plain = parser.ParseLine("""{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"42 files","is_error":false}]}}""");
        var blocks = parser.ParseLine("""{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_2","type":"tool_result","content":[{"type":"text","text":"line 1"},{"type":"image","source":{}},{"type":"text","text":"line 2"}],"is_error":true}]},"tool_use_result":{"stdout":"ignored"}}""");

        Assert.Equal("42 files", (string?)Assert.Single(plain).Payload["content"]);
        var result = Assert.Single(blocks);
        Assert.Equal(RunEventKind.ToolResult, result.Kind);
        Assert.Equal("line 1\n[image]\nline 2", (string?)result.Payload["content"]);
        Assert.True((bool)result.Payload["is_error"]!);
        Assert.Equal("toolu_2", (string?)result.Payload["tool_use_id"]);
    }

    [Fact]
    public void The_result_gives_outcome_cost_and_tokens()
    {
        var parser = new StreamJsonParser();

        var e = Assert.Single(parser.ParseLine("""{"type":"result","subtype":"success","is_error":false,"duration_ms":31000,"num_turns":5,"result":"Done.","session_id":"s","total_cost_usd":0.0025,"usage":{"input_tokens":10,"cache_creation_input_tokens":20,"cache_read_input_tokens":30,"output_tokens":40},"permission_denials":[]}"""));

        Assert.Equal(RunEventKind.Result, e.Kind);
        Assert.Equal(5, (int)e.Payload["num_turns"]!);
        Assert.Equal(new Core.Runs.AgentResult(false, "success"), parser.Result);
        Assert.Equal(0.0025m, parser.Cost!.Usd);
        Assert.Equal(100, parser.Cost.Tokens);
    }

    [Fact]
    public void Tokens_come_from_each_messages_usage_once_also_without_a_result()
    {
        var parser = new StreamJsonParser();
        static string Line(string id, int output, string block) =>
            """{"type":"assistant","message":{"id":"ID","role":"assistant","content":[BLOCK],"usage":{"input_tokens":2,"cache_creation_input_tokens":100,"cache_read_input_tokens":1000,"output_tokens":OUT}}}"""
                .Replace("ID", id, StringComparison.Ordinal).Replace("BLOCK", block, StringComparison.Ordinal).Replace("OUT", $"{output}", StringComparison.Ordinal);

        // One API message printed as two lines (thinking, then text), with its usage on each; then a second message.
        parser.ParseLine(Line("msg_1", 10, """{"type":"thinking","thinking":""}"""));
        parser.ParseLine(Line("msg_1", 30, """{"type":"text","text":"Asking."}"""));
        parser.ParseLine(Line("msg_2", 5, """{"type":"tool_use","id":"t","name":"mcp__tikai__ask_human","input":{}}"""));

        // Stopped while waiting: no result, but the tokens are known.
        Assert.Equal(new AgentCost(null, 1132 + 1107), parser.Cost);

        parser.ParseLine("""{"type":"result","subtype":"success","is_error":false,"num_turns":2,"total_cost_usd":0.163077,"usage":{"input_tokens":1,"output_tokens":1}}""");
        Assert.Equal(new AgentCost(0.163077m, 1132 + 1107), parser.Cost);
    }

    [Fact]
    public void An_error_result_is_an_error()
    {
        var parser = new StreamJsonParser();

        parser.ParseLine("""{"type":"result","subtype":"error_max_turns","is_error":true,"num_turns":1}""");

        Assert.Equal(new Core.Runs.AgentResult(true, "error_max_turns"), parser.Result);
        Assert.Null(parser.Cost!.Usd);
        Assert.Null(parser.Cost.Tokens);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void Other_output_becomes_a_log_line(string line)
    {
        var e = Assert.Single(new StreamJsonParser().ParseLine(line));

        Assert.Equal(RunEventKind.Log, e.Kind);
        Assert.Equal(line, (string?)e.Payload["text"]);
    }

    [Fact]
    public void Unknown_message_types_are_logged_with_their_type()
    {
        var e = Assert.Single(new StreamJsonParser().ParseLine("""{"type":"system","subtype":"compact_boundary","x":1}"""));

        Assert.Equal(RunEventKind.Log, e.Kind);
        Assert.Equal("compact_boundary", (string?)e.Payload["subtype"]);
    }

    [Fact]
    public void Heartbeats_of_a_waiting_tool_are_not_stored_but_other_tool_progress_is()
    {
        var parser = new StreamJsonParser();

        Assert.Empty(parser.ParseLine("""{"type":"tool_progress","tool_use_id":"toolu_1-heartbeat-0","tool_name":"mcp__tikai__propose_plan","parent_tool_use_id":"toolu_1","elapsed_time_seconds":31,"heartbeat":true,"session_id":"s"}"""));
        Assert.Equal(RunEventKind.Log, Assert.Single(parser.ParseLine("""{"type":"tool_progress","tool_use_id":"toolu_2","tool_name":"Bash","elapsed_time_seconds":5}""")).Kind);
    }

    [Fact]
    public void Blank_lines_give_nothing()
    {
        Assert.Empty(new StreamJsonParser().ParseLine("   "));
    }
}
