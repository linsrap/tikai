using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tikai.Api.Llm;
using Tikai.Api.Projects;
using Tikai.Core.Events;
using Tikai.Core.Git;
using Tikai.Core.Jobs;
using Tikai.Core.Previews;
using Tikai.Core.Reviews;
using Tikai.Data;
using Tikai.Orchestrator;
using Tikai.Orchestrator.Previews;

namespace Tikai.Api.Previews;

/// <summary>Start, stop and watch previews of a review round (the latest one).</summary>
internal static class PreviewEndpoints
{
    public const int MaxLogLines = 1_000;

    public static void MapPreviewEndpoints(this RouteGroupBuilder api)
    {
        var changeSets = api.MapGroup("/change-sets").WithTags("Previews").RequireAuthorization();
        changeSets.MapGet("/{id:guid}/preview", GetForChangeSetAsync);
        changeSets.MapPost("/{id:guid}/previews", StartAsync);

        var previews = api.MapGroup("/previews").WithTags("Previews").RequireAuthorization();
        previews.MapPost("/{id:guid}/stop", StopAsync);
        previews.MapPost("/{id:guid}/entry", EntryAsync);
        previews.MapGet("/{id:guid}/log", LogAsync);
    }

    private static async Task<Results<Ok<ChangeSetPreviewInfo>, ProblemHttpResult>> GetForChangeSetAsync(
        Guid id, HttpRequest request, PreviewService previews, CancellationToken cancellationToken)
    {
        var target = await previews.TargetAsync(id, cancellationToken);
        if (target is null)
        {
            return ProjectEndpoints.NotFound("Change set");
        }
        var definition = await previews.ReadDefinitionAsync(target, cancellationToken);
        var latest = await previews.LatestAsync(id, request, cancellationToken);
        var unavailable = previews.Unavailable(request);
        return TypedResults.Ok(new ChangeSetPreviewInfo(unavailable is null, unavailable,
            new PreviewDefinitionInfo(target.RoundNo, definition.Found, definition.Errors), latest));
    }

    private static async Task<Results<Ok<PreviewInfo>, ProblemHttpResult>> StartAsync(
        Guid id, HttpRequest request, ClaimsPrincipal user, PreviewService previews, CancellationToken cancellationToken) =>
        await previews.StartAsync(id, LlmProviderService.UserId(user), request, cancellationToken);

    /// <summary>
    /// A one-time link into a running preview for this person's browser (valid for two minutes): the hub's login does
    /// not reach the preview's host, so the review screen asks for one each time it opens the preview.
    /// </summary>
    private static async Task<Results<Ok<PreviewEntryInfo>, ProblemHttpResult>> EntryAsync(
        Guid id, HttpRequest request, ClaimsPrincipal user, TikaiDbContext db, PreviewService previews, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (previews.Unavailable(request) is { } reason)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Previews are off", detail: reason);
        }
        if (!await db.Previews.AnyAsync(p => p.Id == id, cancellationToken))
        {
            return ProjectEndpoints.NotFound("Preview");
        }
        if (!await db.Previews.AnyAsync(p => p.Id == id && p.State == PreviewState.Running, cancellationToken))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not running", detail: "The preview is not running.");
        }
        var token = "tikai_entry_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = timeProvider.GetUtcNow();
        db.PreviewEntries.Add(new PreviewEntry
        {
            Id = Guid.CreateVersion7(now),
            PreviewId = id,
            UserId = LlmProviderService.UserId(user),
            TokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
            ExpiresAt = now + PreviewRules.EntryLifetime,
        });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new PreviewEntryInfo($"{previews.Origin(id, request)}{PreviewHosts.EntryPath}?t={token}"));
    }

    private static async Task<Results<Ok<PreviewInfo>, ProblemHttpResult>> StopAsync(
        Guid id, ClaimsPrincipal user, TikaiDbContext db, IJobQueue jobs, IEventPublisher events, PreviewService previews,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var name = user.Identity?.Name ?? "someone";
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            if (await PreviewStops.StopAsync(db, jobs, events, id, $"Stopped by {name}.", timeProvider.GetUtcNow(), cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        return await previews.InfoAsync(id, null, cancellationToken) is { } info ? TypedResults.Ok(info) : ProjectEndpoints.NotFound("Preview");
    }

    private static async Task<Results<Ok<List<PreviewLogLineInfo>>, ProblemHttpResult>> LogAsync(
        Guid id, TikaiDbContext db, CancellationToken cancellationToken, long afterSeq = 0, int limit = 500)
    {
        if (!await db.Previews.AnyAsync(p => p.Id == id, cancellationToken))
        {
            return ProjectEndpoints.NotFound("Preview");
        }
        var lines = await db.PreviewLogLines
            .Where(l => l.PreviewId == id && l.Seq > afterSeq)
            .OrderBy(l => l.Seq)
            .Take(Math.Clamp(limit, 1, MaxLogLines))
            .Select(l => new PreviewLogLineInfo(l.Seq, l.Source, l.Text, l.CreatedAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(lines);
    }
}

/// <summary>The change set's latest round, where its repository's mirror is, and whether it is open.</summary>
internal sealed record PreviewTarget(Guid ChangeSetId, ChangeSetStatus Status, int RoundNo, string HeadSha, string Mirror);

/// <summary>What the hub read from a round's tikai-preview.yml.</summary>
internal sealed record DefinitionRead(bool Found, string? Text, IReadOnlyList<string> Errors, PreviewDefinition? Definition);

/// <summary>
/// Starts previews: the latest round's definition is read from the mirror and checked, then, under one lock for the
/// whole installation, the change set's active preview is replaced and the limit of running previews is checked.
/// </summary>
internal sealed class PreviewService(
    TikaiDbContext db,
    IGitService git,
    IJobQueue jobs,
    IEventPublisher events,
    MirrorPaths mirrors,
    IOptions<PreviewOptions> options,
    IOptions<DockerOptions> docker,
    TimeProvider timeProvider)
{
    /// <summary>Serializes starts, so two of them cannot both pass the limit.</summary>
    private const long StartLock = 0x7469_6b61_6970_7276; // "tikaiprv"

    public async Task<PreviewTarget?> TargetAsync(Guid changeSetId, CancellationToken cancellationToken)
    {
        var target = await (
            from c in db.ChangeSets
            join r in db.Repositories on c.RepositoryId equals r.Id
            where c.Id == changeSetId
            select new { c.Status, r.ProjectId, RepositoryId = r.Id }).SingleOrDefaultAsync(cancellationToken);
        if (target is null)
        {
            return null;
        }
        var round = await db.ReviewRounds.Where(r => r.ChangeSetId == changeSetId).OrderByDescending(r => r.RoundNo)
            .Select(r => new { r.RoundNo, r.HeadSha }).FirstAsync(cancellationToken);
        return new PreviewTarget(changeSetId, target.Status, round.RoundNo, round.HeadSha, mirrors.For(target.ProjectId, target.RepositoryId));
    }

    public async Task<DefinitionRead> ReadDefinitionAsync(PreviewTarget target, CancellationToken cancellationToken)
    {
        if (await git.BlobIdAsync(target.Mirror, target.HeadSha, PreviewDefinition.FileName, cancellationToken) is not { } blobId)
        {
            return new DefinitionRead(false, null, [], null);
        }
        var blob = await git.ReadBlobAsync(target.Mirror, blobId, PreviewDefinition.MaxBytes, cancellationToken);
        if (blob?.Content is not { } content)
        {
            return new DefinitionRead(true, null, [$"the file is larger than {PreviewDefinition.MaxBytes / 1024} KB"], null);
        }
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return new DefinitionRead(true, null, ["the file is not UTF-8 text"], null);
        }
        var parsed = PreviewDefinition.Parse(text);
        return new DefinitionRead(true, text, parsed.Errors, parsed.Definition);
    }

    /// <summary>Why previews cannot run for a hub opened at this request's host; null when they can.</summary>
    public string? Unavailable(HttpRequest request)
    {
        if (!docker.Value.Enabled)
        {
            return DockerOptions.OffMessage;
        }
        if (options.Value is { Upstream: PreviewUpstream.Network, HubContainer: null or "" })
        {
            return "Previews need Tikai:Previews:HubContainer (the hub's own container), so the hub can reach them. See docs/self-hosting.md.";
        }
        return PreviewHosts.Suffix(options.Value.Domain, request.Host.Host) is null
            ? "Previews need their own host names: set TIKAI_PREVIEW_DOMAIN to a domain with wildcard DNS to this hub (see docs/self-hosting.md), or open Tikai on localhost."
            : null;
    }

    /// <summary>The preview's origin as people open it, with the scheme and port they use for the hub.</summary>
    public string? Origin(Guid previewId, HttpRequest request) =>
        PreviewHosts.Suffix(options.Value.Domain, request.Host.Host) is { } suffix
            ? PreviewHosts.Origin(previewId, suffix, request.Scheme, request.Host.Port)
            : null;

    public async Task<Results<Ok<PreviewInfo>, ProblemHttpResult>> StartAsync(Guid changeSetId, Guid userId, HttpRequest request, CancellationToken cancellationToken)
    {
        if (Unavailable(request) is { } reason)
        {
            return Problem(StatusCodes.Status409Conflict, "Previews are off", reason);
        }
        var target = await TargetAsync(changeSetId, cancellationToken);
        if (target is null)
        {
            return ProjectEndpoints.NotFound("Change set");
        }
        if (target.Status != ChangeSetStatus.Open)
        {
            return Problem(StatusCodes.Status409Conflict, "Not open", "Only an open change set can be previewed.");
        }
        var definition = await ReadDefinitionAsync(target, cancellationToken);
        if (!definition.Found)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "No preview definition",
                $"Round {target.RoundNo} has no {PreviewDefinition.FileName} at the repository root.");
        }
        if (definition is not { Definition: { } parsed, Text: { } text })
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, $"{PreviewDefinition.FileName} cannot be used", string.Join("\n", definition.Errors));
        }

        var now = timeProvider.GetUtcNow();
        var previewId = Guid.CreateVersion7(now);
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({StartLock})", cancellationToken);
            await PreviewStops.ForChangeSetAsync(db, jobs, events, changeSetId, "Replaced by a new preview.", now, cancellationToken);
            var running = await db.Previews.CountAsync(p => PreviewRules.Active.Contains(p.State), cancellationToken);
            if (running >= options.Value.MaxRunning)
            {
                return Problem(StatusCodes.Status409Conflict, "Too many previews",
                    $"At most {options.Value.MaxRunning} previews run at a time on this hub. Stop one first.");
            }
            db.Previews.Add(new Preview
            {
                Id = previewId,
                ChangeSetId = changeSetId,
                RoundNo = target.RoundNo,
                HeadSha = target.HeadSha,
                State = PreviewState.Preparing,
                Definition = text,
                IdleMinutes = parsed.IdleMinutes,
                StartedById = userId,
            });
            await db.SaveChangesAsync(cancellationToken);
            // Tried once: a start interrupted by a restart is failed by the reconciler (people start it again).
            await jobs.EnqueueAsync(PreviewJob.StartQueue, new PreviewJob(previewId).ToJson(), maxAttempts: 1, cancellationToken: cancellationToken);
            await events.PublishAsync(Preview.Topic, previewId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return TypedResults.Ok((await InfoAsync(previewId, request, cancellationToken))!);
    }

    public async Task<PreviewInfo?> LatestAsync(Guid changeSetId, HttpRequest request, CancellationToken cancellationToken)
    {
        var id = await db.Previews.Where(p => p.ChangeSetId == changeSetId).OrderByDescending(p => p.CreatedAt)
            .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(cancellationToken);
        return id is null ? null : await InfoAsync(id.Value, request, cancellationToken);
    }

    /// <param name="request">For the preview's address while it runs; null leaves it out.</param>
    public async Task<PreviewInfo?> InfoAsync(Guid previewId, HttpRequest? request, CancellationToken cancellationToken)
    {
        var p = await db.Previews.AsNoTracking().SingleOrDefaultAsync(p => p.Id == previewId, cancellationToken);
        return p is null ? null : new PreviewInfo(
            p.Id, p.ChangeSetId, p.RoundNo, p.HeadSha, p.State, p.Error, p.StopReason, p.CreatedAt, p.RunningAt, p.StoppedAt,
            p.IdleMinutes, p.LastLogSeq, p.State == PreviewState.Running && request is not null ? Origin(p.Id, request) : null,
            Apps(p.Definition));
    }

    /// <summary>The apps of the definition stored with the preview (checked when it was started).</summary>
    private static List<PreviewAppInfo> Apps(string definition) =>
        PreviewDefinition.Parse(definition).Definition is { } parsed
            ? [.. parsed.Apps.Select(a => new PreviewAppInfo(a.Name, a.Path, a.LogSource))]
            : [];

    private static ProblemHttpResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail);
}
