using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Tikai.Core.Agents;
using Tikai.Core.Events;
using Tikai.Core.Reviews;
using Tikai.Core.Runs;
using Tikai.Data;
using Tikai.Data.Runs;
using Tikai.IntegrationTests.Auth;
using Tikai.IntegrationTests.Mcp;

namespace Tikai.IntegrationTests.Live;

public sealed class LiveHubTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(20);

    private TikaiApiFactory _factory = null!;
    private BrowserClient _browser = null!;
    private readonly System.Net.CookieContainer _cookies = new();
    private readonly List<HubConnection> _connections = [];
    private ModelContextProtocol.Client.McpClient? _agent;

    public async ValueTask InitializeAsync()
    {
        _factory = await TikaiApiFactory.CreateAsync(postgres);
        _browser = new BrowserClient(_factory.CreateDefaultClient(new CookieContainerHandler(_cookies)));
        await _browser.RefreshAntiforgeryAsync();
        await _browser.SignUpAdminAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }
        if (_agent is not null)
        {
            await _agent.DisposeAsync();
        }
        _browser.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Only_a_login_cookie_from_the_same_origin_opens_the_hub()
    {
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        var (_, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);

        Assert.Equal(HttpStatusCode.OK, await NegotiateAsync(_browser.Http, origin: null));
        Assert.Equal(HttpStatusCode.OK, await NegotiateAsync(_browser.Http, origin: "http://localhost"));
        Assert.Equal(HttpStatusCode.Forbidden, await NegotiateAsync(_browser.Http, origin: "http://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, await NegotiateAsync(_browser.Http, origin: "http://localhost:5173"));
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await NegotiateAsync(anonymous, origin: null));
        Assert.Equal(HttpStatusCode.Unauthorized, await NegotiateAsync(anonymous, origin: null, bearer: token));

        await using var connection = Connect();
        await connection.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HubConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task Changes_reach_those_who_watch_and_log_events_only_those_who_watch_the_run()
    {
        var ct = TestContext.Current.CancellationToken;
        var ticket = await McpTestKit.CreateTicketAsync(_browser, "hub", "HUB", "Ticket");
        await McpTestKit.CreateTicketAsync(_browser, "other", "OT", "Other");
        var (runId, token) = await McpTestKit.StartRunAsync(_factory, ticket.Id);

        var all = await ListenAsync(c => c.InvokeAsync("WatchAll", ct));
        var project = await ListenAsync(async c => Assert.True(await c.InvokeAsync<bool>("WatchProject", "hub", ct)));
        var otherProject = await ListenAsync(async c => Assert.True(await c.InvokeAsync<bool>("WatchProject", "other", ct)));
        var run = await ListenAsync(async c => Assert.True(await c.InvokeAsync<bool>("WatchRun", runId, ct)));
        await using (var probe = Connect())
        {
            await probe.StartAsync(ct);
            Assert.False(await probe.InvokeAsync<bool>("WatchProject", "nope", ct));
            Assert.False(await probe.InvokeAsync<bool>("WatchRun", Guid.NewGuid(), ct));
        }

        // An agent asks: the item is raised and the run blocked (it was queued here, so only the item changes).
        _agent = await McpTestKit.ConnectAsync(_factory, token);
        _ = _agent.CallAsync("ask_human", new { question = "Q?" });
        var item = Assert.Single(await McpTestKit.WaitForOpenItemsAsync(_browser));

        foreach (var listener in new[] { all, project })
        {
            var message = await listener.NextAsync("AttentionChanged");
            Assert.Equal(item.Id.ToString(), message["itemId"]!.GetValue<string>());
            Assert.Equal("hub", message["projectSlug"]!.GetValue<string>());
            Assert.Equal(1, message["ticketNumber"]!.GetValue<int>());
        }

        await AppendEventsAsync(runId);
        var events = await run.NextAsync("RunEvents");
        Assert.Equal(runId.ToString(), events["runId"]!.GetValue<string>());

        await PublishRunChangedAsync(runId);
        Assert.Equal(runId.ToString(), (await all.NextAsync("RunChanged"))["runId"]!.GetValue<string>());
        Assert.Equal(runId.ToString(), (await project.NextAsync("RunChanged"))["runId"]!.GetValue<string>());

        // A submission's change set (a new round, a verdict).
        var changeSetId = await AddChangeSetAsync(runId, ticket.Id);
        foreach (var listener in new[] { all, project })
        {
            var message = await listener.NextAsync("ChangeSetChanged");
            Assert.Equal(changeSetId.ToString(), message["changeSetId"]!.GetValue<string>());
            Assert.Equal(("hub", 1), (message["projectSlug"]!.GetValue<string>(), message["ticketNumber"]!.GetValue<int>()));
        }

        // Nothing for the other project, and no log events for those who do not watch the run.
        await Task.Delay(500, ct);
        Assert.Empty(otherProject.Received);
        Assert.DoesNotContain(all.Received, m => m.Method == "RunEvents");
        Assert.DoesNotContain(project.Received, m => m.Method == "RunEvents");
        Assert.DoesNotContain(run.Received, m => m.Method is "RunChanged" or "AttentionChanged" or "ChangeSetChanged");

        await run.Connection.InvokeAsync("UnwatchRun", runId, ct);
        await AppendEventsAsync(runId);
        await PublishRunChangedAsync(runId);
        await all.NextAsync("RunChanged");
        await Task.Delay(500, ct);
        Assert.Single(run.Received, m => m.Method == "RunEvents");
    }

    [Fact]
    public async Task After_the_hub_lost_its_database_listener_every_client_is_told_to_reread()
    {
        var listener = await ListenAsync(_ => Task.CompletedTask);

        // Kill the hub's listener connection from the server side, like a database restart would (once it listens).
        await using (var db = postgres.CreateContext())
        {
            var database = new NpgsqlConnectionStringBuilder(_factory.ConnectionString).Database;
            using var timeout = new CancellationTokenSource(_timeout);
            while (!(await db.Database.SqlQuery<bool>(
                $"""SELECT pg_terminate_backend(pid) AS "Value" FROM pg_stat_activity WHERE datname = {database} AND query LIKE 'LISTEN %'""")
                .ToListAsync(TestContext.Current.CancellationToken)).Contains(true))
            {
                await Task.Delay(100, timeout.Token);
            }
        }

        await listener.NextAsync("Resync");
    }

    private HubConnection Connect()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/live"), options =>
            {
                // The test server has no real sockets; long polling goes through its handler with the login cookie.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => new CookieContainerHandler(_cookies) { InnerHandler = _factory.Server.CreateHandler() };
            })
            .Build();
        _connections.Add(connection);
        return connection;
    }

    private async Task<Listener> ListenAsync(Func<HubConnection, Task> watch)
    {
        var connection = Connect();
        var listener = new Listener(connection);
        foreach (var method in new[] { "RunChanged", "AttentionChanged", "ChangeSetChanged", "RunEvents" })
        {
            connection.On<JsonObject>(method, message => listener.Add(method, message));
        }
        connection.On("Resync", () => listener.Add("Resync", new JsonObject()));
        await connection.StartAsync(TestContext.Current.CancellationToken);
        await watch(connection);
        return listener;
    }

    private async Task AppendEventsAsync(Guid runId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await db.AppendAsync(runId, [new ParsedEvent(RunEventKind.Log, new JsonObject { ["source"] = "hub", ["text"] = "hello" })], null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        await scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(RunEvent.Topic, runId, TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A change set row as a submission writes it (the git part is in ReviewTests), and its event.</summary>
    private async Task<Guid> AddChangeSetAsync(Guid runId, Guid ticketId)
    {
        (await _browser.PostAsync("/api/projects/hub/repositories", new { name = "app" })).EnsureSuccessStatusCode();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
        var repositoryId = await db.Repositories.Where(r => r.Name == "app").Select(r => r.Id).SingleAsync(TestContext.Current.CancellationToken);
        var changeSet = new ChangeSet
        {
            Id = Guid.CreateVersion7(),
            TicketId = ticketId,
            RunId = runId,
            RepositoryId = repositoryId,
            BaseSha = new string('a', 40),
            HeadSha = new string('b', 40),
            Status = ChangeSetStatus.Open,
        };
        db.ChangeSets.Add(changeSet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(ChangeSet.Topic, changeSet.Id, TestContext.Current.CancellationToken);
        return changeSet.Id;
    }

    private async Task PublishRunChangedAsync(Guid runId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(AgentRun.Topic, runId, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpStatusCode> NegotiateAsync(HttpClient http, string? origin, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/hubs/live/negotiate?negotiateVersion=1");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }
        if (bearer is not null)
        {
            request.Headers.Add("Authorization", $"Bearer {bearer}");
        }
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private sealed class Listener(HubConnection connection)
    {
        private readonly Channel<(string Method, JsonObject Message)> _channel = Channel.CreateUnbounded<(string, JsonObject)>();

        public HubConnection Connection { get; } = connection;

        public List<(string Method, JsonObject Message)> Received { get; } = [];

        public void Add(string method, JsonObject message)
        {
            lock (Received)
            {
                Received.Add((method, message));
            }
            _channel.Writer.TryWrite((method, message));
        }

        /// <summary>The next message of that kind; others in between are skipped (they stay in <see cref="Received"/>).</summary>
        public async Task<JsonObject> NextAsync(string method)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(_timeout);
            while (true)
            {
                var (received, message) = await _channel.Reader.ReadAsync(timeout.Token);
                if (received == method)
                {
                    return message;
                }
            }
        }
    }
}
