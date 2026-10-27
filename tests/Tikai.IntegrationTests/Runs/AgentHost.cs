using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tikai.Api.Runs;
using Tikai.Api.Tickets;
using Tikai.Core.Runs;
using Tikai.Data;
using Tikai.IntegrationTests.Auth;
using Tikai.Orchestrator;

namespace Tikai.IntegrationTests.Runs;

/// <summary>
/// Builds the agent base and stub images once and gives every test a hub on a real port, so agent containers reach
/// its MCP endpoint. Defaults fit Docker Desktop and a Linux host (host.docker.internal); when the tests themselves run
/// in a container, TIKAI_TEST_AGENT_NETWORK and TIKAI_TEST_HUB_HOST name a network both share and this container's name.
/// </summary>
public sealed class AgentHost : IAsyncLifetime
{
    public const string BaseImage = "tikai/agent-base:test";
    public const string StubImage = "tikai/agent-stub:test";

    public string RunsVolume { get; } = "tikai-test-runs-" + Guid.NewGuid().ToString("N")[..12];

    public IDockerClient Docker { get; } = new DockerClientBuilder().WithEndpoint(new DockerOptions().Endpoint).Build();

    private static string? NetworkOverride => Environment.GetEnvironmentVariable("TIKAI_TEST_AGENT_NETWORK");

    private static string HubHost => Environment.GetEnvironmentVariable("TIKAI_TEST_HUB_HOST") ?? "host.docker.internal";

    public async ValueTask InitializeAsync()
    {
        var root = RepoRoot();
        await BuildAsync(Path.Combine(root, "images", "agent-base"), BaseImage, null);
        await BuildAsync(Path.Combine(root, "tests", "agent-stub"), StubImage, BaseImage);
    }

    public async ValueTask DisposeAsync()
    {
        // Agent containers of failed tests, then the volume with all run directories.
        var leftovers = await Docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>> { ["volume"] = new Dictionary<string, bool> { [RunsVolume] = true } },
        });
        foreach (var container in leftovers)
        {
            await Docker.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true });
        }
        try
        {
            await Docker.Volumes.RemoveAsync(RunsVolume, force: true);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
        Docker.Dispose();
    }

    /// <summary>A hub listening on a free port, with the stub as agent image.</summary>
    public async Task<AgentHub> StartHubAsync(PostgresFixture postgres, IReadOnlyDictionary<string, string>? settings = null)
    {
        var port = FreePort();
        var all = Settings(port);
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            all[key] = value;
        }
        var factory = await TikaiApiFactory.CreateAsync(postgres, all);
        return await AgentHub.StartAsync(factory, port);
    }

    public Dictionary<string, string> Settings(int port)
    {
        var settings = new Dictionary<string, string>
        {
            ["Tikai:Agents:RunsVolume"] = RunsVolume,
            ["Tikai:Agents:Network"] = NetworkOverride ?? "bridge",
            ["Tikai:Agents:McpUrl"] = McpUrl(port),
            ["Tikai:Agents:ClaudeCodeImage"] = StubImage,
            ["Tikai:Agents:InitTimeout"] = "00:00:30",
            ["Tikai:Agents:MemoryBytes"] = (512L * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Tikai:Agents:ReconnectDelay"] = "00:00:01",
            ["Tikai:Agents:ReconcileInterval"] = "00:00:01",
            ["Tikai:Mcp:HeartbeatInterval"] = "00:00:00.500",
            [TikaiApiFactory.ReconcilerSetting] = "on",
        };
        if (NetworkOverride is null)
        {
            // Docker Desktop knows the name already; on Linux it needs the gateway mapping.
            settings["Tikai:Agents:ExtraHosts:0"] = "host.docker.internal:host-gateway";
        }
        return settings;
    }

    /// <summary>The hub's MCP endpoint as agent containers reach it.</summary>
    public static string McpUrl(int port) => $"http://{HubHost}:{port}/mcp";

    /// <summary>Listens on all interfaces when agents reach the hub by container name, otherwise on loopback.</summary>
    internal static bool ListenOnAllInterfaces => NetworkOverride is not null || !OperatingSystem.IsWindows();

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task BuildAsync(string directory, string name, string? baseImage)
    {
        var builder = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(directory)
            .WithDockerfile("Dockerfile")
            .WithName(name)
            .WithCleanUp(false)
            .WithDeleteIfExists(false);
        if (baseImage is not null)
        {
            builder = builder.WithBuildArgument("BASE_IMAGE", baseImage);
        }
        await builder.Build().CreateAsync();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tikai.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Tikai.slnx not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>One hub for a test, with a logged-in browser and helpers for a project that can run agents.</summary>
public sealed class AgentHub : IAsyncDisposable
{
    private AgentHub(TikaiApiFactory factory, int port, BrowserClient browser)
    {
        Factory = factory;
        Port = port;
        Browser = browser;
    }

    public TikaiApiFactory Factory { get; }

    public int Port { get; }

    public BrowserClient Browser { get; }

    public const string ApiKey = "test-provider-key-0123456789";

    internal static async Task<AgentHub> StartAsync(TikaiApiFactory factory, int port, bool signUp = true)
    {
        factory.UseKestrel(options =>
        {
            if (AgentHost.ListenOnAllInterfaces)
            {
                options.ListenAnyIP(port);
            }
            else
            {
                options.ListenLocalhost(port);
            }
        });
        factory.StartServer();
        var browser = new BrowserClient(factory.CreateClient(new() { HandleCookies = true, BaseAddress = new Uri($"http://localhost:{port}") }));
        await browser.RefreshAntiforgeryAsync();
        if (signUp)
        {
            await browser.SignUpAdminAsync();
        }
        else
        {
            (await browser.LoginAsync("ada@example.org")).EnsureSuccessStatusCode();
            await browser.RefreshAntiforgeryAsync();
        }
        return new AgentHub(factory, port, browser);
    }

    /// <summary>The same database and port with a new hub process, like a restart; logs in again.</summary>
    public async Task<AgentHub> RestartAsync() => await StartAgainAsync(await StopAsync(), Port);

    /// <summary>Stops this hub (the agents keep running) and returns the factory for the next one.</summary>
    public async Task<TikaiApiFactory> StopAsync()
    {
        var next = Factory.Restart();
        await DisposeAsync();
        return next;
    }

    /// <summary>A hub from <see cref="StopAsync"/>'s factory on the same port.</summary>
    public static Task<AgentHub> StartAgainAsync(TikaiApiFactory next, int port) => StartAsync(next, port, signUp: false);

    /// <summary>Answers a question through the API, as a person would.</summary>
    public async Task AnswerAsync(Guid itemId, string text)
    {
        using var response = await Browser.PostAsync($"/api/attention-items/{itemId}/answer", new { text });
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>The texts of the run's assistant messages, oldest first.</summary>
    public async Task<List<string>> MessagesAsync(Guid runId) =>
        [.. (await EventsAsync(runId))
            .Where(e => e.Kind == RunEventKind.Message && e.Payload.TryGetProperty("text", out _))
            .Select(e => e.Payload.GetProperty("text").GetString()!)];

    /// <summary>A project with a local repository, a provider and one ticket whose description picks the stub's scenario.</summary>
    public async Task<TicketInfo> CreateTicketAsync(string scenario, string key = "HUB")
    {
        var slug = key.ToLowerInvariant() + "-" + Guid.NewGuid().ToString("N")[..6];
        (await Browser.PostAsync("/api/projects", new { name = slug, slug, key })).EnsureSuccessStatusCode();
        (await Browser.PostAsync($"/api/projects/{slug}/repositories", new { name = "app" })).EnsureSuccessStatusCode();
        (await Browser.PostAsync($"/api/projects/{slug}/llm-providers", new { name = "Team key", defaultModel = "claude-haiku-5-5", apiKey = ApiKey }))
            .EnsureSuccessStatusCode();
        using var created = await Browser.PostAsync($"/api/projects/{slug}/tickets", new
        {
            title = "Say hello",
            description = $"Add hello.txt. stub:{scenario}",
            criteria = new[] { "hello.txt exists", "It is committed" },
        });
        created.EnsureSuccessStatusCode();
        var ticket = (await created.Content.ReadFromJsonAsync<TicketInfo>(TikaiJson.Options))!;
        Slugs[ticket.Id] = slug;
        return ticket;
    }

    public Dictionary<Guid, string> Slugs { get; } = [];

    /// <summary>The hub's bare repository of the ticket's project, e.g. to put files on its default branch.</summary>
    public async Task<string> MirrorAsync(TicketInfo ticket)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
        var projectId = await db.Tickets.Where(t => t.Id == ticket.Id).Select(t => t.ProjectId).SingleAsync();
        var repositoryId = await db.Repositories.Where(r => r.ProjectId == projectId).Select(r => r.Id).SingleAsync();
        return scope.ServiceProvider.GetRequiredService<Tikai.Api.Projects.MirrorPaths>().For(projectId, repositoryId);
    }

    public async Task<RunInfo> AssignAsync(TicketInfo ticket, object? body = null)
    {
        using var response = await Browser.PostAsync($"/api/projects/{Slugs[ticket.Id]}/tickets/{ticket.Number}/runs", body ?? new { });
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RunInfo>(TikaiJson.Options))!;
    }

    public Task<RunInfo> GetRunAsync(Guid runId) => Browser.GetJsonAsync<RunInfo>($"/api/runs/{runId}");

    public async Task<RunInfo> WaitForAsync(Guid runId, Func<RunInfo, bool> condition, int seconds = 120)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        RunInfo run;
        do
        {
            run = await GetRunAsync(runId);
            if (condition(run))
            {
                return run;
            }
            try
            {
                await Task.Delay(250, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                var events = await EventsAsync(runId);
                Assert.Fail($"Run is {run.State} after {seconds} s ({run.FailureReason}). Events:\n{string.Join("\n", events.Select(e => $"{e.Seq} {e.Kind} {e.Payload}"))}");
            }
        }
        while (true);
    }

    /// <summary>
    /// Until the run is past running and its container is gone: a run moves to in_review during its submit call, and
    /// its container's end (exit code, cost) comes a moment later.
    /// </summary>
    public async Task<RunInfo> WaitForEndAsync(Guid runId, int seconds = 120)
    {
        var run = await WaitForAsync(runId, r => r.State is not (AgentRunState.Queued or AgentRunState.Preparing or AgentRunState.Running), seconds);
        if (run.State != AgentRunState.InReview)
        {
            return run;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        while (await ContainerIdAsync(runId) is not null)
        {
            await Task.Delay(250, timeout.Token);
        }
        return await GetRunAsync(runId);
    }

    private async Task<string?> ContainerIdAsync(Guid runId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
        return await db.AgentRuns.Where(r => r.Id == runId).Select(r => r.ContainerId).SingleAsync();
    }

    public Task<List<RunEventInfo>> EventsAsync(Guid runId, long afterSeq = 0) =>
        Browser.GetJsonAsync<List<RunEventInfo>>($"/api/runs/{runId}/events?afterSeq={afterSeq}&limit=1000");

    /// <summary>What the stub saw from inside its container.</summary>
    public async Task<JsonElement> StubFactsAsync(Guid runId)
    {
        var text = (await EventsAsync(runId))
            .Where(e => e.Kind == RunEventKind.Message && e.Payload.TryGetProperty("text", out _))
            .Select(e => e.Payload.GetProperty("text").GetString()!)
            .Single(t => t.StartsWith("STUB_FACTS ", StringComparison.Ordinal));
        return JsonDocument.Parse(text["STUB_FACTS ".Length..]).RootElement;
    }

    public async ValueTask DisposeAsync()
    {
        Browser.Dispose();
        await Factory.DisposeAsync();
    }
}
