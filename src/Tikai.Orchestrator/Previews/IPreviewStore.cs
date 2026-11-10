using System.Text.Json;
using Tikai.Core.Previews;

namespace Tikai.Orchestrator.Previews;

/// <summary>What the preview jobs and the reconciler read and change in the database (implemented by the API host).</summary>
public interface IPreviewStore
{
    /// <summary>What a start needs; null when the preview is not preparing any more (stopped meanwhile).</summary>
    Task<PreviewStartInfo?> GetStartInfoAsync(Guid previewId, CancellationToken cancellationToken);

    /// <summary>Appends lines with numbers from the preview's counter; past the limit only the hub's own lines.</summary>
    Task AppendLogAsync(Guid previewId, IReadOnlyList<PreviewLogEntry> lines, CancellationToken cancellationToken);

    Task<bool> IsActiveAsync(Guid previewId, CancellationToken cancellationToken);

    /// <summary>preparing → starting; false when it was stopped meanwhile.</summary>
    Task<bool> MarkStartingAsync(Guid previewId, CancellationToken cancellationToken);

    /// <summary>starting → running with where the hub reaches each app; false when it was stopped meanwhile.</summary>
    Task<bool> MarkRunningAsync(Guid previewId, IReadOnlyList<PreviewAppUpstream> apps, CancellationToken cancellationToken);

    /// <summary>An active preview (in <paramref name="state"/> when given) → failed, and its teardown queued.</summary>
    Task<bool> FailAsync(Guid previewId, string reason, PreviewState? state, CancellationToken cancellationToken);

    /// <summary>An active preview → stopped, and its teardown queued.</summary>
    Task<bool> StopAsync(Guid previewId, string reason, CancellationToken cancellationToken);

    /// <summary>Ended and its directory not yet removed.</summary>
    Task<bool> NeedsCleanupAsync(Guid previewId, CancellationToken cancellationToken);

    Task MarkCleanedAsync(Guid previewId, CancellationToken cancellationToken);

    /// <summary>Previews in preparing, starting or running, with what the reconciler decides on (times by the database clock).</summary>
    Task<IReadOnlyList<ActivePreview>> ListActiveAsync(CancellationToken cancellationToken);

    /// <summary>Ended previews whose directory is still there and whose teardown is not queued.</summary>
    Task<IReadOnlyList<Guid>> ListUncleanedAsync(CancellationToken cancellationToken);

    /// <summary>Queues a teardown unless one is queued or a start is still working on the preview.</summary>
    Task RequestTeardownAsync(Guid previewId, CancellationToken cancellationToken);
}

/// <param name="RoundRef">The round's pinned ref in the mirror, which is bundled and checked out.</param>
/// <param name="Definition">The tikai-preview.yml text checked when the preview was started.</param>
public sealed record PreviewStartInfo(Guid PreviewId, string MirrorPath, string RoundRef, string Definition);

/// <param name="ChangeSetOpen">Open or being delivered; a preview of a merged, pushed or closed change set stops.</param>
/// <param name="Age">Since the preview was started.</param>
/// <param name="Apps">The apps and their containers while it runs; empty before.</param>
/// <param name="IdleFor">Since the last proxied request (or since it runs); null before it runs.</param>
public sealed record ActivePreview(
    Guid PreviewId,
    PreviewState State,
    bool ChangeSetOpen,
    IReadOnlyList<PreviewAppUpstream> Apps,
    bool HasLiveStartJob,
    TimeSpan Age,
    TimeSpan? IdleFor,
    int IdleMinutes);

/// <summary>Job payload of the preview queues.</summary>
public sealed record PreviewJob(Guid PreviewId)
{
    /// <summary>Starts (clone, setup, services, app): long, one at a time.</summary>
    public const string StartQueue = "previews";

    /// <summary>Teardowns (containers, network, directory): their own queue, so a stop never waits behind a long start.</summary>
    public const string TeardownQueue = "preview_stops";

    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string ToJson() => JsonSerializer.Serialize(this, _json);

    public static PreviewJob FromJson(string json) => JsonSerializer.Deserialize<PreviewJob>(json, _json)!;
}
