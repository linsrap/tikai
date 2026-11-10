using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Tikai.Core.Agents;
using Tikai.Core.Runs;

namespace Tikai.Orchestrator.Previews;

/// <summary>One line of a preview's log.</summary>
/// <param name="Source">hub, setup 1, app, …</param>
public sealed record PreviewLogEntry(string Source, string Text);

/// <summary>Stores lines of a preview's log (already redacted).</summary>
public delegate Task PreviewLogSink(IReadOnlyList<PreviewLogEntry> lines, CancellationToken cancellationToken);

/// <summary>A preview could not be started, for a reason people can act on.</summary>
public sealed class PreviewStartException(string message) : Exception(message);

/// <summary>A started preview: where the hub reaches its app, and the values the hub generated (removed from its log).</summary>
public sealed record PreviewStarted(Uri Upstream, string AppContainerId, IReadOnlyList<string> Secrets)
{
    public override string ToString() => $"PreviewStarted {{ Upstream = {Upstream}, AppContainerId = {AppContainerId}, Secrets = {Secrets.Count} }}";
}

/// <summary>
/// The Docker side of a preview, built like an agent run: the round as a bundle written into the preview's directory
/// through a seed container, a prep container that clones it, the setup steps each in its own sandboxed container (with
/// internet), then the services and the app on the preview's own network, and finally removal and the cleanup container.
/// The hub never mounts the previews volume and never runs anything from the definition itself.
/// </summary>
public sealed class PreviewEnvironment(
    IContainerRuntime containers,
    IAgentAdapter adapter,
    IOptions<PreviewOptions> options,
    IOptions<OrchestratorOptions> agentOptions,
    TimeProvider timeProvider)
{
    public const string BundleFile = "prep/source.bundle";

    /// <summary>How long log lines are collected before they are stored together.</summary>
    private static readonly TimeSpan _logFlushInterval = TimeSpan.FromMilliseconds(500);

    private static readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private readonly PreviewOptions _options = options.Value;

    // Arguments come as positional parameters, never inside the script text.
    private const string CloneScript = """
        git init --quiet /preview/src
        cd /preview/src
        git fetch --quiet --no-tags /preview/prep/source.bundle "$1"
        git -c advice.detachedHead=false checkout --quiet --detach FETCH_HEAD
        git log --oneline -1
        """;

    /// <summary>The image the hub's own helpers use (prep, seed, cleanup): the agent image, which has git and the agent uid.</summary>
    public string HelperImage => adapter.Image;

    /// <summary>
    /// The preview's directory: agent-owned root, src/ and home/ (setup steps write their output anywhere below
    /// /preview), and the bundle in a root-owned prep/.
    /// </summary>
    public async Task WriteSourceAsync(Guid previewId, string bundleFile, CancellationToken cancellationToken)
    {
        if (!await containers.ImageExistsAsync(HelperImage, cancellationToken))
        {
            throw new PreviewStartException($"The agent image {HelperImage} is not on this Docker host; previews use it to clone the round. Build it with \"docker compose --profile agents build\".");
        }
        ArchiveEntry[] entries =
        [
            RunFile.Directory("", agentOwned: true, RunFile.Modes.Directory),
            RunFile.Directory("src", agentOwned: true, RunFile.Modes.Directory),
            RunFile.Directory("home", agentOwned: true, RunFile.Modes.Private),
            RunFile.Directory("prep", agentOwned: false, RunFile.Modes.Executable),
            new ArchiveEntry(new RunFile(BundleFile, null, AgentOwned: false, RunFile.Modes.ReadOnly), bundleFile),
        ];
        var seed = await containers.CreatePreviewSeedAsync(previewId, HelperImage, cancellationToken);
        try
        {
            await using var tar = await RunArchive.BuildAsync(previewId.ToString("D"), entries, agentOptions.Value.AgentUid, timeProvider.GetUtcNow(), cancellationToken);
            await containers.UploadAsync(seed, "/previews", tar, cancellationToken);
        }
        finally
        {
            await containers.RemoveAsync(seed, CancellationToken.None);
        }
    }

    /// <summary>Checks out <paramref name="reference"/> from the bundle into /preview/src (agent uid, no network).</summary>
    public async Task CloneAsync(Guid previewId, string reference, PreviewLogSink log, CancellationToken cancellationToken)
    {
        var prep = await containers.CreatePreviewAsync(new PreviewContainerSpec(
            previewId, PreviewRole.Prep, HelperImage,
            Entrypoint: ["/bin/sh", "-euc", CloneScript.Replace("\r\n", "\n", StringComparison.Ordinal)],
            Command: ["tikai-preview-prepare", reference],
            Environment: []), cancellationToken);
        try
        {
            var output = await HelperContainer.RunToEndAsync(containers, prep, agentOptions.Value.PrepareTimeout, cancellationToken)
                ?? throw new PreviewStartException($"Cloning the round took longer than {agentOptions.Value.PrepareTimeout}.");
            if (output.ExitCode != 0)
            {
                throw new PreviewStartException($"Cloning the round failed (exit code {output.ExitCode}): {output.Text}");
            }
            await log([new PreviewLogEntry("hub", "Source: " + output.Text)], cancellationToken);
        }
        finally
        {
            await containers.RemoveAsync(prep, CancellationToken.None);
        }
    }

    public async Task PullAsync(string image, PreviewLogSink log, CancellationToken cancellationToken)
    {
        if (await containers.ImageExistsAsync(image, cancellationToken))
        {
            return;
        }
        await log([new PreviewLogEntry("hub", $"Pulling {image}")], cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.PullTimeout);
        try
        {
            await containers.PullAsync(image, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PreviewStartException($"Pulling {image} took longer than {_options.PullTimeout}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PreviewStartException)
        {
            throw new PreviewStartException($"Pulling {image} failed: {ex.Message}");
        }
    }

    /// <summary>Runs one setup step to its end with its output in the log; a non-zero exit stops the preview.</summary>
    /// <param name="number">1-based, as people count the steps.</param>
    public async Task RunSetupAsync(Guid previewId, int number, PreviewSetupStep step, PreviewLogSink log, CancellationToken cancellationToken)
    {
        await PullAsync(step.Image, log, cancellationToken);
        var source = $"setup {number.ToString(CultureInfo.InvariantCulture)}";
        await log([new PreviewLogEntry("hub", $"Setup step {number}: {step.Run}")], cancellationToken);
        await containers.RemoveAsync(DockerContainerRuntime.PreviewName(PreviewRole.Setup, previewId), CancellationToken.None);
        var container = await containers.CreatePreviewAsync(new PreviewContainerSpec(
            previewId, PreviewRole.Setup, step.Image, Entrypoint: ["/bin/sh", "-c", step.Run], Command: [], Environment: []), cancellationToken);
        try
        {
            await containers.StartAsync(container, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.SetupTimeout);
            long exitCode;
            try
            {
                await PumpLogAsync(container, source, new SecretRedactor([]), log, timeout.Token);
                exitCode = await containers.WaitAsync(container, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PreviewStartException($"Setup step {number} took longer than {_options.SetupTimeout}.");
            }
            if (exitCode != 0)
            {
                throw new PreviewStartException($"Setup step {number} failed with exit code {exitCode}; see the preview log.");
            }
        }
        finally
        {
            await containers.RemoveAsync(container, CancellationToken.None);
        }
    }

    /// <summary>
    /// The preview's network, the services (started, then healthy), and the app; returns once the app answers on its
    /// health check path (any answer on / without one).
    /// </summary>
    public async Task<PreviewStarted> StartAsync(Guid previewId, PreviewDefinition definition, PreviewLogSink log, CancellationToken cancellationToken)
    {
        foreach (var image in definition.Services.Select(s => s.Image).Append(definition.App.Image).Distinct(StringComparer.Ordinal))
        {
            await PullAsync(image, log, cancellationToken);
        }
        await containers.CreatePreviewNetworkAsync(previewId, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.StartTimeout);
        var started = new List<(PreviewService Service, string Id)>();
        foreach (var service in definition.Services)
        {
            var id = await containers.CreatePreviewAsync(new PreviewContainerSpec(
                previewId, PreviewRole.Service, service.Image, Entrypoint: null, Command: service.Command,
                Environment: [.. service.Environment.Select(e => new ContainerEnv(e.Key, e.Value))],
                ServiceName: service.Name, HealthcheckCommand: service.HealthcheckCommand), cancellationToken);
            await containers.StartAsync(id, cancellationToken);
            started.Add((service, id));
            await log([new PreviewLogEntry("hub", $"Service {service.Name} started ({service.Image})")], cancellationToken);
        }
        try
        {
            foreach (var (service, id) in started)
            {
                await WaitForServiceAsync(service, id, log, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PreviewStartException($"The services were not healthy within {_options.StartTimeout}.");
        }

        var secrets = definition.App.Generate.ToDictionary(name => name, _ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), StringComparer.Ordinal);
        var app = definition.App;
        var appId = await containers.CreatePreviewAsync(new PreviewContainerSpec(
            previewId, PreviewRole.App, app.Image, Entrypoint: ["/bin/sh", "-c", app.Command], Command: [],
            Environment: [.. app.Environment.Select(e => new ContainerEnv(e.Key, e.Value)), .. secrets.Select(s => new ContainerEnv(s.Key, s.Value, IsSecret: true))],
            Port: app.Port), cancellationToken);
        await containers.StartAsync(appId, cancellationToken);
        await log([new PreviewLogEntry("hub", $"App started ({app.Image}): {app.Command}")], cancellationToken);

        Uri upstream;
        try
        {
            upstream = await UpstreamAsync(previewId, appId, app.Port, cancellationToken);
            await WaitForAppAsync(upstream, app.HealthcheckPath, appId, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await LogTailAsync(appId, "app", new SecretRedactor(secrets.Values), log);
            throw new PreviewStartException($"The app did not answer on port {app.Port}{app.HealthcheckPath} within {_options.StartTimeout}; see the preview log.");
        }
        catch (PreviewStartException)
        {
            await LogTailAsync(appId, "app", new SecretRedactor(secrets.Values), log);
            throw;
        }
        return new PreviewStarted(upstream, appId, [.. secrets.Values]);
    }

    /// <summary>Follows the app's log into the preview log until it ends or <paramref name="cancellationToken"/> stops it.</summary>
    public Task FollowAppLogAsync(string appContainerId, IReadOnlyList<string> secrets, PreviewLogSink log, CancellationToken cancellationToken) =>
        PumpLogAsync(appContainerId, "app", new SecretRedactor(secrets), log, cancellationToken);

    /// <summary>Removes the preview's containers and network.</summary>
    public Task StopAsync(Guid previewId, CancellationToken cancellationToken) => containers.RemovePreviewAsync(previewId, cancellationToken);

    /// <summary>Removes the preview's directory from the previews volume (after <see cref="StopAsync"/>).</summary>
    public async Task CleanupAsync(Guid previewId, CancellationToken cancellationToken)
    {
        if (!await containers.ImageExistsAsync(HelperImage, cancellationToken))
        {
            throw new InvalidOperationException($"The agent image {HelperImage} is missing; the preview's directory is removed once it is back.");
        }
        await containers.RemoveAsync(DockerContainerRuntime.PreviewName(PreviewRole.Cleanup, previewId), CancellationToken.None);
        var cleanup = await containers.CreatePreviewCleanupAsync(previewId, HelperImage, cancellationToken);
        try
        {
            var output = await HelperContainer.RunToEndAsync(containers, cleanup, agentOptions.Value.InspectTimeout, cancellationToken)
                ?? throw new InvalidOperationException($"Removing the preview's directory took longer than {agentOptions.Value.InspectTimeout}.");
            if (output.ExitCode != 0)
            {
                throw new InvalidOperationException($"Removing the preview's directory failed (exit code {output.ExitCode}): {output.Text}");
            }
        }
        finally
        {
            await containers.RemoveAsync(cleanup, CancellationToken.None);
        }
    }

    private async Task<Uri> UpstreamAsync(Guid previewId, string appId, int port, CancellationToken cancellationToken)
    {
        if (_options.Upstream == PreviewUpstream.Network)
        {
            // The hub is on the preview's network, where Docker resolves container names.
            return new Uri($"http://{DockerContainerRuntime.PreviewName(PreviewRole.App, previewId)}:{port.ToString(CultureInfo.InvariantCulture)}");
        }
        var published = await containers.PublishedPortAsync(appId, port, cancellationToken);
        if (published is null)
        {
            await ThrowIfStoppedAsync(appId, cancellationToken);
            throw new PreviewStartException("Docker did not publish the app's port on the loopback.");
        }
        return new Uri($"http://127.0.0.1:{published.Value.ToString(CultureInfo.InvariantCulture)}");
    }

    private async Task WaitForServiceAsync(PreviewService service, string id, PreviewLogSink log, CancellationToken cancellationToken)
    {
        while (true)
        {
            var status = await containers.InspectAsync(id, cancellationToken);
            if (status is null || !status.Running)
            {
                await LogTailAsync(id, service.Name, new SecretRedactor([]), log);
                throw new PreviewStartException($"Service {service.Name} stopped with exit code {status?.ExitCode}; see the preview log.");
            }
            if (service.HealthcheckCommand is null || status.Health == "healthy")
            {
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);
        }
    }

    private async Task WaitForAppAsync(Uri upstream, string? healthPath, string appId, CancellationToken cancellationToken)
    {
        var url = new Uri(upstream, healthPath ?? "/");
        while (true)
        {
            try
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var code = (int)response.StatusCode;
                if (healthPath is null || code is >= 200 and < 400)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The single request timed out.
            }
            await ThrowIfStoppedAsync(appId, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);
        }
    }

    private async Task ThrowIfStoppedAsync(string appId, CancellationToken cancellationToken)
    {
        var status = await containers.InspectAsync(appId, cancellationToken);
        if (status is null || !status.Running)
        {
            throw new PreviewStartException($"The app stopped with exit code {status?.ExitCode}{(status?.OomKilled == true ? " (out of memory)" : "")}.");
        }
    }

    /// <summary>The last lines of a container's log, after a failure.</summary>
    private async Task LogTailAsync(string containerId, string source, SecretRedactor redactor, PreviewLogSink log)
    {
        var lines = new Queue<string>();
        try
        {
            await foreach (var line in containers.ReadLogAsync(containerId, follow: false, CancellationToken.None))
            {
                lines.Enqueue(redactor.Redact(line.Text));
                if (lines.Count > 50)
                {
                    lines.Dequeue();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lines.Enqueue($"(reading the log failed: {ex.Message})");
        }
        await log([.. lines.Select(text => new PreviewLogEntry(source, RunEventPayload.Cut(text)))], CancellationToken.None);
    }

    /// <summary>Follows a container's log and stores it in batches, so a long build shows up while it runs.</summary>
    private async Task PumpLogAsync(string containerId, string source, SecretRedactor redactor, PreviewLogSink log, CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<PreviewLogEntry>(new BoundedChannelOptions(10_000) { SingleReader = true, SingleWriter = true });
        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in containers.ReadLogAsync(containerId, follow: true, cancellationToken))
                {
                    var text = line.SkippedBytes is { } skipped ? $"[a line of {skipped} bytes was left out]" : redactor.Redact(line.Text);
                    await channel.Writer.WriteAsync(new PreviewLogEntry(source, RunEventPayload.Cut(text)), cancellationToken);
                }
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, CancellationToken.None);

        var batch = new List<PreviewLogEntry>();
        while (await WaitAsync(channel.Reader, cancellationToken))
        {
            while (batch.Count < 200 && channel.Reader.TryRead(out var line))
            {
                batch.Add(line);
            }
            if (batch.Count > 0)
            {
                await log([.. batch], cancellationToken);
                batch.Clear();
            }
        }
        await reader;
    }

    /// <summary>Waits for more lines, at most <see cref="_logFlushInterval"/>; false when the log ended.</summary>
    private async Task<bool> WaitAsync(ChannelReader<PreviewLogEntry> reader, CancellationToken cancellationToken)
    {
        if (reader.TryPeek(out _))
        {
            await Task.Delay(_logFlushInterval, timeProvider, cancellationToken);
            return true;
        }
        return await reader.WaitToReadAsync(cancellationToken);
    }
}
