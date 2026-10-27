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
    [Description("Submits your work for human review with a self-report. Commit your changes first. Does not wait for the review.")]
    public async Task<string> SubmitForReview(
        RequestContext<CallToolRequestParams> request,
        [Description("What you changed and why.")] string summary,
        [Description("For each acceptance criterion (id from get_ticket): covered, partial or missing, with a note on where or why.")]
        IReadOnlyList<CriterionMapping>? criteria_mapping = null,
        [Description("What you are unsure about or deliberately left out.")] IReadOnlyList<string>? open_concerns = null,
        CancellationToken cancellationToken = default)
    {
        if (await runs.SubmitForReviewAsync(RunScope.From(request.User), summary, criteria_mapping, open_concerns, cancellationToken) is { } error)
        {
            throw new McpException(error);
        }
        return "Submitted for review.";
    }

    [McpServerTool(Name = "propose_plan")]
    [Description("Proposes your plan to a human before you implement it. Returns approve, or revise with notes on what to change." + WaitNote)]
    public async Task<PlanResult> ProposePlan(
        RequestContext<CallToolRequestParams> request,
        IProgress<ProgressNotificationValue> progress,
        [Description("The steps you will take, in order.")] IReadOnlyList<string> steps,
        [Description("What could go wrong or needs care.")] IReadOnlyList<string>? risks = null,
        [Description("Files you expect to create or change.")] IReadOnlyList<string>? files_expected = null,
        CancellationToken cancellationToken = default)
    {
        var (payload, error) = AttentionRules.Plan(steps, risks, files_expected);
        var answer = await RaiseAndWaitAsync(request, progress, AttentionKind.PlanApproval, payload ?? throw new McpException(error!), cancellationToken);
        return new PlanResult(answer.Decision ?? PlanDecision.Revise, answer.Text);
    }

    [McpServerTool(Name = "ask_human")]
    [Description("Asks a human a question instead of guessing, and returns the answer." + WaitNote)]
    public async Task<AskResult> AskHuman(
        RequestContext<CallToolRequestParams> request,
        IProgress<ProgressNotificationValue> progress,
        [Description("The question.")] string question,
        [Description("What the human needs to know to answer: what you are doing, what you found, what you would choose.")] string? context = null,
        [Description("Possible answers, if there are a few clear choices. The human may still answer in their own words.")] IReadOnlyList<string>? options = null,
        CancellationToken cancellationToken = default)
    {
        var (payload, error) = AttentionRules.Question(question, context, options);
        var answer = await RaiseAndWaitAsync(request, progress, AttentionKind.Question, payload ?? throw new McpException(error!), cancellationToken);
        return new AskResult(answer.Text ?? "");
    }

    [McpServerTool(Name = "get_review_comments", ReadOnly = true)]
    [Description("Returns the open review comments sent to you from the last review round, to fix and answer with reply_to_thread.")]
    public static string GetReviewComments() => "There are no review comments for you.";

    [McpServerTool(Name = "reply_to_thread")]
    [Description("Answers a review comment thread (thread_id from get_review_comments).")]
    public static string ReplyToThread(
        [Description("The thread to answer.")] string thread_id,
        [Description("Your reply (markdown).")] string body) =>
        throw new McpException($"There is no review thread '{thread_id}'. Call get_review_comments for the open threads.");

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

/// <param name="Notes">What to change (revise), or remarks with an approval.</param>
public sealed record PlanResult(PlanDecision Decision, string? Notes);
