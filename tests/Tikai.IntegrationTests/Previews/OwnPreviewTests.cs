using System.Net;
using Docker.DotNet;
using Microsoft.Extensions.Options;
using Tikai.Adapters.ClaudeCode;
using Tikai.IntegrationTests.Git;
using Tikai.IntegrationTests.Runs;
using Tikai.Orchestrator;
using Tikai.Orchestrator.Previews;

namespace Tikai.IntegrationTests.Previews;

/// <summary>
/// Tikai previews itself from this repository's committed HEAD and its tikai-preview.yml: web build, API publish, the
/// hub with Postgres and without Docker. Opt-in (TIKAI_TEST_OWN_PREVIEW=1): it pulls node, the .NET SDK and postgres
/// and builds the whole hub, which takes minutes.
/// </summary>
public sealed class OwnPreviewTests(AgentHost agents) : IClassFixture<AgentHost>, IAsyncDisposable
{
    private readonly string _volume = "tikai-test-own-preview-" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _temp = Directory.CreateTempSubdirectory("tikai-own-preview-").FullName;
    private readonly Guid _previewId = Guid.NewGuid();
    private DockerContainerRuntime? _runtime;

    [Fact]
    public async Task Tikai_starts_as_a_preview_of_itself_with_its_database()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIKAI_TEST_OWN_PREVIEW") == "1",
            "Set TIKAI_TEST_OWN_PREVIEW=1 to build and start Tikai as a preview (several minutes).");
        var ct = TestContext.Current.CancellationToken;
        var root = RepoRoot();
        var definition = PreviewDefinition.Parse(await File.ReadAllTextAsync(Path.Combine(root, PreviewDefinition.FileName), ct)).Definition!;

        // The committed HEAD as a round ref of a mirror, bundled like the hub does it.
        var mirror = Path.Combine(_temp, "mirror.git");
        await TestGit.RunAsync(null, "clone", "--quiet", "--bare", "--no-local", root, mirror);
        await TestGit.RunAsync(mirror, "update-ref", "refs/tikai/rounds/own/1", "HEAD");
        var bundle = Path.Combine(_temp, "round.bundle");
        await TestGit.CreateService().CreateRefBundleAsync(mirror, "refs/tikai/rounds/own/1", bundle, ct);

        var agentOptions = new OrchestratorOptions { Network = "bridge" };
        var previewOptions = new PreviewOptions { PreviewsVolume = _volume, Upstream = PreviewUpstream.Loopback };
        _runtime = new DockerContainerRuntime(agents.Docker, Options.Create(agentOptions), Options.Create(previewOptions));
        var adapter = new ClaudeCodeAdapter(Options.Create(new ClaudeCodeOptions { ClaudeCodeImage = AgentHost.StubImage }));
        var environment = new PreviewEnvironment(_runtime, adapter, Options.Create(previewOptions), Options.Create(agentOptions), TimeProvider.System);
        var log = new List<PreviewLogEntry>();
        Task Sink(IReadOnlyList<PreviewLogEntry> lines, CancellationToken _)
        {
            lock (log)
            {
                log.AddRange(lines);
            }
            return Task.CompletedTask;
        }

        try
        {
            await environment.WriteSourceAsync(_previewId, bundle, ct);
            await environment.CloneAsync(_previewId, "refs/tikai/rounds/own/1", Sink, ct);
            for (var i = 0; i < definition.Setup.Count; i++)
            {
                await environment.RunSetupAsync(_previewId, i + 1, definition.Setup[i], Sink, ct);
            }
            var started = await environment.StartAsync(_previewId, definition, Sink, ct);

            using var http = new HttpClient { BaseAddress = new Uri(Assert.Single(started.Apps).Url) };
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(new Uri("/health/ready", UriKind.Relative), ct)).StatusCode);
            Assert.Contains("<div id=\"root\">", await http.GetStringAsync(new Uri("/", UriKind.Relative), ct), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (log)
            {
                Assert.Fail($"{ex.Message}\nLog:\n{string.Join("\n", log.TakeLast(80).Select(l => $"[{l.Source}] {l.Text}"))}");
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "Tikai.slnx")))
        {
            dir = dir.Parent!;
        }
        return dir.FullName;
    }

    public async ValueTask DisposeAsync()
    {
        if (_runtime is not null)
        {
            await _runtime.RemovePreviewAsync(_previewId, CancellationToken.None);
            try
            {
                await agents.Docker.Volumes.RemoveAsync(_volume, force: true);
            }
            catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }
        foreach (var file in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_temp, recursive: true);
    }
}
