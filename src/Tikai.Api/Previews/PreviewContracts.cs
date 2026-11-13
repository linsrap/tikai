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

/// <summary>What the project page shows about previewing a repository's default branch.</summary>
/// <param name="Available">False when it cannot be started (see <paramref name="UnavailableReason"/>): previews are off,
/// the project is archived, the repository is not ready, or the branch has no commits.</param>
/// <param name="HeadSha">The branch's head now; null when it has none.</param>
/// <param name="Found">The head has a tikai-preview.yml.</param>
/// <param name="Errors">Why the file cannot be used; empty when it can.</param>
/// <param name="Preview">The latest preview of the branch, active or ended.</param>
public sealed record RepositoryPreviewInfo(
    bool Available, string? UnavailableReason, string Branch, string? HeadSha, bool Found, IReadOnlyList<string> Errors, PreviewInfo? Preview);

/// <param name="Url">The preview's own origin while it runs; people open it through an entry link (POST …/entry).</param>
/// <param name="Apps">The app processes of its definition with the path prefix each serves on that origin.</param>
/// <param name="ChangeSetId">With <paramref name="RoundNo"/>, set for a round's preview.</param>
/// <param name="Branch">Set for a preview of the repository's default branch, which stops when the branch moves.</param>
public sealed record PreviewInfo(
    Guid Id,
    Guid RepositoryId,
    Guid? ChangeSetId,
    int? RoundNo,
    string? Branch,
    string HeadSha,
    PreviewState State,
    string? Error,
    string? StopReason,
    DateTimeOffset StartedAt,
    DateTimeOffset? RunningAt,
    DateTimeOffset? StoppedAt,
    int IdleMinutes,
    long LastLogSeq,
    string? Url,
    IReadOnlyList<PreviewAppInfo> Apps);

/// <param name="Path">The path prefix it serves; the longest match wins.</param>
/// <param name="LogSource">The source of its lines in the preview log (<c>app</c> for a single app, else <c>app &lt;name&gt;</c>).</param>
public sealed record PreviewAppInfo(string Name, string Path, string LogSource);

/// <param name="Url">A one-time link that lets this browser into the preview's host.</param>
public sealed record PreviewEntryInfo(string Url);

public sealed record PreviewLogLineInfo(long Seq, string Source, string Text, DateTimeOffset At);
