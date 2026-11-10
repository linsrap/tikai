using System.Globalization;
using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Tikai.Core.Agents;
using Tikai.Orchestrator.Previews;

namespace Tikai.Orchestrator;

/// <summary>What a preview container is for; also a label.</summary>
public enum PreviewRole
{
    /// <summary>Never started: only carries the previews volume for an archive upload.</summary>
    Seed,

    /// <summary>Clones the round from the bundle the hub wrote, without network.</summary>
    Prep,

    /// <summary>One setup step of the definition, with internet (package registries).</summary>
    Setup,

    App,

    /// <summary>A sidecar from the definition (stock image).</summary>
    Service,

    /// <summary>Removes a stopped preview's directory, without network.</summary>
    Cleanup,
}

/// <summary>A preview container: what runs comes from the definition, the sandbox from the hub's settings.</summary>
/// <param name="Entrypoint">Null keeps the image's entrypoint (services).</param>
/// <param name="Command">Null keeps the image's command.</param>
/// <param name="ServiceName">The service's name, which is also its host name on the preview network.</param>
/// <param name="HealthcheckCommand">A shell command Docker runs in a service to report it healthy.</param>
/// <param name="Port">The app's port (published on the loopback in <see cref="PreviewUpstream.Loopback"/> mode).</param>
public sealed record PreviewContainerSpec(
    Guid PreviewId,
    PreviewRole Role,
    string Image,
    IReadOnlyList<string>? Entrypoint,
    IReadOnlyList<string>? Command,
    IReadOnlyList<ContainerEnv> Environment,
    string? ServiceName = null,
    string? HealthcheckCommand = null,
    int? Port = null);

/// <summary>A container or network of a preview on this installation.</summary>
/// <param name="Id">The container id, or the network name.</param>
public sealed record PreviewResource(bool IsNetwork, string Id, Guid PreviewId, PreviewRole? Role, bool Running);

/// <summary>Where things are inside a preview container: the preview's directory is mounted at /preview.</summary>
public static class PreviewLayout
{
    public const string Root = "/preview";
    public const string Source = "/preview/src";
    public const string Home = "/preview/home";
    public const string Prep = "/preview/prep";
}

public partial interface IContainerRuntime
{
    /// <summary>Pulls the image unless it is on the daemon already (a reference without tag means :latest).</summary>
    Task PullAsync(string image, CancellationToken cancellationToken);

    /// <summary>Creates a preview container with the sandbox for its role (see <see cref="DockerContainerRuntime"/>).</summary>
    Task<string> CreatePreviewAsync(PreviewContainerSpec spec, CancellationToken cancellationToken);

    /// <summary>A never-started container with the whole previews volume at /previews, only for archive uploads.</summary>
    Task<string> CreatePreviewSeedAsync(Guid previewId, string image, CancellationToken cancellationToken);

    /// <summary>A container that removes the preview's directory (like <see cref="CreateCleanupAsync"/> for runs).</summary>
    Task<string> CreatePreviewCleanupAsync(Guid previewId, string image, CancellationToken cancellationToken);

    /// <summary>The preview's own network (internal unless in loopback mode); the hub joins it in network mode.</summary>
    Task CreatePreviewNetworkAsync(Guid previewId, CancellationToken cancellationToken);

    /// <summary>Removes every container of the preview, takes the hub off its network and removes the network.</summary>
    Task RemovePreviewAsync(Guid previewId, CancellationToken cancellationToken);

    /// <summary>The loopback port Docker published for the container's port; null when there is none.</summary>
    Task<int?> PublishedPortAsync(string containerId, int port, CancellationToken cancellationToken);

    /// <summary>The containers and networks of this installation's previews (they carry its previews volume as a label).</summary>
    Task<IReadOnlyList<PreviewResource>> ListPreviewsAsync(CancellationToken cancellationToken);
}

public sealed partial class DockerContainerRuntime
{
    public const string PreviewIdLabel = "tikai.preview-id";

    /// <summary>The installation a preview resource belongs to (its previews volume): services mount no volume to filter by.</summary>
    public const string PreviewsLabel = "tikai.previews";

    /// <summary>Capabilities a service keeps, for images like postgres that start as root and drop to their own user.</summary>
    public static readonly IReadOnlyList<string> ServiceCapabilities = ["CHOWN", "SETUID", "SETGID", "DAC_OVERRIDE", "FOWNER"];

    private PreviewOptions Previews => previewOptions.Value;

    public static string PreviewName(PreviewRole role, Guid previewId, string? service = null) => role == PreviewRole.Service
        ? $"tikai-preview-{previewId:D}-{service}"
        : $"tikai-preview-{role.ToString().ToLowerInvariant()}-{previewId:D}";

    public static string PreviewNetwork(Guid previewId) => $"tikai-preview-{previewId:D}";

    public async Task PullAsync(string image, CancellationToken cancellationToken)
    {
        if (await ImageExistsAsync(image, cancellationToken))
        {
            return;
        }
        var (name, tag) = SplitReference(image);
        string? error = null;
        await docker.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = name, Tag = tag }, null,
            new Progress<JSONMessage>(m => error ??= m.Error?.Message), cancellationToken);
        if (error is not null || !await ImageExistsAsync(image, cancellationToken))
        {
            throw new InvalidOperationException($"Pulling {image} failed: {error ?? "the image is not there afterwards"}");
        }
    }

    /// <summary>Name and tag (or digest) for the pull API; an empty tag would pull every tag of the image.</summary>
    internal static (string Name, string Tag) SplitReference(string image)
    {
        var at = image.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            return (image[..at], image[(at + 1)..]);
        }
        var colon = image.LastIndexOf(':');
        return colon > image.LastIndexOf('/') ? (image[..colon], image[(colon + 1)..]) : (image, "latest");
    }

    /// <summary>
    /// Prep, setup and app run agent-written code like an agent: the agent uid, only the preview's directory (subpath
    /// = the preview's GUID) at /preview, and the sandbox of <see cref="Sandbox"/>; prep without network, setup on the
    /// agents network (internet for package registries), the app on the preview's network. Services run stock images as
    /// their own user, without the volume, with a writable root (anonymous volumes of the image only), all capabilities
    /// dropped except <see cref="ServiceCapabilities"/>, no new privileges and the same limits.
    /// </summary>
    public async Task<string> CreatePreviewAsync(PreviewContainerSpec spec, CancellationToken cancellationToken)
    {
        var parameters = spec.Role switch
        {
            PreviewRole.Prep or PreviewRole.Setup or PreviewRole.App => AgentWritten(spec),
            PreviewRole.Service => Service(spec),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Role, "Seed and cleanup containers have their own methods."),
        };
        parameters.Name = PreviewName(spec.Role, spec.PreviewId, spec.ServiceName);
        parameters.Image = spec.Image;
        // Null (not empty) keeps the image's own: an empty entrypoint would clear it.
        parameters.Entrypoint = spec.Entrypoint is null ? null! : [.. spec.Entrypoint];
        parameters.Cmd = spec.Command is null ? null! : [.. spec.Command];
        parameters.Labels = PreviewLabels(spec.PreviewId, spec.Role);
        var response = await docker.Containers.CreateContainerAsync(parameters, cancellationToken);
        return response.ID;
    }

    private CreateContainerParameters AgentWritten(PreviewContainerSpec spec)
    {
        var uid = _options.AgentUid.ToString(CultureInfo.InvariantCulture);
        var network = spec.Role switch
        {
            PreviewRole.Prep => "none",
            PreviewRole.Setup => _options.Network,
            _ => PreviewNetwork(spec.PreviewId),
        };
        var host = Sandbox(
            new Mount
            {
                Type = "volume",
                Source = Previews.PreviewsVolume,
                Target = PreviewLayout.Root,
                VolumeOptions = new VolumeOptions { Subpath = spec.PreviewId.ToString("D") },
            },
            network,
            spec.Role == PreviewRole.Setup ? [.. _options.ExtraHosts] : [],
            new SandboxLimits(Previews.MemoryBytes, Previews.Cpus, Previews.PidsLimit));
        var parameters = new CreateContainerParameters
        {
            Env = [.. spec.Environment.Select(e => $"{e.Name}={e.Value}"), $"HOME={PreviewLayout.Home}"],
            WorkingDir = spec.Role == PreviewRole.Prep ? PreviewLayout.Root : PreviewLayout.Source,
            User = $"{uid}:{uid}",
            HostConfig = host,
        };
        if (spec.Role == PreviewRole.App)
        {
            parameters.NetworkingConfig = Aliases(spec.PreviewId, "app");
            if (Previews.Upstream == PreviewUpstream.Loopback && spec.Port is { } port)
            {
                var key = $"{port.ToString(CultureInfo.InvariantCulture)}/tcp";
                parameters.ExposedPorts = new Dictionary<string, EmptyStruct> { [key] = default };
                host.PortBindings = new Dictionary<string, IList<PortBinding>> { [key] = [new PortBinding { HostIP = "127.0.0.1", HostPort = "" }] };
            }
        }
        return parameters;
    }

    private CreateContainerParameters Service(PreviewContainerSpec spec) => new()
    {
        Env = [.. spec.Environment.Select(e => $"{e.Name}={e.Value}")],
        NetworkingConfig = Aliases(spec.PreviewId, spec.ServiceName ?? throw new ArgumentException("A service needs its name.", nameof(spec))),
        Healthcheck = spec.HealthcheckCommand is null ? null : new HealthcheckConfig
        {
            Test = ["CMD-SHELL", spec.HealthcheckCommand],
            Interval = TimeSpan.FromSeconds(2),
            Timeout = TimeSpan.FromSeconds(5),
            Retries = 3,
            StartPeriod = TimeSpan.FromMinutes(2),
        },
        HostConfig = new HostConfig
        {
            CapDrop = ["ALL"],
            CapAdd = [.. ServiceCapabilities],
            SecurityOpt = ["no-new-privileges"],
            Privileged = false,
            Init = true,
            Memory = Previews.MemoryBytes,
            MemorySwap = Previews.MemoryBytes,
            NanoCPUs = (long)(Previews.Cpus * 1_000_000_000),
            PidsLimit = Previews.PidsLimit,
            NetworkMode = PreviewNetwork(spec.PreviewId),
            LogConfig = new LogConfig { Type = "json-file", Config = new Dictionary<string, string>() },
        },
    };

    private static NetworkingConfig Aliases(Guid previewId, string alias) => new()
    {
        EndpointsConfig = new Dictionary<string, EndpointSettings> { [PreviewNetwork(previewId)] = new() { Aliases = [alias] } },
    };

    public Task<string> CreatePreviewSeedAsync(Guid previewId, string image, CancellationToken cancellationToken) =>
        CreateSeedAsync(PreviewName(PreviewRole.Seed, previewId), PreviewLabels(previewId, PreviewRole.Seed), Previews.PreviewsVolume, "/previews", image, cancellationToken);

    public Task<string> CreatePreviewCleanupAsync(Guid previewId, string image, CancellationToken cancellationToken) =>
        CreateCleanupAsync(PreviewName(PreviewRole.Cleanup, previewId), PreviewLabels(previewId, PreviewRole.Cleanup), Previews.PreviewsVolume, "/previews", previewId, image, cancellationToken);

    public async Task CreatePreviewNetworkAsync(Guid previewId, CancellationToken cancellationToken)
    {
        var name = PreviewNetwork(previewId);
        try
        {
            await docker.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = name,
                Driver = "bridge",
                // No way out; in loopback mode published ports need a normal bridge.
                Internal = Previews.Upstream == PreviewUpstream.Network,
                Labels = PreviewLabels(previewId, null),
            }, cancellationToken);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Created by an earlier attempt of the same start.
        }
        if (Previews.Upstream == PreviewUpstream.Network)
        {
            var hub = Previews.HubContainer ?? throw new InvalidOperationException("Tikai:Previews:HubContainer is not set, so the hub cannot reach previews.");
            try
            {
                await docker.Networks.ConnectNetworkAsync(name, new NetworkConnectParameters { Container = hub }, cancellationToken);
            }
            catch (DockerApiException ex) when (ex.ResponseBody?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
            {
            }
        }
    }

    public async Task RemovePreviewAsync(Guid previewId, CancellationToken cancellationToken)
    {
        var listed = await docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [$"{PreviewIdLabel}={previewId:D}"] = true, [$"{PreviewsLabel}={Previews.PreviewsVolume}"] = true },
            },
        }, cancellationToken);
        foreach (var container in listed.Where(c => c.Labels is null || !c.Labels.TryGetValue(RoleLabel, out var role) || role != "cleanup"))
        {
            await RemoveAsync(container.ID, cancellationToken);
        }
        await RemoveNetworkAsync(PreviewNetwork(previewId), cancellationToken);
    }

    private async Task RemoveNetworkAsync(string name, CancellationToken cancellationToken)
    {
        if (Previews.HubContainer is { } hub)
        {
            try
            {
                await docker.Networks.DisconnectNetworkAsync(name, new NetworkDisconnectParameters { Container = hub, Force = true }, cancellationToken);
            }
            catch (DockerApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Conflict)
            {
                // No such network, or the hub is not on it.
            }
        }
        try
        {
            await docker.Networks.DeleteNetworkAsync(name, cancellationToken);
        }
        catch (DockerNetworkNotFoundException)
        {
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    public async Task<int?> PublishedPortAsync(string containerId, int port, CancellationToken cancellationToken)
    {
        var inspected = await docker.Containers.InspectContainerAsync(containerId, cancellationToken);
        var key = $"{port.ToString(CultureInfo.InvariantCulture)}/tcp";
        return inspected.NetworkSettings?.Ports is { } ports && ports.TryGetValue(key, out var bindings)
            && bindings?.FirstOrDefault(b => b.HostIP is "127.0.0.1" or "0.0.0.0") is { } binding
            && int.TryParse(binding.HostPort, NumberStyles.None, CultureInfo.InvariantCulture, out var hostPort)
            ? hostPort
            : null;
    }

    public async Task<IReadOnlyList<PreviewResource>> ListPreviewsAsync(CancellationToken cancellationToken)
    {
        var installation = new Dictionary<string, IDictionary<string, bool>>
        {
            ["label"] = new Dictionary<string, bool> { [$"{PreviewsLabel}={Previews.PreviewsVolume}"] = true },
        };
        var resources = new List<PreviewResource>();
        foreach (var container in await docker.Containers.ListContainersAsync(new ContainersListParameters { All = true, Filters = installation }, cancellationToken))
        {
            var labels = container.Labels ?? new Dictionary<string, string>();
            if (labels.TryGetValue(PreviewIdLabel, out var label) && Guid.TryParse(label, out var previewId))
            {
                PreviewRole? role = labels.TryGetValue(RoleLabel, out var name) && Enum.TryParse<PreviewRole>(name, ignoreCase: true, out var r) ? r : null;
                resources.Add(new PreviewResource(false, container.ID, previewId, role, container.State == "running"));
            }
        }
        foreach (var network in await docker.Networks.ListNetworksAsync(new NetworksListParameters { Filters = installation }, cancellationToken))
        {
            if (network.Labels?.TryGetValue(PreviewIdLabel, out var label) == true && Guid.TryParse(label, out var previewId))
            {
                resources.Add(new PreviewResource(true, network.Name, previewId, null, false));
            }
        }
        return resources;
    }

    /// <summary>Removes a network of a preview nobody claims (its containers are removed first by the caller).</summary>
    internal Task RemovePreviewNetworkAsync(string name, CancellationToken cancellationToken) => RemoveNetworkAsync(name, cancellationToken);

    private Dictionary<string, string> PreviewLabels(Guid previewId, PreviewRole? role)
    {
        var labels = new Dictionary<string, string>
        {
            [PreviewIdLabel] = previewId.ToString("D"),
            [PreviewsLabel] = Previews.PreviewsVolume,
        };
        if (role is { } r)
        {
            labels[RoleLabel] = r.ToString().ToLowerInvariant();
        }
        return labels;
    }
}
