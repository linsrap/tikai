using System.Buffers;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;
using Tikai.Core.Agents;
using Tikai.Orchestrator.Previews;
using ContainerSpec = Tikai.Core.Agents.ContainerSpec;

namespace Tikai.Orchestrator;

/// <summary>What a sandbox container is for; also a label, so they can be found on the daemon.</summary>
public enum SandboxRole
{
    /// <summary>Never started: only carries the volume for an archive upload.</summary>
    Seed,

    /// <summary>Runs the fixed prep script (clone, branch, hook) without network.</summary>
    Prep,

    Agent,

    /// <summary>Reads the run clone's state (git log, git status, the session file) before a resume, without network.</summary>
    Inspect,

    /// <summary>Bundles the run branch for the hub when the agent submits its work, without network.</summary>
    Bundle,

    /// <summary>Removes an ended run's directory from the runs volume, without network.</summary>
    Cleanup,
}

/// <summary>A container to create: the agent part from the adapter, the sandbox from the orchestrator's settings.</summary>
/// <param name="WithNetwork">Joins the agents network; otherwise it has none.</param>
public sealed record SandboxSpec(Guid RunId, SandboxRole Role, ContainerSpec Container, bool WithNetwork);

/// <summary>A container as Docker reports it.</summary>
/// <param name="Health">Docker's health status (starting, healthy, unhealthy) when the container has a health check.</param>
public sealed record ContainerStatus(bool Running, long ExitCode, bool OomKilled, string? Health = null);

public enum LogStream
{
    Stdout,
    Stderr,
}

/// <param name="SkippedBytes">Set instead of <paramref name="Text"/> when the line was longer than the limit.</param>
public sealed record LogLine(LogStream Stream, string Text, long? SkippedBytes = null);

/// <summary>The Docker operations the orchestrator uses, with the sandbox rules applied in one place.</summary>
public partial interface IContainerRuntime
{
    Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken);

    /// <summary>Creates a hardened container with only the run's directory mounted (at <see cref="RunLayout.Root"/>).</summary>
    Task<string> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken);

    /// <summary>Creates a never-started container with the whole runs volume at /runs, only for archive uploads.</summary>
    Task<string> CreateSeedAsync(Guid runId, string image, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a container that removes the run's directory: root (the directory holds root-owned and agent-owned files)
    /// with only the capability to override file permissions, no network, the whole runs volume at /runs, and a fixed
    /// command built from the run id.
    /// </summary>
    Task<string> CreateCleanupAsync(Guid runId, string image, CancellationToken cancellationToken);

    /// <summary>Extracts a tar into the container's file system; the tar headers set owner and mode.</summary>
    Task UploadAsync(string containerId, string path, Stream tar, CancellationToken cancellationToken);

    /// <summary>
    /// A tar of <paramref name="path"/> in the container (also a stopped one, including its mounts); Docker resolves the
    /// path inside the container and packs a symlink as a link, not its target. Null when the path does not exist.
    /// </summary>
    Task<Stream?> DownloadAsync(string containerId, string path, CancellationToken cancellationToken);

    Task StartAsync(string containerId, CancellationToken cancellationToken);

    /// <summary>The container's log lines from its start, following it until it ends.</summary>
    IAsyncEnumerable<LogLine> ReadLogAsync(string containerId, bool follow, CancellationToken cancellationToken);

    Task<long> WaitAsync(string containerId, CancellationToken cancellationToken);

    /// <summary>Null when the container does not exist.</summary>
    Task<ContainerStatus?> InspectAsync(string containerId, CancellationToken cancellationToken);

    Task StopAsync(string containerId, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Removes the container if it exists (also a running one).</summary>
    Task RemoveAsync(string containerIdOrName, CancellationToken cancellationToken);

    /// <summary>
    /// The containers of this installation that belong to a run (all states): they carry the run id label and mount
    /// this installation's runs volume, so another installation's containers on the same daemon are never listed.
    /// </summary>
    Task<IReadOnlyList<RunContainer>> ListAsync(CancellationToken cancellationToken);
}

/// <summary><see cref="IContainerRuntime"/> on Docker (Docker.DotNet.Enhanced, which has volume subpath mounts).</summary>
public sealed partial class DockerContainerRuntime(IDockerClient docker, IOptions<OrchestratorOptions> options, IOptions<PreviewOptions> previewOptions)
    : IContainerRuntime
{
    public const string RunIdLabel = "tikai.run-id";
    public const string RoleLabel = "tikai.role";

    /// <summary>One line of the log is kept in memory at most this big; longer lines are skipped.</summary>
    public const int MaxLineBytes = 1024 * 1024;

    private readonly OrchestratorOptions _options = options.Value;

    public static string Name(SandboxRole role, Guid runId) =>
        $"tikai-{role.ToString().ToLowerInvariant()}-{runId:D}";

    public async Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken)
    {
        try
        {
            await docker.Images.InspectImageAsync(image, cancellationToken);
            return true;
        }
        catch (DockerImageNotFoundException)
        {
            return false;
        }
    }

    public async Task<string> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken)
    {
        var uid = _options.AgentUid.ToString(CultureInfo.InvariantCulture);
        var response = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = Name(spec.Role, spec.RunId),
            Image = spec.Container.Image,
            Entrypoint = [.. spec.Container.Entrypoint],
            Cmd = [.. spec.Container.Command],
            Env = [.. spec.Container.Environment.Select(e => $"{e.Name}={e.Value}"), $"HOME={RunLayout.Home}"],
            WorkingDir = spec.Container.WorkingDirectory,
            User = $"{uid}:{uid}",
            Labels = Labels(spec.RunId, spec.Role),
            // Exactly the run's directory, named by its GUID: a deeper subpath could be redirected by a symlink the
            // agent planted (spike Q6).
            HostConfig = Sandbox(
                new Mount
                {
                    Type = "volume",
                    Source = _options.RunsVolume,
                    Target = RunLayout.Root,
                    VolumeOptions = new VolumeOptions { Subpath = spec.RunId.ToString("D") },
                },
                spec.WithNetwork ? _options.Network : "none",
                spec.WithNetwork ? [.. _options.ExtraHosts] : [],
                new SandboxLimits(_options.MemoryBytes, _options.Cpus, _options.PidsLimit)),
        }, cancellationToken);
        return response.ID;
    }

    public Task<string> CreateSeedAsync(Guid runId, string image, CancellationToken cancellationToken) =>
        CreateSeedAsync(Name(SandboxRole.Seed, runId), Labels(runId, SandboxRole.Seed), _options.RunsVolume, "/runs", image, cancellationToken);

    // rm does not follow symlinks the agent may have left; the path comes from the GUID only.
    public Task<string> CreateCleanupAsync(Guid runId, string image, CancellationToken cancellationToken) =>
        CreateCleanupAsync(Name(SandboxRole.Cleanup, runId), Labels(runId, SandboxRole.Cleanup), _options.RunsVolume, "/runs", runId, image, cancellationToken);

    /// <summary>A never-started container with a whole volume mounted, only for archive uploads.</summary>
    private async Task<string> CreateSeedAsync(
        string name, Dictionary<string, string> labels, string volume, string target, string image, CancellationToken cancellationToken)
    {
        var response = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = name,
            Image = image,
            Entrypoint = ["true"],
            Labels = labels,
            HostConfig = new HostConfig
            {
                Mounts = [new Mount { Type = "volume", Source = volume, Target = target }],
                NetworkMode = "none",
            },
        }, cancellationToken);
        return response.ID;
    }

    /// <summary>Root with only CAP_DAC_OVERRIDE, no network, the whole volume, and <c>rm -rf -- target/id</c>.</summary>
    private async Task<string> CreateCleanupAsync(
        string name, Dictionary<string, string> labels, string volume, string target, Guid id, string image, CancellationToken cancellationToken)
    {
        var response = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = name,
            Image = image,
            Entrypoint = ["rm", "-rf", "--", $"{target}/{id:D}"],
            Cmd = [],
            User = "0:0",
            Labels = labels,
            HostConfig = new HostConfig
            {
                Mounts = [new Mount { Type = "volume", Source = volume, Target = target }],
                ReadonlyRootfs = true,
                CapDrop = ["ALL"],
                // The agent's directories may be read-only for root without it (it can chmod its own files).
                CapAdd = ["DAC_OVERRIDE"],
                SecurityOpt = ["no-new-privileges"],
                Privileged = false,
                Memory = _options.MemoryBytes,
                PidsLimit = _options.PidsLimit,
                NetworkMode = "none",
                LogConfig = new LogConfig { Type = "json-file", Config = new Dictionary<string, string>() },
            },
        }, cancellationToken);
        return response.ID;
    }

    public Task UploadAsync(string containerId, string path, Stream tar, CancellationToken cancellationToken) =>
        docker.Containers.ExtractArchiveToContainerAsync(containerId, new CopyToContainerParameters { Path = path }, tar, cancellationToken);

    public async Task<Stream?> DownloadAsync(string containerId, string path, CancellationToken cancellationToken)
    {
        try
        {
            var response = await docker.Containers.GetArchiveFromContainerAsync(
                containerId, new ContainerPathStatParameters { Path = path }, statOnly: false, cancellationToken);
            return response.Stream;
        }
        catch (DockerContainerNotFoundException)
        {
            // Docker answers 404 for a missing container and for a missing path alike.
            return null;
        }
    }

    public Task StartAsync(string containerId, CancellationToken cancellationToken) =>
        docker.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), cancellationToken);

    public async IAsyncEnumerable<LogLine> ReadLogAsync(string containerId, bool follow, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // No timestamps: Docker splits lines over 16 KB into chunks and would put a timestamp before every chunk.
        using var stream = await docker.Containers.GetContainerLogsAsync(containerId, new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Follow = follow,
        }, cancellationToken);

        var splitters = new Dictionary<LogStream, LineSplitter> { [LogStream.Stdout] = new(), [LogStream.Stderr] = new() };
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
            if (read.EOF)
            {
                break;
            }
            var target = read.Target == MultiplexedStream.TargetStream.StandardError ? LogStream.Stderr : LogStream.Stdout;
            foreach (var line in splitters[target].Add(buffer.AsSpan(0, read.Count)))
            {
                yield return line.ToLogLine(target);
            }
        }
        foreach (var (target, splitter) in splitters)
        {
            if (splitter.Flush() is { } rest)
            {
                yield return rest.ToLogLine(target);
            }
        }
    }

    public async Task<long> WaitAsync(string containerId, CancellationToken cancellationToken) =>
        (await docker.Containers.WaitContainerAsync(containerId, cancellationToken)).StatusCode;

    public async Task<ContainerStatus?> InspectAsync(string containerId, CancellationToken cancellationToken)
    {
        try
        {
            var state = (await docker.Containers.InspectContainerAsync(containerId, cancellationToken)).State;
            return new ContainerStatus(state?.Running ?? false, state?.ExitCode ?? 0, state?.OOMKilled ?? false, state?.Health?.Status);
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
    }

    public async Task StopAsync(string containerId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await docker.Containers.StopContainerAsync(containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = (uint)Math.Max(0, timeout.TotalSeconds) }, cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
        }
    }

    public async Task RemoveAsync(string containerIdOrName, CancellationToken cancellationToken)
    {
        try
        {
            await docker.Containers.RemoveContainerAsync(containerIdOrName, new ContainerRemoveParameters { Force = true }, cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Removal already in progress.
        }
    }

    public async Task<IReadOnlyList<RunContainer>> ListAsync(CancellationToken cancellationToken)
    {
        var listed = await docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [RunIdLabel] = true },
                ["volume"] = new Dictionary<string, bool> { [_options.RunsVolume] = true },
            },
        }, cancellationToken);
        var containers = new List<RunContainer>();
        foreach (var container in listed)
        {
            var labels = container.Labels ?? new Dictionary<string, string>();
            if (labels.TryGetValue(RunIdLabel, out var label) && Guid.TryParse(label, out var runId))
            {
                SandboxRole? role = labels.TryGetValue(RoleLabel, out var name) && Enum.TryParse<SandboxRole>(name, ignoreCase: true, out var r) ? r : null;
                containers.Add(new RunContainer(container.ID, runId, role, container.State == "running"));
            }
        }
        return containers;
    }

    /// <summary>
    /// The sandbox of every container that runs agent-written code (agent, helpers, preview setup and app): the agent
    /// uid is set by the caller; here a read-only root with a tmpfs /tmp, no capabilities, no new privileges, an init
    /// process, memory, CPU and pid limits, and a json-file log kept whole.
    /// </summary>
    private HostConfig Sandbox(Mount? mount, string networkMode, IList<string> extraHosts, SandboxLimits limits) => new()
    {
        Mounts = mount is null ? [] : [mount],
        ReadonlyRootfs = true,
        Tmpfs = new Dictionary<string, string> { ["/tmp"] = $"rw,nosuid,nodev,size={_options.TmpSize},mode=1777" },
        CapDrop = ["ALL"],
        SecurityOpt = ["no-new-privileges"],
        Privileged = false,
        Init = true,
        Memory = limits.MemoryBytes,
        MemorySwap = limits.MemoryBytes,
        NanoCPUs = (long)(limits.Cpus * 1_000_000_000),
        PidsLimit = limits.PidsLimit,
        NetworkMode = networkMode,
        ExtraHosts = extraHosts,
        // Kept whole and unrotated: after a hub restart the log is read again from the start (see AgentRun.LogLinesRead).
        LogConfig = new LogConfig { Type = "json-file", Config = new Dictionary<string, string>() },
    };

    private sealed record SandboxLimits(long MemoryBytes, double Cpus, long PidsLimit);

    private static Dictionary<string, string> Labels(Guid runId, SandboxRole role) => new()
    {
        [RunIdLabel] = runId.ToString("D"),
        [RoleLabel] = role.ToString().ToLowerInvariant(),
    };
}

/// <summary>Splits one log stream into lines; a line longer than <see cref="DockerContainerRuntime.MaxLineBytes"/> is skipped.</summary>
internal sealed class LineSplitter
{
    private readonly ArrayBufferWriter<byte> _line = new();
    private long _skipped;

    public List<SplitLine> Add(ReadOnlySpan<byte> data)
    {
        var lines = new List<SplitLine>();
        while (!data.IsEmpty)
        {
            var newline = data.IndexOf((byte)'\n');
            var part = newline < 0 ? data : data[..newline];
            Append(part);
            if (newline < 0)
            {
                break;
            }
            lines.Add(Take());
            data = data[(newline + 1)..];
        }
        return lines;
    }

    public SplitLine? Flush() => _line.WrittenCount > 0 || _skipped > 0 ? Take() : null;

    private void Append(ReadOnlySpan<byte> part)
    {
        if (_skipped > 0 || _line.WrittenCount + part.Length > DockerContainerRuntime.MaxLineBytes)
        {
            _skipped += _line.WrittenCount + part.Length;
            _line.Clear();
            return;
        }
        _line.Write(part);
    }

    private SplitLine Take()
    {
        var line = _skipped > 0
            ? new SplitLine(null, _skipped)
            : new SplitLine(System.Text.Encoding.UTF8.GetString(_line.WrittenSpan).TrimEnd('\r'), null);
        _line.Clear();
        _skipped = 0;
        return line;
    }
}

internal sealed record SplitLine(string? Text, long? SkippedBytes)
{
    public LogLine ToLogLine(LogStream stream) => new(stream, Text ?? "", SkippedBytes);
}
