using Tikai.Core.Previews;

namespace Tikai.Api.Previews;

/// <summary>What the review screen shows about previews of a change set.</summary>
/// <param name="Available">False when this installation cannot serve previews (see <paramref name="UnavailableReason"/>).</param>
/// <param name="Definition">The latest round's tikai-preview.yml.</param>
/// <param name="Preview">The latest preview of the change set, active or ended.</param>
public sealed record ChangeSetPreviewInfo(bool Available, string? UnavailableReason, PreviewDefinitionInfo Definition, PreviewInfo? Preview);

/// <param name="Found">The latest round has the file.</param>
/// <param name="Errors">Why the file cannot be used; empty when it can.</param>
public sealed record PreviewDefinitionInfo(int RoundNo, bool Found, IReadOnlyList<string> Errors);

/// <param name="Url">The preview's own origin while it runs; people open it through an entry link (POST …/entry).</param>
public sealed record PreviewInfo(
    Guid Id,
    Guid ChangeSetId,
    int RoundNo,
    string HeadSha,
    PreviewState State,
    string? Error,
    string? StopReason,
    DateTimeOffset StartedAt,
    DateTimeOffset? RunningAt,
    DateTimeOffset? StoppedAt,
    int IdleMinutes,
    long LastLogSeq,
    string? Url);

/// <param name="Url">A one-time link that lets this browser into the preview's host.</param>
public sealed record PreviewEntryInfo(string Url);

public sealed record PreviewLogLineInfo(long Seq, string Source, string Text, DateTimeOffset At);
