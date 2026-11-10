namespace Tikai.Orchestrator.Previews;

/// <summary>Where preview containers run and what they may use (configuration section Tikai:Previews).</summary>
public sealed class PreviewOptions
{
    public const string Section = "Tikai:Previews";

    /// <summary>
    /// The Docker volume with the preview directories (source and setup output). Each preview container mounts only its
    /// preview's subdirectory; the hub never mounts it.
    /// </summary>
    public string PreviewsVolume { get; set; } = "tikai_previews";

    /// <summary>How the hub reaches a preview's app.</summary>
    public PreviewUpstream Upstream { get; set; } = PreviewUpstream.Network;

    /// <summary>
    /// The hub's own container (name or id), which joins each preview network to reach the app. Required for
    /// <see cref="PreviewUpstream.Network"/>; compose sets it.
    /// </summary>
    public string? HubContainer { get; set; }

    /// <summary>
    /// The host part of preview URLs: a preview is served at <c>&lt;id&gt;.&lt;Domain&gt;</c>. Empty: previews only for a hub on
    /// localhost (<c>&lt;id&gt;.preview.localhost</c>, which browsers resolve without DNS).
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>Running previews per installation; a start beyond it is refused.</summary>
    public int MaxRunning { get; set; } = 2;

    /// <summary>A preview is stopped after this time even when it is used.</summary>
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromHours(4);

    /// <summary>Limit for pulling one image.</summary>
    public TimeSpan PullTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Limit for one setup step.</summary>
    public TimeSpan SetupTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Limit for the services to be healthy and the app to answer.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Memory of each preview container (setup, app, each service).</summary>
    public long MemoryBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    public double Cpus { get; set; } = 2;

    public long PidsLimit { get; set; } = 1024;

    /// <summary>After this many lines the preview log keeps only the hub's own notes.</summary>
    public int MaxLogLines { get; set; } = 5_000;
}

public enum PreviewUpstream
{
    /// <summary>The hub joins the preview's internal network and proxies to the app's container.</summary>
    Network,

    /// <summary>
    /// Development only, for a hub that runs on the host (dotnet run, integration tests): the app's port is published
    /// on 127.0.0.1 and the preview network is a normal bridge (the app and services can reach the internet). A browser
    /// on that machine could open the port directly, without the preview's own origin.
    /// </summary>
    Loopback,
}
