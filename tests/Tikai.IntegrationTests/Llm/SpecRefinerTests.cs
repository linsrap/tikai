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
    public void Only_what_differs_from_the_draft_is_proposed()
    {
        var proposal = SpecRefiner.Map(new RefinedSpec
        {
            Size = "LARGE",
            Title = "  Add a\nlogin page  ",
            Description = "  ## Goal\n\nPeople can log in.\n",
            Criteria =
            [
                new() { Replaces = 1, Text = "- Wrong passwords are rejected" },
                new() { Replaces = 1, Text = "A second rewrite of the same criterion" },
                new() { Replaces = 2, Text = "FAST" },
                new() { Replaces = 9, Text = "Outside the draft" },
                new() { Replaces = 0, Text = "2. A session cookie is set" },
                new() { Replaces = 0, Text = "a session cookie is SET" },
                new() { Replaces = 0, Text = "works" },
                new() { Replaces = 0, Text = "  " },
                new() { Replaces = 0, Text = new string('x', TicketRules.MaxCriterionLength + 1) },
                null,
            ],
            Assumptions = ["Assumed: sessions last a day", "- assumed:   sessions last a day", "", new string('a', SpecRefiner.MaxAssumptionLength + 1)],
            Questions =
            [
                new() { Question = "1) Which identity provider?", Options = ["GitHub", " Google ", "github"], Recommended = "google" },
                new() { Question = "Only one option?", Options = ["Yes"], Recommended = "Yes" },
                new() { Question = "Four options?", Options = ["a", "b", "c", "d"], Recommended = "a" },
                new() { Question = "Unknown recommendation?", Options = ["a", "b"], Recommended = "z" },
            ],
        }, _draft);

        Assert.Equal(SpecSize.Large, proposal.Size);
        Assert.Equal("Add a login page", proposal.Title);
        Assert.Equal("## Goal\n\nPeople can log in.", proposal.Description);
        Assert.Equal(
            [new CriterionChange(0, "works", "Wrong passwords are rejected"), new CriterionChange(null, null, "A session cookie is set")],
            proposal.Criteria);
        Assert.Equal(["sessions last a day"], proposal.Assumptions);
        Assert.Equal(2, proposal.Questions.Count);
        Assert.Equal(("Which identity provider?", "Google"), (proposal.Questions[0].Question, proposal.Questions[0].Recommended));
        Assert.Equal(["GitHub", "Google"], proposal.Questions[0].Options);
        Assert.Null(proposal.Questions[1].Recommended);
        Assert.Equal("refine-spec.v2", proposal.PromptVersion);
    }

    [Fact]
    public void The_size_sets_the_limits()
    {
        RefinedSpec Answer(string size) => new()
        {
            Size = size,
            Title = "login",
            Description = "## Goal\n\nA description a small ticket does not get.",
            Criteria = [new() { Replaces = 2, Text = "Logging in takes under a second" }, .. Enumerable.Range(1, 6).Select(i => new RefinedCriterion { Replaces = 0, Text = $"New {i}" })],
            Assumptions = [.. Enumerable.Range(1, 10).Select(i => $"Assumption {i}")],
            Questions = [.. Enumerable.Range(1, 4).Select(i => new RefinedQuestion { Question = $"Question {i}?", Options = ["a", "b"], Recommended = "a" })],
        };

        var small = SpecRefiner.Map(Answer("small"), _draft);
        Assert.Equal(SpecSize.Small, small.Size);
        Assert.Null(small.Title); // the same as the draft's
        Assert.Null(small.Description);
        // The draft has 2 criteria: a rewrite always counts, one new criterion fits under 3.
        Assert.Equal(["Logging in takes under a second", "New 1"], small.Criteria.Select(c => c.Text));
        Assert.Equal((3, 1), (small.Assumptions.Count, small.Questions.Count));

        // "More detail": at least medium.
        var medium = SpecRefiner.Map(Answer("small"), _draft with { MinSize = SpecSize.Medium });
        Assert.Equal(SpecSize.Medium, medium.Size);
        Assert.NotNull(medium.Description);
        Assert.Equal(4, medium.Criteria.Count);
        Assert.Equal((5, 2), (medium.Assumptions.Count, medium.Questions.Count));

        // An unknown size is large; a smaller minimum does not shrink it.
        var large = SpecRefiner.Map(Answer("huge"), _draft with { MinSize = SpecSize.Small });
        Assert.Equal((SpecSize.Large, 7, 8, 3), (large.Size, large.Criteria.Count, large.Assumptions.Count, large.Questions.Count));
        Assert.Null(SpecRefiner.Map(new RefinedSpec { Description = new string('d', TicketRules.MaxDescriptionLength + 1) }, _draft).Description);
    }

    [Fact]
    public async Task The_versioned_prompt_and_the_draft_reach_the_model_with_a_schema()
    {
        var chat = new FakeChatClient(_ => Reply("""{"size":"medium","title":"Add login","description":"## Goal\n\nLog in.","criteria":[{"replaces":0,"text":"Works offline"}],"assumptions":[],"questions":[]}"""));

        var result = await Refiner(chat).RefineAsync(_connection, _draft with { MinSize = SpecSize.Medium }, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal("Add login", result.Proposal!.Title);
        var (messages, options) = Assert.Single(chat.Calls);
        Assert.Equal([ChatRole.System, ChatRole.User], messages.Select(m => m.Role));
        Assert.StartsWith("You help a software team turn a rough ticket into a clear spec.", messages[0].Text, StringComparison.Ordinal);
        Assert.Equal(
            "Project: Tikai Hub\nTitle: login\nThe human asked for more detail: the size is at least medium.\n\n<description>\nusers need to log in\n\n## Notes\nuse cookies\n</description>\n\n<acceptance_criteria>\n1. works\n2. fast\n</acceptance_criteria>\n",
            messages[1].Text.ReplaceLineEndings("\n"));
        var format = Assert.IsType<ChatResponseFormatJson>(options!.ResponseFormat);
        var properties = format.Schema!.Value.GetProperty("properties");
        Assert.Equal(["size", "title", "description", "criteria", "assumptions", "questions"], properties.EnumerateObject().Select(p => p.Name));
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
        var answer = JsonSerializer.Serialize(new
        {
            size = "large",
            title = "Add login",
            description = "## Goal\n\nLog in.",
            criteria = new[] { new { replaces = 0, text = "Works offline" } },
            assumptions = new[] { "Sessions last a day" },
            questions = new[] { new { question = "Which IdP?", options = new[] { "GitHub", "Google" }, recommended = "GitHub" } },
        });
        await using var server = await FakeAnthropicServer.StartAsync(body: FakeAnthropicServer.TextReply(answer));

        var result = await Refiner(new ChatClientFactory()).RefineAsync(_connection with { BaseUrl = server.BaseUrl }, _draft, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal([new CriterionChange(null, null, "Works offline")], result.Proposal!.Criteria);
        Assert.Equal(["Sessions last a day"], result.Proposal.Assumptions);
        var question = Assert.Single(result.Proposal.Questions);
        Assert.Equal(("Which IdP?", "GitHub"), (question.Question, question.Recommended));
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
