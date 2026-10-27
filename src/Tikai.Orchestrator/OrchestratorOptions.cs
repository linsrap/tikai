namespace Tikai.Orchestrator;

/// <summary>Where agent containers run and what they may use (configuration section Tikai:Agents).</summary>
public sealed class OrchestratorOptions
{
    public const string Section = "Tikai:Agents";

    /// <summary>
    /// The Docker volume that holds the run directories. Each agent container mounts only its run's subdirectory
    /// (named by the run id). The hub itself never mounts it: it writes there through the Docker archive API.
    /// </summary>
    public string RunsVolume { get; set; } = "tikai_runs";

    /// <summary>The network agent containers join; it must reach the hub and the LLM provider.</summary>
    public string Network { get; set; } = "bridge";

    /// <summary>The hub's MCP endpoint as agent containers reach it.</summary>
    public string McpUrl { get; set; } = "http://hub:8080/mcp";

    /// <summary>Extra host entries for agent containers ("name:ip" or "name:host-gateway"), e.g. for tests.</summary>
    public IList<string> ExtraHosts { get; } = [];

    /// <summary>The uid (and gid) agents run as; the agent images' non-root user.</summary>
    public int AgentUid { get; set; } = 1000;

    public long MemoryBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    public double Cpus { get; set; } = 2;

    public long PidsLimit { get; set; } = 512;

    /// <summary>Size of the writable /tmp (tmpfs); the root file system is read-only.</summary>
    public string TmpSize { get; set; } = "1g";

    /// <summary>
    /// How long a blocking MCP call may stay open: the per-server timeout in the agent's MCP config, Claude Code's own
    /// hard limit. It must be longer than <see cref="BlockTimeout"/>, so the hub decides first.
    /// </summary>
    public TimeSpan ToolCallTimeout { get; set; } = TimeSpan.FromHours(5);

    /// <summary>
    /// How long an agent's container waits for an answer, from the oldest open question or plan (database time). Then
    /// the container is stopped; the run stays blocked and continues in a new container once it is answered.
    /// </summary>
    public TimeSpan BlockTimeout { get; set; } = TimeSpan.FromHours(4);

    /// <summary>The smallest gap between <see cref="BlockTimeout"/> and <see cref="ToolCallTimeout"/>.</summary>
    public static readonly TimeSpan BlockTimeoutMargin = TimeSpan.FromMinutes(15);

    /// <summary>How often runs and containers are compared (and the block timeout checked).</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Limit for the inspect container that reads the run clone's state before a resume.</summary>
    public TimeSpan InspectTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The agent must report its start (and the MCP server status) within this time, or it is stopped.</summary>
    public TimeSpan InitTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Limit for the prep container (clone, branch, hook).</summary>
    public TimeSpan PrepareTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a stopped agent gets to exit before it is killed.</summary>
    public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>After this many events only the result and the hub's own notes are stored.</summary>
    public int MaxEventsPerRun { get; set; } = 10_000;

    /// <summary>Wait before following a container's log again after the connection to Docker broke.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>How the hub reaches Docker (configuration section Tikai:Docker).</summary>
public sealed class DockerOptions
{
    public const string Section = "Tikai:Docker";

    /// <summary>tcp://docker-proxy:2375 in compose; the local socket or named pipe by default.</summary>
    public string? Host { get; set; }

    public Uri Endpoint => new(Host ?? (OperatingSystem.IsWindows() ? "npipe://./pipe/docker_engine" : "unix:///var/run/docker.sock"));
}
