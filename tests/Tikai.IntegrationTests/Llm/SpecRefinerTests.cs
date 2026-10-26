using System.Text.Json;
using Anthropic.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Tikai.Core.Llm;
using Tikai.Core.Tickets;
using Tikai.Llm;
using Tikai.Llm.Refine;

namespace Tikai.IntegrationTests.Llm;

/// <summary>Refine spec: the mapping and the request with a fake IChatClient, the wire format with the real SDK against a fake Messages API.</summary>
public sealed class SpecRefinerTests
{
    private static readonly LlmConnection _connection = new(LlmProviderKind.Anthropic, "sk-ant-test-0123456789", "claude-opus-5-5", null);
    private static readonly SpecDraft _draft = new("Tikai Hub", "login", "users need to log in\n\n## Notes\nuse cookies", ["works", "fast"]);

    [Fact]
    public void The_answer_is_cleaned_to_fit_a_ticket()
    {
        var proposal = SpecRefiner.Map(new RefinedSpec
        {
            Title = "  Add a\nlogin page  ",
            Description = "  ## Goal\n\nPeople can log in.\n",
            Criteria = ["- Wrong passwords are rejected", "2. A session cookie is set", "  ", null, "a session cookie is SET", new string('x', TicketRules.MaxCriterionLength + 1), "Spans\ntwo lines"],
            Questions = ["1) Which session length?", "", "Which session length?"],
        });

        Assert.Equal("Add a login page", proposal.Title);
        Assert.Equal("## Goal\n\nPeople can log in.", proposal.Description);
        Assert.Equal(["Wrong passwords are rejected", "A session cookie is set", "Spans two lines"], proposal.Criteria);
        Assert.Equal(["Which session length?"], proposal.Questions);
        Assert.Equal("refine-spec.v1", proposal.PromptVersion);
    }

    [Fact]
    public void Unusable_fields_become_no_proposal_and_lists_are_capped()
    {
        var proposal = SpecRefiner.Map(new RefinedSpec
        {
            Title = new string('t', TicketRules.MaxTitleLength + 1),
            Description = "   ",
            Criteria = [.. Enumerable.Range(1, TicketRules.MaxCriteria + 5).Select(i => $"Criterion {i}")],
            Questions = null,
        });

        Assert.Null(proposal.Title);
        Assert.Null(proposal.Description);
        Assert.Equal(TicketRules.MaxCriteria, proposal.Criteria.Count);
        Assert.Empty(proposal.Questions);
        Assert.Null(SpecRefiner.Map(new RefinedSpec { Description = new string('d', TicketRules.MaxDescriptionLength + 1) }).Description);
    }

    [Fact]
    public async Task The_versioned_prompt_and_the_draft_reach_the_model_with_a_schema()
    {
        var chat = new FakeChatClient(_ => Reply("""{"title":"Add login","description":"## Goal\n\nLog in.","criteria":["Works"],"questions":[]}"""));

        var result = await Refiner(chat).RefineAsync(_connection, _draft, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal("Add login", result.Proposal!.Title);
        var (messages, options) = Assert.Single(chat.Calls);
        Assert.Equal([ChatRole.System, ChatRole.User], messages.Select(m => m.Role));
        Assert.StartsWith("You help a software team turn a rough ticket into a clear spec.", messages[0].Text, StringComparison.Ordinal);
        Assert.Equal(
            "Project: Tikai Hub\nTitle: login\n\n<description>\nusers need to log in\n\n## Notes\nuse cookies\n</description>\n\n<acceptance_criteria>\n1. works\n2. fast\n</acceptance_criteria>\n",
            messages[1].Text.ReplaceLineEndings("\n"));
        var format = Assert.IsType<ChatResponseFormatJson>(options!.ResponseFormat);
        var properties = format.Schema!.Value.GetProperty("properties");
        Assert.Equal(["title", "description", "criteria", "questions"], properties.EnumerateObject().Select(p => p.Name));
        Assert.Equal(SpecRefiner.MaxOutputTokens, options.MaxOutputTokens);
    }

    [Theory]
    [InlineData("not json", null, "not a complete proposal")]
    [InlineData("{\"title\":\"Add lo", "length", "cut off at the output limit")]
    public async Task An_answer_that_is_not_complete_JSON_is_an_error(string text, string? finishReason, string expected)
    {
        var chat = new FakeChatClient(_ => Reply(text, finishReason is null ? ChatFinishReason.Stop : new ChatFinishReason(finishReason)));

        var result = await Refiner(chat).RefineAsync(_connection, _draft, TestContext.Current.CancellationToken);

        Assert.Null(result.Proposal);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_errors_become_messages()
    {
        var chat = new FakeChatClient(_ => throw new AnthropicIOException("connection refused", new HttpRequestException("refused")));

        var result = await Refiner(chat).RefineAsync(_connection, _draft, TestContext.Current.CancellationToken);

        Assert.Null(result.Proposal);
        Assert.StartsWith("Could not reach the provider", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_real_SDK_sends_the_schema_as_structured_output_and_reads_the_answer()
    {
        var answer = JsonSerializer.Serialize(new { title = "Add login", description = "## Goal\n\nLog in.", criteria = new List<string> { "Works" }, questions = new List<string> { "Which IdP?" } });
        await using var server = await FakeAnthropicServer.StartAsync(body: FakeAnthropicServer.TextReply(answer));

        var result = await Refiner(new ChatClientFactory()).RefineAsync(_connection with { BaseUrl = server.BaseUrl }, _draft, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal(["Works"], result.Proposal!.Criteria);
        Assert.Equal(["Which IdP?"], result.Proposal.Questions);
        var body = Assert.Single(server.Requests).Body;
        var format = body.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("schema").GetProperty("properties").TryGetProperty("criteria", out _));
        Assert.StartsWith("You help a software team", body.GetProperty("system")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal(SpecRefiner.MaxOutputTokens, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task The_real_SDK_reports_the_providers_message()
    {
        await using var server = await FakeAnthropicServer.StartAsync(
            StatusCodes.Status429TooManyRequests, FakeAnthropicServer.Error("rate_limit_error", "Your credit balance is too low."));

        var result = await Refiner(new ChatClientFactory()).RefineAsync(_connection with { BaseUrl = server.BaseUrl }, _draft, TestContext.Current.CancellationToken);

        Assert.Equal("The provider is rate limiting this key, or it is out of credit. Anthropic says: Your credit balance is too low.", result.Error);
    }

    private static SpecRefiner Refiner(IChatClient chat) => Refiner(new SingleClientFactory(chat));

    private static SpecRefiner Refiner(IChatClientFactory factory) => new(factory, NullLogger<SpecRefiner>.Instance);

    private static ChatResponse Reply(string text, ChatFinishReason? finishReason = null) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = finishReason ?? ChatFinishReason.Stop };

    private sealed class SingleClientFactory(IChatClient chat) : IChatClientFactory
    {
        public IChatClient Create(LlmConnection connection) => chat;
    }
}

/// <summary>Answers every call with <paramref name="reply"/> and records what was sent.</summary>
internal sealed class FakeChatClient(Func<IList<ChatMessage>, ChatResponse> reply) : IChatClient
{
    public List<(IList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Calls.Add((list, options));
        return Task.FromResult(reply(list));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
