using System.Net;
using System.Text.Json;
using Docker.DotNet;
using Microsoft.Extensions.Options;
using Tikai.Adapters.ClaudeCode;
using Tikai.IntegrationTests.Git;
using Tikai.IntegrationTests.Runs;
using Tikai.Orchestrator;
using Tikai.Orchestrator.Previews;

namespace Tikai.IntegrationTests.Previews;

/// <summary>
/// The Docker side of a preview with real containers: a round cloned from a bundle, a setup step, a service with a
/// health check and an app, all from the agent base image (node), then stop and cleanup.
/// </summary>
public sealed class PreviewEnvironmentTests(AgentHost agents) : IClassFixture<AgentHost>, IAsyncDisposable
{
    private const string RoundRef = "refs/tikai/rounds/0b4c2a3e-0000-0000-0000-000000000001/1";

    // The app reports what it sees from inside its container.
    private const string Server = """
        const http = require('http'); const fs = require('fs');
        http.createServer(async (req, res) => {
          if (req.url === '/health') { res.end('ok'); return; }
          let rootWritable = true; try { fs.writeFileSync('/probe', 'x'); } catch { rootWritable = false; }
          const service = await (await fetch(process.env.SERVICE_URL)).text();
          res.setHeader('content-type', 'application/json');
          res.end(JSON.stringify({
            greeting: fs.readFileSync('/preview/out/greeting.txt', 'utf8').trim(),
            uid: process.getuid(), service, secretLength: (process.env.SECRET_X || '').length,
            rootWritable, cwd: process.cwd(), hasSource: fs.existsSync('/preview/src/server.js'),
          }));
        }).listen(8000);
        console.log('listening on 8000 with ' + process.env.SECRET_X);
        """;

    private readonly string _volume = "tikai-test-previews-" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _temp = Directory.CreateTempSubdirectory("tikai-preview-").FullName;
    private readonly List<Guid> _previews = [];

    private static string? NetworkOverride => Environment.GetEnvironmentVariable("TIKAI_TEST_AGENT_NETWORK");

    [Fact]
    public async Task A_round_is_cloned_set_up_and_started_with_its_service_then_stopped_and_cleaned_up()
    {
        var (environment, runtime) = Create();
        var previewId = NewPreview();
        var log = new List<PreviewLogLine>();
        Task Sink(IReadOnlyList<PreviewLogLine> lines, CancellationToken _)
        {
            lock (log)
            {
                log.AddRange(lines);
            }
            return Task.CompletedTask;
        }
        var ct = TestContext.Current.CancellationToken;

        await environment.WriteSourceAsync(previewId, await BundleAsync(), ct);
        await environment.CloneAsync(previewId, RoundRef, Sink, ct);
        await environment.RunSetupAsync(previewId, 1, new PreviewSetupStep(AgentHost.BaseImage,
            "mkdir -p /preview/out && echo 'hello from setup' > /preview/out/greeting.txt && echo setup-done"), Sink, ct);
        var definition = new PreviewDefinition(
            [],
            new PreviewApp(AgentHost.BaseImage, "node server.js", 8000,
                new Dictionary<string, string> { ["SERVICE_URL"] = "http://echo:9000" }, ["SECRET_X"], "/health"),
            [new PreviewService("echo", AgentHost.BaseImage, new Dictionary<string, string>(),
                ["node", "-e", "require('http').createServer((q, s) => s.end('from-service')).listen(9000)"],
                "curl -fsS http://localhost:9000")],
            30);
        var started = await environment.StartAsync(previewId, definition, Sink, ct);

        // In network mode the test itself runs in a container on the agents network, which is not the preview's.
        if (NetworkOverride is null)
        {
            using var http = new HttpClient();
            var seen = JsonDocument.Parse(await http.GetStringAsync(started.Upstream, ct)).RootElement;
            Assert.Equal("hello from setup", seen.GetProperty("greeting").GetString());
            Assert.Equal(1000, seen.GetProperty("uid").GetInt32());
            Assert.Equal("from-service", seen.GetProperty("service").GetString());
            Assert.Equal(64, seen.GetProperty("secretLength").GetInt32());
            Assert.False(seen.GetProperty("rootWritable").GetBoolean());
            Assert.Equal("/preview/src", seen.GetProperty("cwd").GetString());
            Assert.True(seen.GetProperty("hasSource").GetBoolean());
        }

        // The app's log reaches the preview log with the generated value removed.
        using (var follow = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            follow.CancelAfter(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => environment.FollowAppLogAsync(started.AppContainerId, started.Secrets, Sink, follow.Token));
        }
        lock (log)
        {
            Assert.Contains(log, l => l.Source == "setup 1" && l.Text == "setup-done");
            Assert.Contains(log, l => l.Source == "hub" && l.Text.StartsWith("Source: ", StringComparison.Ordinal) && l.Text.Contains("Add the app", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Source == "app" && l.Text == "listening on 8000 with [redacted]");
            Assert.DoesNotContain(log, l => started.Secrets.Any(s => l.Text.Contains(s, StringComparison.Ordinal)));
        }

        var resources = (await runtime.ListPreviewsAsync(ct)).Where(r => r.PreviewId == previewId).ToList();
        Assert.Contains(resources, r => r.IsNetwork);
        Assert.Contains(resources, r => r is { Role: PreviewRole.App, Running: true });
        Assert.Contains(resources, r => r is { Role: PreviewRole.Service, Running: true });

        await environment.StopAsync(previewId, ct);
        Assert.DoesNotContain(await runtime.ListPreviewsAsync(ct), r => r.PreviewId == previewId);

        await environment.CleanupAsync(previewId, ct);
        var seed = await runtime.CreatePreviewSeedAsync(previewId, AgentHost.StubImage, ct);
        try
        {
            Assert.Null(await runtime.DownloadAsync(seed, $"/previews/{previewId:D}", ct));
        }
        finally
        {
            await runtime.RemoveAsync(seed, CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_failing_setup_step_stops_the_preview_with_its_exit_code()
    {
        var (environment, _) = Create();
        var previewId = NewPreview();
        var ct = TestContext.Current.CancellationToken;
        await environment.WriteSourceAsync(previewId, await BundleAsync(), ct);
        await environment.CloneAsync(previewId, RoundRef, (_, _) => Task.CompletedTask, ct);

        var error = await Assert.ThrowsAsync<PreviewStartException>(() => environment.RunSetupAsync(
            previewId, 2, new PreviewSetupStep(AgentHost.BaseImage, "echo broken; exit 3"), (_, _) => Task.CompletedTask, ct));

        Assert.Equal("Setup step 2 failed with exit code 3; see the preview log.", error.Message);
    }

    [Fact]
    public async Task An_app_that_exits_fails_the_start_with_its_log()
    {
        var (environment, _) = Create();
        var previewId = NewPreview();
        var log = new List<PreviewLogLine>();
        var ct = TestContext.Current.CancellationToken;
        await environment.WriteSourceAsync(previewId, await BundleAsync(), ct);
        await environment.CloneAsync(previewId, RoundRef, (_, _) => Task.CompletedTask, ct);

        var definition = new PreviewDefinition([],
            new PreviewApp(AgentHost.BaseImage, "echo cannot start; exit 7", 8000, new Dictionary<string, string>(), [], null), [], 30);
        var error = await Assert.ThrowsAsync<PreviewStartException>(() => environment.StartAsync(previewId, definition, (lines, _) =>
        {
            log.AddRange(lines);
            return Task.CompletedTask;
        }, ct));

        Assert.Equal("The app stopped with exit code 7.", error.Message);
        Assert.Contains(log, l => l.Source == "app" && l.Text == "cannot start");
    }

    private (PreviewEnvironment Environment, DockerContainerRuntime Runtime) Create()
    {
        var agentOptions = new OrchestratorOptions
        {
            Network = NetworkOverride ?? "bridge",
            MemoryBytes = 512L * 1024 * 1024,
        };
        var previewOptions = new PreviewOptions
        {
            PreviewsVolume = _volume,
            MemoryBytes = 512L * 1024 * 1024,
            StartTimeout = TimeSpan.FromSeconds(60),
            // Inside a container the tests reach previews like the hub does: by joining their network.
            Upstream = NetworkOverride is null ? PreviewUpstream.Loopback : PreviewUpstream.Network,
            HubContainer = NetworkOverride is null ? null : Environment.GetEnvironmentVariable("TIKAI_TEST_HUB_HOST"),
        };
        var runtime = new DockerContainerRuntime(agents.Docker, Options.Create(agentOptions), Options.Create(previewOptions));
        var adapter = new ClaudeCodeAdapter(Options.Create(new ClaudeCodeOptions { ClaudeCodeImage = AgentHost.StubImage }));
        return (new PreviewEnvironment(runtime, adapter, Options.Create(previewOptions), Options.Create(agentOptions), TimeProvider.System), runtime);
    }

    private Guid NewPreview()
    {
        var id = Guid.NewGuid();
        _previews.Add(id);
        return id;
    }

    /// <summary>A mirror with the round's pinned ref, bundled like the hub does it.</summary>
    private async Task<string> BundleAsync()
    {
        var mirror = await TestGit.InitBareAsync(Path.Combine(_temp, "mirror-" + Guid.NewGuid().ToString("N")[..6] + ".git"));
        var head = await TestGit.CommitAsync(mirror, "tikai/1-abc", "Add the app", ("server.js", Server), ("README.md", "app"));
        await TestGit.RunAsync(mirror, "update-ref", RoundRef, head);
        var bundle = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".bundle");
        Assert.Equal(head, await TestGit.CreateService().CreateRefBundleAsync(mirror, RoundRef, bundle, TestContext.Current.CancellationToken));
        return bundle;
    }

    public async ValueTask DisposeAsync()
    {
        var (_, runtime) = Create();
        foreach (var id in _previews)
        {
            await runtime.RemovePreviewAsync(id, CancellationToken.None);
        }
        foreach (var leftover in (await runtime.ListPreviewsAsync(CancellationToken.None)).Where(r => !r.IsNetwork))
        {
            await runtime.RemoveAsync(leftover.Id, CancellationToken.None);
        }
        try
        {
            await agents.Docker.Volumes.RemoveAsync(_volume, force: true);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
        // Git writes its objects read-only, which Windows refuses to delete.
        foreach (var file in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_temp, recursive: true);
    }
}
