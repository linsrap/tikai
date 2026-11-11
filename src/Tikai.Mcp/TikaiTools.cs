using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tikai.Core.Attention;
using Tikai.Core.Runs;

namespace Tikai.Mcp;

/// <summary>
/// The tools an agent sees. Parameter names are the tools' argument names (snake_case, as in the design doc).
/// Errors the agent can fix are thrown as <see cref="McpException"/>, which the SDK returns as a tool error with the message.
/// </summary>
[McpServerToolType]
internal sealed partial class TikaiTools(
    IRunTools runs,
    IAttentionWaiter waiter,
    IOptions<McpOptions> options,
    TimeProvider timeProvider,
    ILogger<TikaiTools> logger)
{
    private const string WaitNote =
        " This blocks until a human answers, which can take minutes or hours. Keep waiting; do not call it again unless the call fails.";

    [McpServerTool(Name = "get_ticket", ReadOnly = true)]
    [Description("Returns your ticket: key, title, description (markdown), status, priority and the acceptance criteria with their ids.")]
    public Task<TicketResult> GetTicket(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken) =>
        runs.GetTicketAsync(RunScope.From(request.User), cancellationToken);

    [McpServerTool(Name = "report_progress")]
    [Description("Tells the humans what you are working on now. Shown on the agent board; does not wait.")]
    public async Task<string> ReportProgress(
        RequestContext<CallToolRequestParams> request,
        [Description("The current step, one short line.")] string step,
        [Description("Optional detail.")] string? note = null,
        CancellationToken cancellationToken = default)
    {
        var text = step?.Trim() ?? "";
        if (text.Length is 0 or > RunRules.MaxProgressStepLength || note?.Length > RunRules.MaxProgressNoteLength)
        {
            throw new McpException(
                $"Give a step of 1 to {RunRules.MaxProgressStepLength} characters and a note of at most {RunRules.MaxProgressNoteLength}.");
        }
        await runs.ReportProgressAsync(RunScope.From(request.User), text, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), cancellationToken);
        return "Progress recorded.";
    }

    [McpServerTool(Name = "submit_for_review")]
    [Description("Submits your work for human review with a short self-report: the committed state of your branch is taken, uncommitted changes are not. Commit first. Does not wait for the review; stop after it.")]
    public async Task<string> SubmitForReview(
        RequestContext<CallToolRequestParams> request,
        [Description("What you changed and why, in at most 3 sentences (400 characters). Details belong in the commits.")] string summary,
        [Description("For each acceptance criterion (id from get_ticket): covered, partial or missing, with a note on where or why (at most 200 characters).")]
        IReadOnlyList<CriterionMapping>? criteria_mapping = null,
        [Description("At most 3 things a reviewer must know: what you are unsure about or deliberately left out (200 characters each).")]
        IReadOnlyList<string>? open_concerns = null,
        CancellationToken cancellationToken = default)
    {
        var result = await runs.SubmitForReviewAsync(RunScope.From(request.User), summary, criteria_mapping, open_concerns, cancellationToken);
        return result.Accepted ? result.Message : throw new McpException(result.Message);
    }

    [McpServerTool(Name = "propose_plan")]
    [Description("Proposes your plan to a human before you implement it. The human reads the summary first and opens the details only when needed, so keep it short. Returns approve, or revise with notes on what to change." + WaitNote)]
    public async Task<PlanResult> ProposePlan(
        RequestContext<CallToolRequestParams> request,
        IProgress<ProgressNotificationValue> progress,
        [Description("The plan in one sentence (at most 160 characters): what you will change and how.")] string summary,
        [Description("1 to 6 steps in order, one line each (at most 120 characters).")] IReadOnlyList<string> steps,
        [Description("At most 3 risks a human should know before approving (160 characters each).")] IReadOnlyList<string>? risks = null,
        [Description("Paths of the files you expect to create or change.")] IReadOnlyList<string>? files_expected = null,
        CancellationToken cancellationToken = default)
    {
        var (payload, error) = AttentionRules.Plan(summary, steps, risks, files_expected);
        var answer = await RaiseAndWaitAsync(request, progress, AttentionKind.PlanApproval, payload ?? throw new McpException(error!), cancellationToken);
        return new PlanResult(answer.Decision ?? PlanDecision.Revise, answer.Text);
    }

    [McpServerTool(Name = "ask_human")]
    [Description("Asks a human a question instead of guessing, and returns the answer: the text of the option they picked, or their own words. The human sees the question and one button per option; the context only on request." + WaitNote)]
    public async Task<AskResult> AskHuman(
        RequestContext<CallToolRequestParams> request,
        IProgress<ProgressNotificationValue> progress,
        [Description("One short question (at most 200 characters).")] string question,
        [Description("2 to 4 possible answers, each at most 80 characters. The human may still answer in their own words.")] IReadOnlyList<string> options,
        [Description("The option you would choose, exactly as written in options.")] string recommended,
        [Description("Why you ask: what you found and why you would choose the recommended option (at most 2000 characters).")] string? context = null,
        CancellationToken cancellationToken = default)
    {
        var (payload, error) = AttentionRules.Question(question, context, options, recommended);
        var answer = await RaiseAndWaitAsync(request, progress, AttentionKind.Question, payload ?? throw new McpException(error!), cancellationToken);
        return new AskResult(answer.Text ?? "");
    }

    [McpServerTool(Name = "get_review_comments", ReadOnly = true)]
    [Description("Returns the open review threads with comments a reviewer sent to you, and your replies so far. Fix what they ask for and answer each thread with reply_to_thread.")]
    public async Task<ReviewCommentsResult> GetReviewComments(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var threads = await runs.GetReviewCommentsAsync(RunScope.From(request.User), cancellationToken);
        return new ReviewCommentsResult(threads, threads.Count == 0 ? "There are no review comments for you." : null);
    }

    [McpServerTool(Name = "reply_to_thread")]
    [Description("Answers a review thread (thread_id from get_review_comments): what you changed, or why you did not. Does not wait.")]
    public async Task<string> ReplyToThread(
        RequestContext<CallToolRequestParams> request,
        [Description("The thread to answer.")] string thread_id,
        [Description("Your reply (markdown).")] string body,
        CancellationToken cancellationToken = default)
    {
        var (text, error) = Core.Reviews.ThreadRules.Body(body);
        if (text is null)
        {
            throw new McpException(error!);
        }
        var reply = Guid.TryParse(thread_id, out var threadId)
            ? await runs.ReplyToThreadAsync(RunScope.From(request.User), threadId, text, cancellationToken)
            : null;
        return reply ?? throw new McpException($"There is no review thread '{thread_id}' for you. Call get_review_comments for the open threads.");
    }

    [McpServerTool(Name = "get_preview_spec", ReadOnly = true)]
    [Description("Returns the format of tikai-preview.yml, the file at the repository root that tells Tikai how to start your change as a preview for the reviewer: keys, rules and examples (markdown). Read it before you write or change that file.")]
    public string GetPreviewSpec() => runs.PreviewSpec();

    [McpServerTool(Name = "check_preview_definition", ReadOnly = true)]
    [Description("Checks the text of your tikai-preview.yml with the parser the hub uses to start previews. Returns result \"valid\", or \"invalid\" with every error (key, line, message). It does not start a preview, so it cannot tell whether the app comes up. Call it before submit_for_review whenever you changed the file.")]
    public PreviewCheckResult CheckPreviewDefinition([Description("The whole text of tikai-preview.yml.")] string content) =>
        runs.CheckPreviewDefinition(content ?? throw new McpException("content: required (the whole text of tikai-preview.yml)."));

    private async Task<AttentionAnswer> RaiseAndWaitAsync<TPayload>(
        RequestContext<CallToolRequestParams> request,
        IProgress<ProgressNotificationValue> progress,
        AttentionKind kind,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        var run = RunScope.From(request.User);
        var item = await runs.RaiseAsync(run, kind, payload, cancellationToken);
        LogRaised(kind, item.ItemId, run.RunId, item.Merged, item.Answer is not null);
        if (item.Answer is { } stored)
        {
            // Asked again after the answer was given (a dropped call, a hub restart): it reaches the agent now.
            await runs.DeliveredAsync(run, item.ItemId, cancellationToken);
            return stored;
        }

        var started = timeProvider.GetTimestamp();
        var wait = waiter.WaitAsync(item.ItemId, cancellationToken);
        var interval = options.Value.HeartbeatInterval;
        for (var beat = 1; ; beat++)
        {
            var tick = Task.Delay(interval, timeProvider, cancellationToken);
            if (await Task.WhenAny(wait, tick) == wait)
            {
                var answer = await wait;
                var elapsed = timeProvider.GetElapsedTime(started);
                LogAnswered(item.ItemId, elapsed);
                // Only while the call is still there: a cancelled call leaves the answer undelivered for a resume.
                await runs.DeliveredAsync(run, item.ItemId, cancellationToken);
                return answer;
            }
            await tick;
            // Keeps Claude Code's idle timer from ending the call, and shows the hub is alive.
            progress.Report(new ProgressNotificationValue { Progress = beat, Message = $"Waiting for a human ({timeProvider.GetElapsedTime(started):hh\\:mm\\:ss})" });
            // A missed notification costs at most one interval.
            await waiter.RecheckAsync(item.ItemId, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Kind} item {ItemId} for run {RunId} (merged: {Merged}, already answered: {Answered})")]
    private partial void LogRaised(AttentionKind kind, Guid itemId, Guid runId, bool merged, bool answered);

    [LoggerMessage(Level = LogLevel.Information, Message = "Item {ItemId} answered after {Elapsed}")]
    private partial void LogAnswered(Guid itemId, TimeSpan elapsed);
}

public sealed record AskResult(string Answer);

/// <param name="Note">Set when there is nothing to do.</param>
public sealed record ReviewCommentsResult(IReadOnlyList<ReviewThreadResult> Threads, string? Note);

/// <param name="Notes">What to change (revise), or remarks with an approval.</param>
public sealed record PlanResult(PlanDecision Decision, string? Notes);
