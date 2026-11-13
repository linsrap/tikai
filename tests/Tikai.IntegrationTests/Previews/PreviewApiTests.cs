using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tikai.Api.Previews;
using Tikai.Core.Previews;
using Tikai.Core.Runs;
using Tikai.Data;
using Tikai.IntegrationTests.Auth;
using Tikai.IntegrationTests.Git;
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

        // The agent checked its file before it submitted: the tool calls went through, with the hub's answers.
        var runId = await RunIdAsync(hub, changeSetId);
        var events = await hub.EventsAsync(runId);
        var calls = events
            .Where(e => e.Kind == RunEventKind.ToolCall && e.Payload.GetProperty("name").GetString() == "mcp__tikai__check_preview_definition")
            .Select(e => e.Payload.GetProperty("id").GetString())
            .ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, id => Assert.Contains(events, e =>
            e.Kind == RunEventKind.ToolResult && e.Payload.GetProperty("tool_use_id").GetString() == id && !e.Payload.GetProperty("is_error").GetBoolean()));
        var message = (await hub.MessagesAsync(runId)).Single(t => t.StartsWith("STUB_PREVIEW_CHECK ", StringComparison.Ordinal));
        var checks = JsonDocument.Parse(message["STUB_PREVIEW_CHECK ".Length..]).RootElement;
        Assert.Equal("valid", checks.GetProperty("check").GetProperty("result").GetString());
        Assert.Equal("invalid", checks.GetProperty("draft").GetProperty("result").GetString());
        Assert.Equal(
            [("app.ports", 3), ("app.command", 1), ("app.port", 1)],
            checks.GetProperty("draft").GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("key").GetString()!, e.GetProperty("line").GetInt32())));

        var before = await GetAsync(hub, changeSetId);
        Assert.True(before.Available);
        Assert.Equal((1, true, 0), (before.Definition.RoundNo, before.Definition.Found, before.Definition.Errors.Count));
        Assert.Null(before.Preview);

        var first = await StartAsync(hub, changeSetId);
        Assert.Equal(PreviewState.Preparing, first.State);
        var running = await WaitForAsync(hub, changeSetId, p => p.Id == first.Id && p.State == PreviewState.Running);
        Assert.NotNull(running.RunningAt);

        // The note follows the state change.
        await WaitForLogAsync(hub, first.Id, l => l.Source == "hub" && l.Text == "Preview running");
        var log = await LogAsync(hub, first.Id);
        Assert.Contains(log, l => l.Source == "hub" && l.Text.StartsWith("Setup step 1: mkdir", StringComparison.Ordinal));
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
    public async Task A_preview_with_two_apps_runs_once_both_answer_and_fails_when_one_stops()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var changeSetId = await SubmittedAsync(hub, "preview-apps");

        var started = await StartAsync(hub, changeSetId);
        Assert.Equal([("web", "/", "app web"), ("api", "/api", "app api")], started.Apps.Select(a => (a.Name, a.Path, a.LogSource)));
        await WaitForAsync(hub, changeSetId, p => p.Id == started.Id && p.State == PreviewState.Running);
        var row = await PreviewRowAsync(hub, started.Id);
        var apps = PreviewUpstreams.FromJson(row.Upstreams);
        Assert.Equal([("web", "/"), ("api", "/api")], apps.Select(a => (a.Name, a.Path)));
        await WaitForLogAsync(hub, started.Id, l => l.Source == "hub" && l.Text.StartsWith("The app api started on /api", StringComparison.Ordinal));

        // One app stops: the whole preview fails and is torn down.
        await agents.Docker.Containers.KillContainerAsync(apps[1].ContainerId, new Docker.DotNet.Models.ContainerKillParameters(), TestContext.Current.CancellationToken);
        var failed = await WaitForStateAsync(hub, started.Id, PreviewState.Failed);
        Assert.Equal("The app api stopped.", failed.Error);
        await WaitForCleanedAsync(hub, started.Id);
        Assert.DoesNotContain(await RuntimeAsync(hub), r => r.PreviewId == started.Id);
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

    [Fact]
    public async Task The_default_branch_is_previewed_from_the_project_and_stops_when_the_branch_moves()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ct = TestContext.Current.CancellationToken;
        var ticket = await hub.CreateTicketAsync("preview");
        var slug = hub.Slugs[ticket.Id];
        var mirror = await hub.MirrorAsync(ticket);
        var repositoryId = (await hub.Browser.GetJsonAsync<List<Tikai.Api.Projects.RepositoryInfo>>($"/api/projects/{slug}/repositories")).Single().Id;
        var path = $"/api/projects/{slug}/repositories/{repositoryId}";

        // Before the merge, main has no tikai-preview.yml.
        var empty = await hub.Browser.GetJsonAsync<RepositoryPreviewInfo>(path + "/preview");
        Assert.Equal(("main", false, true), (empty.Branch, empty.Found, empty.Available));
        using (var refused = await hub.Browser.PostAsync(path + "/previews", new { }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("main at ", await refused.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

        // The agent's app is merged into main; now main can be previewed.
        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);
        (await hub.Browser.PostAsync($"/api/change-sets/{run.ChangeSetId}/verdict", new { verdict = "approve" })).EnsureSuccessStatusCode();
        await hub.WaitForAsync(run.Id, r => r.State == AgentRunState.Merged);
        var head = (await TestGit.TryResolveAsync(mirror, "refs/heads/main"))!;
        var info = await hub.Browser.GetJsonAsync<RepositoryPreviewInfo>(path + "/preview");
        Assert.Equal((head, true, 0), (info.HeadSha, info.Found, info.Errors.Count));

        using (var response = await hub.Browser.PostAsync(path + "/previews", new { }))
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
            var started = (await response.Content.ReadFromJsonAsync<PreviewInfo>(TikaiJson.Options, ct))!;
            Assert.Equal(("main", head, (Guid?)null, (int?)null, repositoryId), (started.Branch, started.HeadSha, started.ChangeSetId, started.RoundNo, started.RepositoryId));
        }
        var preview = (await hub.Browser.GetJsonAsync<RepositoryPreviewInfo>(path + "/preview")).Preview!;
        await WaitForStateAsync(hub, preview.Id, PreviewState.Running);
        // The project's own repository list cannot drop it while it runs.
        using (var delete = await hub.Browser.DeleteAsync(path))
        {
            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        }

        // Someone moves main (a merge, a fetch): the reconciler stops the preview, which would show an old state.
        var tree = await TestGit.RunAsync(mirror, "rev-parse", head + "^{tree}");
        var next = await TestGit.RunAsync(mirror, "commit-tree", tree, "-p", head, "-m", "Next");
        await TestGit.RunAsync(mirror, "update-ref", "refs/heads/main", next);
        var stopped = await WaitForStateAsync(hub, preview.Id, PreviewState.Stopped);
        Assert.Equal($"main moved to {next[..12]}. Start a new preview to see it.", stopped.StopReason);
        await WaitForCleanedAsync(hub, preview.Id);
        Assert.DoesNotContain(await RuntimeAsync(hub), r => r.PreviewId == preview.Id);

        // An archived project starts nothing.
        (await hub.Browser.PostAsync($"/api/projects/{slug}/archive", new { })).EnsureSuccessStatusCode();
        var archived = await hub.Browser.GetJsonAsync<RepositoryPreviewInfo>(path + "/preview");
        Assert.False(archived.Available);
        using (var closed = await hub.Browser.PostAsync(path + "/previews", new { }))
        {
            Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
        }
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

    private static async Task<Preview> WaitForStateAsync(AgentHub hub, Guid previewId, PreviewState state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var row = await PreviewRowAsync(hub, previewId);
            if (row.State == state)
            {
                return row;
            }
            Assert.True(DateTime.UtcNow < deadline, $"Preview is {row.State} ({row.Error})");
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<Guid> RunIdAsync(AgentHub hub, Guid changeSetId)
    {
        await using var scope = hub.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TikaiDbContext>().ChangeSets.Where(c => c.Id == changeSetId).Select(c => c.RunId).SingleAsync();
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
