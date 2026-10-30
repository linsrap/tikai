using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Tikai.Core.Llm;
using Tikai.Core.Reviews;
using Tikai.Llm;
using Tikai.Llm.Reviews;

namespace Tikai.IntegrationTests.Llm;

/// <summary>The review analysis call with the real SDK against a fake Messages API: prompt, schema, parsing, errors.</summary>
public sealed class ReviewAnalyzerTests
{
    private static readonly LlmConnection _connection = new(LlmProviderKind.Anthropic, "sk-ant-test-0123456789", "claude-sonnet-5-5", null);

    [Fact]
    public async Task The_prompt_message_and_a_schema_with_required_lists_reach_the_provider_and_the_answer_is_parsed()
    {
        var answer = """
            {"summary":"Adds login.","coverage":[{"criterion_id":"c1","status":"covered","note":"Login.cs","refs":[{"path":"src/Login.cs","line_start":3,"line_end":9}]}],
             "groups":[{"title":"Endpoint","layer":"api","note":"The endpoint.","files":["src/Login.cs"]}],
             "files":[{"path":"src/Login.cs","risk":"high","reason":"Checks passwords."}]}
            """;
        await using var server = await FakeAnthropicServer.StartAsync(body: FakeAnthropicServer.TextReply(answer, inputTokens: 1234, outputTokens: 321));

        var result = await Analyzer().AnalyzeAsync(_connection with { BaseUrl = server.BaseUrl }, "Ticket: HUB-1 — Login", AnalysisTier.Strict, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal((1234L, 321L), (result.InputTokens, result.OutputTokens));
        var parsed = result.Answer!;
        Assert.Equal("Adds login.", parsed.Summary);
        var coverage = Assert.Single(parsed.Coverage!)!;
        Assert.Equal(("c1", "covered", 3, 9), (coverage.CriterionId, coverage.Status, coverage.Refs![0]!.LineStart, coverage.Refs[0]!.LineEnd));
        Assert.Equal("api", parsed.Groups![0]!.Layer);
        Assert.Equal("high", parsed.Files![0]!.Risk);

        var request = Assert.Single(server.Requests);
        Assert.Equal("claude-sonnet-5-5", request.Model);
        var body = request.Body;
        Assert.Equal(ReviewAnalyzer.MaxOutputTokens(AnalysisTier.Strict), body.GetProperty("max_tokens").GetInt32());
        Assert.StartsWith("You help a human reviewer check a code change", body.GetProperty("system")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("Ticket: HUB-1 — Login", body.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());
        var schema = body.GetProperty("output_config").GetProperty("format").GetProperty("schema");
        Assert.Equal(["summary", "coverage", "groups", "files"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        // Every object in the schema lists its required properties, or the SDK would drop it.
        Assert.All(Objects(schema), o => Assert.True(o.TryGetProperty("required", out _)));
    }

    [Theory]
    [InlineData(StatusCodes.Status429TooManyRequests, "rate_limit_error", true, "The provider is rate limiting this key")]
    [InlineData(529, "overloaded_error", true, "The provider")]
    [InlineData(StatusCodes.Status401Unauthorized, "authentication_error", false, "The provider rejected the API key.")]
    [InlineData(StatusCodes.Status404NotFound, "not_found_error", false, "The model \"claude-sonnet-5-5\" was not found")]
    public async Task Provider_errors_say_whether_a_retry_may_help(int status, string type, bool transient, string message)
    {
        await using var server = await FakeAnthropicServer.StartAsync(status, FakeAnthropicServer.Error(type, "Nope."));

        var result = await Analyzer().AnalyzeAsync(_connection with { BaseUrl = server.BaseUrl }, "x", AnalysisTier.Normal, TestContext.Current.CancellationToken);

        Assert.Null(result.Answer);
        Assert.Equal(transient, result.Transient);
        Assert.StartsWith(message, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json", "end_turn", "not a complete analysis")]
    [InlineData("{\"summary\":\"Add", "max_tokens", "cut off at the output limit")]
    public async Task An_incomplete_answer_is_a_permanent_error(string text, string stopReason, string expected)
    {
        await using var server = await FakeAnthropicServer.StartAsync(body: FakeAnthropicServer.TextReply(text, stopReason));

        var result = await Analyzer().AnalyzeAsync(_connection with { BaseUrl = server.BaseUrl }, "x", AnalysisTier.Light, TestContext.Current.CancellationToken);

        Assert.Null(result.Answer);
        Assert.False(result.Transient);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    private static ReviewAnalyzer Analyzer() => new(new ChatClientFactory(), NullLogger<ReviewAnalyzer>.Instance);

    private static IEnumerable<JsonElement> Objects(JsonElement schema)
    {
        if (schema.TryGetProperty("type", out var type) && type.GetString() == "object")
        {
            yield return schema;
            foreach (var property in schema.GetProperty("properties").EnumerateObject())
            {
                foreach (var inner in Objects(property.Value))
                {
                    yield return inner;
                }
            }
        }
        if (schema.TryGetProperty("items", out var items))
        {
            foreach (var inner in Objects(items))
            {
                yield return inner;
            }
        }
    }
}
