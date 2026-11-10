using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tikai.Api.Previews;
using Tikai.Core.Previews;
using Tikai.Data;
using Tikai.IntegrationTests.Auth;
using Tikai.IntegrationTests.Runs;

namespace Tikai.IntegrationTests.Previews;

/// <summary>
/// Previews through the API with real containers: the stub agent submits a tiny node app with a tikai-preview.yml
/// (scenario "preview"), and the hub clones, sets up and starts it.
/// </summary>
public sealed class PreviewApiTests(PostgresFixture postgres, AgentHost agents) : IClassFixture<AgentHost>
{
    [Fact]
    public async Task A_round_is_previewed_replaced_by_a_new_start_and_stopped_with_everything_removed()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var changeSetId = await SubmittedAsync(hub, "preview");

        var before = await GetAsync(hub, changeSetId);
        Assert.True(before.Available);
        Assert.Equal((1, true, 0), (before.Definition.RoundNo, before.Definition.Found, before.Definition.Errors.Count));
        Assert.Null(before.Preview);

        var first = await StartAsync(hub, changeSetId);
        Assert.Equal(PreviewState.Preparing, first.State);
        var running = await WaitForAsync(hub, changeSetId, p => p.Id == first.Id && p.State == PreviewState.Running);
        Assert.NotNull(running.RunningAt);

        var log = await LogAsync(hub, first.Id);
        Assert.Contains(log, l => l.Source == "hub" && l.Text.StartsWith("Setup step 1: mkdir", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Source == "hub" && l.Text == "Preview running");
        Assert.Equal(log.Select(l => l.Seq), Enumerable.Range(1, log.Count).Select(i => (long)i));
        // The app's own output follows, with the generated value removed.
        var appLine = await WaitForLogAsync(hub, first.Id, l => l.Source == "app");
        Assert.Equal("stub app listening with [redacted]", appLine.Text);

        // A new start replaces the change set's preview.
        var second = await StartAsync(hub, changeSetId);
        var replaced = await PreviewRowAsync(hub, first.Id);
        Assert.Equal((PreviewState.Stopped, "Replaced by a new preview."), (replaced.State, replaced.StopReason));
        await WaitForAsync(hub, changeSetId, p => p.Id == second.Id && p.State == PreviewState.Running);
        await WaitForCleanedAsync(hub, first.Id);

        using (var stop = await hub.Browser.PostAsync($"/api/previews/{second.Id}/stop", new { }))
        {
            stop.EnsureSuccessStatusCode();
            var stopped = (await stop.Content.ReadFromJsonAsync<PreviewInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
            Assert.Equal(PreviewState.Stopped, stopped.State);
            Assert.StartsWith("Stopped by ", stopped.StopReason, StringComparison.Ordinal);
        }
        await WaitForCleanedAsync(hub, second.Id);
        Assert.DoesNotContain(await RuntimeAsync(hub), r => r.PreviewId == first.Id || r.PreviewId == second.Id);
    }

    [Fact]
    public async Task A_round_without_a_definition_says_so_and_cannot_be_started()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var changeSetId = await SubmittedAsync(hub, "ok");

        var info = await GetAsync(hub, changeSetId);
        Assert.False(info.Definition.Found);

        using var response = await hub.Browser.PostAsync($"/api/change-sets/{changeSetId}/previews", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("has no tikai-preview.yml", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_installation_limit_holds_and_a_rejected_change_set_stops_its_preview()
    {
        await using var hub = await agents.StartHubAsync(postgres, new Dictionary<string, string> { ["Tikai:Previews:MaxRunning"] = "1" });
        var first = await SubmittedAsync(hub, "preview");
        var second = await SubmittedAsync(hub, "preview", "TWO");

        var preview = await StartAsync(hub, first);
        using (var refused = await hub.Browser.PostAsync($"/api/change-sets/{second}/previews", new { }))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("At most 1 previews", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        (await hub.Browser.PostAsync($"/api/change-sets/{first}/verdict", new { verdict = "reject" })).EnsureSuccessStatusCode();
        var stopped = await PreviewRowAsync(hub, preview.Id);
        Assert.Equal((PreviewState.Stopped, PreviewStops.ChangeSetEnded), (stopped.State, stopped.StopReason));
        await WaitForCleanedAsync(hub, preview.Id);

        // The slot is free again.
        var next = await StartAsync(hub, second);
        Assert.Equal(PreviewState.Preparing, next.State);
        (await hub.Browser.PostAsync($"/api/previews/{next.Id}/stop", new { })).EnsureSuccessStatusCode();
        await WaitForCleanedAsync(hub, next.Id);
    }

    /// <summary>A ticket whose stub run submitted its work; returns the change set.</summary>
    private static async Task<Guid> SubmittedAsync(AgentHub hub, string scenario, string key = "HUB")
    {
        var ticket = await hub.CreateTicketAsync(scenario, key);
        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);
        return run.ChangeSetId ?? throw new InvalidOperationException($"The run ended {run.State} without a change set: {run.FailureReason}");
    }

    private static Task<ChangeSetPreviewInfo> GetAsync(AgentHub hub, Guid changeSetId) =>
        hub.Browser.GetJsonAsync<ChangeSetPreviewInfo>($"/api/change-sets/{changeSetId}/preview");

    private static async Task<PreviewInfo> StartAsync(AgentHub hub, Guid changeSetId)
    {
        using var response = await hub.Browser.PostAsync($"/api/change-sets/{changeSetId}/previews", new { });
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<PreviewInfo>(TikaiJson.Options))!;
    }

    private static Task<List<PreviewLogLineInfo>> LogAsync(AgentHub hub, Guid previewId) =>
        hub.Browser.GetJsonAsync<List<PreviewLogLineInfo>>($"/api/previews/{previewId}/log?limit=1000");

    private static async Task<PreviewInfo> WaitForAsync(AgentHub hub, Guid changeSetId, Func<PreviewInfo, bool> condition, int seconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var preview = (await GetAsync(hub, changeSetId)).Preview;
            if (preview is not null && condition(preview))
            {
                return preview;
            }
            if (DateTime.UtcNow > deadline || preview?.State == PreviewState.Failed)
            {
                var log = preview is null ? [] : await LogAsync(hub, preview.Id);
                Assert.Fail($"Preview is {preview?.State} ({preview?.Error}). Log:\n{string.Join("\n", log.Select(l => $"{l.Seq} [{l.Source}] {l.Text}"))}");
            }
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<PreviewLogLineInfo> WaitForLogAsync(AgentHub hub, Guid previewId, Func<PreviewLogLineInfo, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var log = await LogAsync(hub, previewId);
            if (log.FirstOrDefault(condition) is { } line)
            {
                return line;
            }
            Assert.True(DateTime.UtcNow < deadline, "Log:\n" + string.Join("\n", log.Select(l => $"[{l.Source}] {l.Text}")));
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<Preview> PreviewRowAsync(AgentHub hub, Guid previewId)
    {
        await using var scope = hub.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TikaiDbContext>().Previews.AsNoTracking().SingleAsync(p => p.Id == previewId);
    }

    private static async Task WaitForCleanedAsync(AgentHub hub, Guid previewId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while ((await PreviewRowAsync(hub, previewId)).CleanedAt is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "The preview's teardown did not finish.");
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<IReadOnlyList<Tikai.Orchestrator.PreviewResource>> RuntimeAsync(AgentHub hub) =>
        await hub.Factory.Services.GetRequiredService<Tikai.Orchestrator.IContainerRuntime>().ListPreviewsAsync(TestContext.Current.CancellationToken);
}
