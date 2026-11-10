using Tikai.Core.Common;

namespace Tikai.Core.Previews;

/// <summary>
/// A short-lived instance of a review round's app, started from the round's <c>tikai-preview.yml</c>. Every start is a
/// fresh preview with empty data; nothing is kept after it stops. At most one is active per change set.
/// </summary>
public class Preview : Entity
{
    /// <summary>Event topic for state changes and new log lines (the published id is the preview's).</summary>
    public const string Topic = "previews";

    /// <summary>The only environment until named environments per project exist.</summary>
    public const string DefaultEnvironment = "default";

    public Guid ChangeSetId { get; set; }

    public int RoundNo { get; set; }

    /// <summary>The round's head, which the preview runs.</summary>
    public required string HeadSha { get; set; }

    public string Environment { get; set; } = DefaultEnvironment;

    public PreviewState State { get; set; }

    /// <summary>Why it failed, for people.</summary>
    public string? Error { get; set; }

    /// <summary>Why it stopped: the button, idle time, the time limit, the change set's end, a newer preview.</summary>
    public string? StopReason { get; set; }

    /// <summary>The definition as it was read and checked when the preview was started.</summary>
    public required string Definition { get; set; }

    public int IdleMinutes { get; set; }

    public Guid StartedById { get; set; }

    public DateTimeOffset? RunningAt { get; set; }

    /// <summary>The last request the hub proxied to the app (written at most once a minute).</summary>
    public DateTimeOffset? LastRequestAt { get; set; }

    public DateTimeOffset? StoppedAt { get; set; }

    /// <summary>Set once its containers, network and directory are gone.</summary>
    public DateTimeOffset? CleanedAt { get; set; }

    /// <summary>Where the hub reaches the app (set while running).</summary>
    public string? Upstream { get; set; }

    public string? AppContainerId { get; set; }

    /// <summary>The number of the last log line (counter for <see cref="PreviewLogLine.Seq"/>).</summary>
    public long LastLogSeq { get; set; }

    public bool IsActive => State is PreviewState.Preparing or PreviewState.Starting or PreviewState.Running;
}

public enum PreviewState
{
    /// <summary>Cloning the round and running the setup steps.</summary>
    Preparing,

    /// <summary>Starting the services and the app, waiting for its health check.</summary>
    Starting,
    Running,
    Stopped,
    Failed,
}

/// <summary>One line of a preview's log: the hub's notes and the output of setup steps, services and the app.</summary>
public class PreviewLogLine : Entity
{
    public Guid PreviewId { get; set; }

    public long Seq { get; set; }

    /// <summary>hub, setup 1, app, a service's name.</summary>
    public required string Source { get; set; }

    public required string Text { get; set; }
}

/// <summary>
/// A one-time link that lets one person's browser into a preview's host: the hub's login cookie does not reach that
/// host, so the review screen asks for an entry, and the preview host trades it for its own cookie.
/// </summary>
public class PreviewEntry : Entity
{
    public Guid PreviewId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the token (hex); the token itself is only in the link.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? UsedAt { get; set; }
}

public static class PreviewRules
{
    /// <summary>How long an entry link works (once).</summary>
    public static readonly TimeSpan EntryLifetime = TimeSpan.FromMinutes(2);

    public const int MaxErrorLength = 4_000;
    public const int MaxSourceLength = 40;

    /// <summary>States in which a preview has (or is getting) containers.</summary>
    public static readonly IReadOnlyList<PreviewState> Active = [PreviewState.Preparing, PreviewState.Starting, PreviewState.Running];
}
