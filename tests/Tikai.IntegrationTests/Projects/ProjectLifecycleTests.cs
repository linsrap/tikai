using System.Net;
using System.Net.Http.Json;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tikai.Api.Attention;
using Tikai.Api.Projects;
using Tikai.Api.Reviews;
using Tikai.Core.Reviews;
using Tikai.Core.Runs;
using Tikai.Data;
using Tikai.IntegrationTests.Auth;
using Tikai.IntegrationTests.Git;
using Tikai.IntegrationTests.Runs;

namespace Tikai.IntegrationTests.Projects;

// Projects with history, with real containers: a repository that runs refer to, archiving while an agent waits, and
// deleting a project with a submitted change, its run directory, its mirror and a remote that must stay as it is.
public sealed class ProjectLifecycleTests(PostgresFixture postgres, AgentHost agents) : IClassFixture<AgentHost>, IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task A_repository_with_history_is_refused_and_archiving_cancels_the_waiting_agent_until_the_project_is_restored()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("wait");
        var slug = hub.Slugs[ticket.Id];
        var run = await hub.WaitForAsync((await hub.AssignAsync(ticket)).Id, r => r is { State: AgentRunState.Blocked, Waiting.Count: 1 });
        Assert.Contains(await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"), i => i.RunId == run.Id);

        // The repository has a run: a clear 409, not a foreign-key error.
        var repository = Assert.Single(await hub.Browser.GetJsonAsync<List<RepositoryInfo>>($"/api/projects/{slug}/repositories"));
        using (var refused = await hub.Browser.DeleteAsync($"/api/projects/{slug}/repositories/{repository.Id}"))
        {
            var body = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(refused.StatusCode == HttpStatusCode.Conflict, body);
            Assert.Contains("The repository has history", body, StringComparison.Ordinal);
            Assert.Contains("Archive the project", body, StringComparison.Ordinal);
        }
        Assert.Single(await hub.Browser.GetJsonAsync<List<RepositoryInfo>>($"/api/projects/{slug}/repositories"));

        // Archive: the waiting agent is cancelled, its question leaves the inbox, nothing new starts.
        using (var archived = await hub.Browser.PostAsync($"/api/projects/{slug}/archive"))
        {
            Assert.True(archived.StatusCode == HttpStatusCode.OK, await archived.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        await hub.WaitForAsync(run.Id, r => r.State == AgentRunState.Cancelled);
        Assert.DoesNotContain(await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"), i => i.RunId == run.Id);
        using (var start = await hub.Browser.PostAsync($"/api/projects/{slug}/tickets/{ticket.Number}/runs", new { }))
        {
            var body = await start.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(start.StatusCode == HttpStatusCode.Conflict, body);
            Assert.Contains("The project is archived", body, StringComparison.Ordinal);
        }
        // The history is kept and readable.
        Assert.Equal(AgentRunState.Cancelled, Assert.Single(await hub.Browser.GetJsonAsync<List<Api.Runs.RunInfo>>($"/api/projects/{slug}/tickets/{ticket.Number}/runs")).State);

        // Restored: work goes on.
        using (var restored = await hub.Browser.PostAsync($"/api/projects/{slug}/restore"))
        {
            Assert.True(restored.StatusCode == HttpStatusCode.OK, await restored.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        var again = await hub.AssignAsync(ticket);
        using (var cancel = await hub.Browser.PostAsync($"/api/runs/{again.Id}/cancel", new { }))
        {
            Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        }
        await hub.WaitForAsync(again.Id, r => r.State == AgentRunState.Cancelled);
    }

    [Fact]
    public async Task Deleting_a_project_removes_its_rows_run_directory_and_mirror_and_leaves_the_remote_alone()
    {
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("ok");
        var slug = hub.Slugs[ticket.Id];
        var mirror = await hub.MirrorAsync(ticket);
        await TestGit.CommitAsync(mirror, "main", "Seed", ("seed.txt", "seed\n"));
        var remote = await TestGit.InitBareAsync(_temp.Combine("remote.git"));
        var repository = Assert.Single(await hub.Browser.GetJsonAsync<List<RepositoryInfo>>($"/api/projects/{slug}/repositories"));
        using (var connected = await hub.Browser.PutAsync($"/api/projects/{slug}/repositories/{repository.Id}/remote", new { remoteUrl = TestGit.FileUrl(remote) }))
        {
            Assert.True(connected.StatusCode == HttpStatusCode.OK, await connected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        var remoteMain = await TestGit.TryResolveAsync(remote, "refs/heads/main");
        Assert.NotNull(remoteMain);
        // Another project that must not be touched.
        var other = await hub.CreateTicketAsync("ok", "OTH");

        // A submitted change: run in review (not ended, its directory is on the volume), change set, round, inbox item.
        var run = await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id);
        Assert.Equal(AgentRunState.InReview, run.State);
        Assert.Equal(ChangeSetStatus.Open, (await hub.Browser.GetJsonAsync<ChangeSetInfo>($"/api/change-sets/{run.ChangeSetId}")).Status);
        Assert.True(await RunDirectoryExistsAsync(run.Id));

        using (var wrong = await hub.Browser.PostAsync($"/api/projects/{slug}/delete", new { name = "something else" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }
        using (var accepted = await hub.Browser.PostAsync($"/api/projects/{slug}/delete", new { name = slug }))
        {
            Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True((await accepted.Content.ReadFromJsonAsync<ProjectInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!.Deleting);
        }
        // While it goes, nothing starts in it.
        using (var late = await hub.Browser.PostAsync($"/api/projects/{slug}/tickets", new { title = "Too late", description = "", criteria = Array.Empty<string>() }))
        {
            Assert.True(late.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound, late.StatusCode.ToString());
        }
        await hub.Browser.DeleteProjectAsync(slug, seconds: 120);

        // Rows: only the other project's are left.
        await using (var scope = hub.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
            var ct = TestContext.Current.CancellationToken;
            Assert.Equal([hub.Slugs[other.Id]], await db.Projects.Select(p => p.Slug).ToListAsync(ct));
            Assert.Equal([other.Id], await db.Tickets.Select(t => t.Id).ToListAsync(ct));
            Assert.Equal(0, await db.AgentRuns.CountAsync(ct));
            Assert.Equal(0, await db.RunEvents.CountAsync(ct));
            Assert.Equal(0, await db.AttentionItems.CountAsync(ct));
            Assert.Equal(0, await db.ChangeSets.CountAsync(ct));
            Assert.Equal(0, await db.ReviewRounds.CountAsync(ct));
            Assert.Equal(1, await db.Repositories.CountAsync(ct));
            Assert.Equal(1, await db.LlmProviders.CountAsync(ct));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await hub.Browser.GetAsync($"/api/runs/{run.Id}")).StatusCode);
        Assert.DoesNotContain(await hub.Browser.GetJsonAsync<List<AttentionItemInfo>>("/api/attention-items"), i => i.RunId == run.Id);

        // Outside the database: the run's directory and the mirror are gone, the remote is as it was.
        Assert.False(await RunDirectoryExistsAsync(run.Id));
        var until = DateTimeOffset.UtcNow.AddSeconds(30);
        while (Directory.Exists(mirror) && DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.False(Directory.Exists(mirror));
        Assert.True(Directory.Exists(await hub.MirrorAsync(other)));
        Assert.Equal(remoteMain, await TestGit.TryResolveAsync(remote, "refs/heads/main"));
        Assert.Equal("", await TestGit.RunAsync(remote, "for-each-ref", "refs/heads/tikai/"));
    }

    /// <summary>Looks into the runs volume with a throwaway container (the hub itself never mounts it).</summary>
    private async Task<bool> RunDirectoryExistsAsync(Guid runId)
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = await agents.Docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = AgentHost.StubImage,
            Entrypoint = ["test", "-d", $"/runs/{runId:D}"],
            HostConfig = new HostConfig { Mounts = [new Mount { Type = "volume", Source = agents.RunsVolume, Target = "/runs" }], NetworkMode = "none" },
        }, ct);
        try
        {
            await agents.Docker.Containers.StartContainerAsync(probe.ID, new ContainerStartParameters(), ct);
            return (await agents.Docker.Containers.WaitContainerAsync(probe.ID, ct)).StatusCode == 0;
        }
        finally
        {
            await agents.Docker.Containers.RemoveContainerAsync(probe.ID, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
        }
    }
}
