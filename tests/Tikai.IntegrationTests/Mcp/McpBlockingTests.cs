using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using Tikai.Api.Attention;
using Tikai.Core.Attention;
using Tikai.Data;
using Tikai.IntegrationTests.Auth;

namespace Tikai.IntegrationTests.Mcp;

public sealed class McpBlockingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TikaiApiFactory _factory = null!;
    private BrowserClient _browser = null!;
    private bool _restarted;

    public async ValueTask InitializeAsync()
    {
        _factory = await TikaiApiFactory.CreateAsync(postgres, McpTestKit.FastHeartbeat);
        _browser = new BrowserClient(_factory.CreateClient(new() { HandleCookies = true }));
        await _browser.RefreshAntiforgeryAsync();
        await _browser.SignUpAdminAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _browser.Dispose();
        await _factory.DisposeAsync();
        // A restarted factory does not own the data directory (the first one deleted it when it stopped).
        if (_restarted && Directory.Exists(_factory.DataPath))
        {
            Directory.Delete(_factory.DataPath, recursive: true);
        }
    }

    [Fact]
    public async Task A_blocking_call_returns_when_the_item_is_answered_through_the_api()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (runId, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);
        var progress = new ConcurrentQueue<ProgressNotificationValue>();

        var ask = agent.CallAsync(
            "ask_human",
            new { question = "  Which database? ", context = "Choosing storage", options = new[] { "Postgres", " ", "SQLite" } },
            new SynchronousProgress(progress.Enqueue));
        var item = (await McpTestKit.WaitForOpenItemsAsync(_browser)).Single();

        Assert.Equal(AttentionKind.Question, item.Kind);
        Assert.Equal(runId, item.RunId);
        Assert.Equal("HUB-1", item.TicketKey);
        Assert.Equal("Which database?", item.Question!.Question);
        Assert.Equal("Choosing storage", item.Question.Context);
        Assert.Equal(["Postgres", "SQLite"], item.Question.Options);
        // Heartbeats keep the call alive while it waits.
        await WaitUntilAsync(() => progress.Count >= 2);
        Assert.Contains("Waiting for a human", progress.First().Message, StringComparison.Ordinal);
        Assert.False(ask.IsCompleted);

        await McpTestKit.AnswerAsync(_browser, item.Id, new { text = "Postgres" });

        Assert.Equal("Postgres", (await ask).Json().GetProperty("answer").GetString());
        var answered = await _browser.GetJsonAsync<AttentionItemInfo>($"/api/attention-items/{item.Id}");
        Assert.Equal(AttentionStatus.Answered, answered.Status);
        Assert.Equal("Postgres", answered.Answer!.Text);
        Assert.NotNull(answered.AnsweredAt);
        Assert.Empty(await _browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"));
    }

    [Fact]
    public async Task A_plan_is_approved_or_sent_back_with_notes()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);

        var first = agent.CallAsync("propose_plan", new { steps = new[] { "Add table", "Add API" }, risks = new[] { "Migration" }, files_expected = new[] { "src/A.cs" } });
        var item = (await McpTestKit.WaitForOpenItemsAsync(_browser)).Single();
        Assert.Equal(AttentionKind.PlanApproval, item.Kind);
        Assert.Equal(["Add table", "Add API"], item.Plan!.Steps);
        Assert.Equal(["src/A.cs"], item.Plan.FilesExpected);
        await McpTestKit.AnswerAsync(_browser, item.Id, new { decision = "revise", text = "Add tests first" });
        var revise = (await first).Json();
        Assert.Equal("revise", revise.GetProperty("decision").GetString());
        Assert.Equal("Add tests first", revise.GetProperty("notes").GetString());

        // The revised plan is a different request, so a new item.
        var second = agent.CallAsync("propose_plan", new { steps = new[] { "Add tests", "Add table", "Add API" } });
        var newItem = (await McpTestKit.WaitForOpenItemsAsync(_browser)).Single();
        Assert.NotEqual(item.Id, newItem.Id);
        await McpTestKit.AnswerAsync(_browser, newItem.Id, new { decision = "approve" });
        var approve = (await second).Json();
        Assert.Equal("approve", approve.GetProperty("decision").GetString());
        // Null fields are left out (the SDK's JSON defaults).
        Assert.False(approve.TryGetProperty("notes", out _));
    }

    [Fact]
    public async Task An_answer_given_before_the_waiter_attached_is_returned_at_once()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);

        // The call drops (client timeout, network) while it waits; the item stays open.
        using (var drop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            var dropped = agent.CallAsync("ask_human", new { question = "Which port?" }, cancellationToken: drop.Token);
            await McpTestKit.WaitForOpenItemsAsync(_browser);
            await drop.CancelAsync();
            // The SDK client reports it as a cancellation or as the server's 499.
            await Assert.ThrowsAnyAsync<Exception>(() => dropped);
        }
        await WaitUntilAsync(() => Waiter.WaitingCount == 0);

        // The human answers while no call waits.
        var item = (await McpTestKit.WaitForOpenItemsAsync(_browser)).Single();
        await McpTestKit.AnswerAsync(_browser, item.Id, new { text = "8080" });

        // The agent asks again: same item, stored answer, no wait.
        var again = await agent.CallAsync("ask_human", new { question = "Which port?" });
        Assert.Equal("8080", again.Json().GetProperty("answer").GetString());
        Assert.Equal(1, await CountItemsAsync());
    }

    [Fact]
    public async Task The_waiter_completes_at_once_for_an_item_answered_before_it_attached()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);
        using var drop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var dropped = agent.CallAsync("ask_human", new { question = "Q" }, cancellationToken: drop.Token);
        var item = (await McpTestKit.WaitForOpenItemsAsync(_browser)).Single();
        await drop.CancelAsync();
        // The SDK client reports it as a cancellation or as the server's 499.
        await Assert.ThrowsAnyAsync<Exception>(() => dropped);
        await McpTestKit.AnswerAsync(_browser, item.Id, new { text = "A" });

        // No notification can arrive any more: only the read after registering can find the answer.
        var answer = await Waiter.WaitAsync(item.Id, McpTestKit.Timeout(5));
        Assert.Equal("A", answer.Text);
    }

    [Fact]
    public async Task A_question_asked_again_while_open_is_merged_and_both_calls_get_the_answer()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);

        // A dangling call (Claude Code's idle abort leaves the request open) and the retry, plus a few at once.
        var calls = Enumerable.Range(0, 4).Select(_ => agent.CallAsync("ask_human", new { question = "Same?", options = new[] { "yes", "no" } })).ToList();
        var item = (await McpTestKit.WaitForOpenItemsAsync(_browser)).Single();
        await WaitUntilAsync(() => Waiter.WaitingCount == 4);
        Assert.Equal(1, await CountItemsAsync());

        await McpTestKit.AnswerAsync(_browser, item.Id, new { text = "yes" });

        foreach (var call in calls)
        {
            Assert.Equal("yes", (await call).Json().GetProperty("answer").GetString());
        }
        // Asked once more after the answer was handed out: the same answer again, still one item.
        Assert.Equal("yes", (await agent.CallAsync("ask_human", new { question = "Same?", options = new[] { "yes", "no" } })).Json().GetProperty("answer").GetString());
        Assert.Equal(1, await CountItemsAsync());
        await WaitUntilAsync(() => Waiter.WaitingCount == 0);
    }

    [Fact]
    public async Task Only_the_runs_latest_item_is_reused()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);

        foreach (var (question, answer) in new[] { ("First?", "1"), ("Second?", "2") })
        {
            var call = agent.CallAsync("ask_human", new { question });
            var item = (await McpTestKit.WaitForOpenItemsAsync(_browser)).Single();
            await McpTestKit.AnswerAsync(_browser, item.Id, new { text = answer });
            await call;
        }
        // "First?" again, after another question: a new question, not the old answer.
        var again = agent.CallAsync("ask_human", new { question = "First?" });
        var open = (await McpTestKit.WaitForOpenItemsAsync(_browser)).Single();
        Assert.Equal("First?", open.Question!.Question);
        Assert.Equal(3, await CountItemsAsync());
        await McpTestKit.AnswerAsync(_browser, open.Id, new { text = "1 again" });
        Assert.Equal("1 again", (await again).Json().GetProperty("answer").GetString());
    }

    [Fact]
    public async Task After_a_hub_restart_the_question_asked_again_gets_the_stored_answer()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        Guid itemId;
        await using (var agent = await McpTestKit.ConnectAsync(_factory, token))
        {
            var call = agent.CallAsync("propose_plan", new { steps = new[] { "One step" } });
            itemId = (await McpTestKit.WaitForOpenItemsAsync(_browser)).Single().Id;
            // The hub goes down: the call is cut, everything in memory is gone.
            await _factory.DisposeAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => call);
        }

        _factory = _factory.Restart();
        _restarted = true;
        _browser.Dispose();
        _browser = new BrowserClient(_factory.CreateClient(new() { HandleCookies = true }));
        await _browser.RefreshAntiforgeryAsync();
        (await _browser.LoginAsync("ada@example.org")).EnsureSuccessStatusCode();
        await _browser.RefreshAntiforgeryAsync();
        Assert.Equal(itemId, (await McpTestKit.WaitForOpenItemsAsync(_browser)).Single().Id);
        await McpTestKit.AnswerAsync(_browser, itemId, new { decision = "approve" });

        await using var resumed = await McpTestKit.ConnectAsync(_factory, token);
        var result = await resumed.CallAsync("propose_plan", new { steps = new[] { "One step" } });
        Assert.Equal("approve", result.Json().GetProperty("decision").GetString());
        Assert.Equal(1, await CountItemsAsync());
    }

    [Fact]
    public async Task A_missed_notification_is_caught_by_the_heartbeat()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);
        var call = agent.CallAsync("ask_human", new { question = "Quiet?" });
        var item = (await McpTestKit.WaitForOpenItemsAsync(_browser)).Single();

        // Answered without pg_notify, as if the notification was lost.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
            var answer = AttentionJson.Serialize(new AttentionAnswer("heard", null));
            await db.AttentionItems.Where(i => i.Id == item.Id).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, AttentionStatus.Answered)
                .SetProperty(i => i.Answer, answer)
                .SetProperty(i => i.AnsweredAt, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        }

        Assert.Equal("heard", (await call).Json().GetProperty("answer").GetString());
    }

    [Fact]
    public async Task Many_waits_on_one_item_complete_and_cancelled_waits_leave_nothing_behind()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);
        await using var agent = await McpTestKit.ConnectAsync(_factory, token);
        using var drop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var dropped = agent.CallAsync("ask_human", new { question = "Q" }, cancellationToken: drop.Token);
        var item = (await McpTestKit.WaitForOpenItemsAsync(_browser)).Single();
        await drop.CancelAsync();
        // The SDK client reports it as a cancellation or as the server's 499.
        await Assert.ThrowsAnyAsync<Exception>(() => dropped);
        await WaitUntilAsync(() => Waiter.WaitingCount == 0);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cancelled = Enumerable.Range(0, 20).Select(_ => Waiter.WaitAsync(item.Id, cancel.Token)).ToList();
        var waits = Enumerable.Range(0, 50).Select(_ => Waiter.WaitAsync(item.Id, McpTestKit.Timeout())).ToList();
        await WaitUntilAsync(() => Waiter.WaitingCount == 70);
        await cancel.CancelAsync();
        foreach (var wait in cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        }
        Assert.Equal(50, Waiter.WaitingCount);

        await McpTestKit.AnswerAsync(_browser, item.Id, new { text = "all" });

        Assert.All(await Task.WhenAll(waits), a => Assert.Equal("all", a.Text));
        Assert.Equal(0, Waiter.WaitingCount);
    }

    private AttentionWaiter Waiter => _factory.Services.GetRequiredService<AttentionWaiter>();

    private async Task<int> CountItemsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TikaiDbContext>().AttentionItems.CountAsync(TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var cancellationToken = McpTestKit.Timeout(15);
        while (!condition())
        {
            await Task.Delay(20, cancellationToken);
        }
    }

    /// <summary>Calls back on the reporting thread (Progress&lt;T&gt; would post to a sync context).</summary>
    private sealed class SynchronousProgress(Action<ProgressNotificationValue> report) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) => report(value);
    }
}
