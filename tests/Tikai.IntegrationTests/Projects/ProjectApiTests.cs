using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tikai.Api.Projects;
using Tikai.Data;
using Tikai.IntegrationTests.Auth;

namespace Tikai.IntegrationTests.Projects;

public sealed class ProjectApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TikaiApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await TikaiApiFactory.CreateAsync(postgres);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Project_endpoints_need_a_login()
    {
        using var anonymous = new BrowserClient(_factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        await anonymous.RefreshAntiforgeryAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/projects", NewProject())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/projects/hub/repositories")).StatusCode);
    }

    [Fact]
    public async Task Projects_can_be_created_listed_renamed_archived_restored_and_deleted()
    {
        using var browser = await AdminAsync();

        using var created = await browser.PostAsync("/api/projects", NewProject());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/projects/hub", created.Headers.Location?.OriginalString);
        var project = (await created.Content.ReadFromJsonAsync<ProjectInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
        Assert.Equal(("Tikai Hub", "hub", "HUB"), (project.Name, project.Slug, project.Key));

        Assert.Equal([project], await browser.GetJsonAsync<List<ProjectInfo>>("/api/projects"));
        using (var renamed = await browser.PutAsync("/api/projects/hub", new { name = "  The Hub  " }))
        {
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        }
        Assert.Equal("The Hub", (await browser.GetJsonAsync<ProjectInfo>("/api/projects/hub")).Name);

        // Archived: still listed (the web hides it), marked, and restorable.
        using (var archived = await browser.PostAsync("/api/projects/hub/archive"))
        {
            Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
            Assert.NotNull((await archived.Content.ReadFromJsonAsync<ProjectInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!.ArchivedAt);
        }
        Assert.NotNull(Assert.Single(await browser.GetJsonAsync<List<ProjectInfo>>("/api/projects")).ArchivedAt);
        using (var refused = await browser.PostAsync("/api/projects/hub/repositories", new { name = "api" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }
        (await browser.PostAsync("/api/projects/hub/archive")).EnsureSuccessStatusCode(); // twice is fine
        using (var restored = await browser.PostAsync("/api/projects/hub/restore"))
        {
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            Assert.Null((await restored.Content.ReadFromJsonAsync<ProjectInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!.ArchivedAt);
        }

        // Deleting asks for the project's name.
        using (var wrong = await browser.PostAsync("/api/projects/hub/delete", new { name = "hub" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/projects/hub")).StatusCode);
        await browser.DeleteProjectAsync("hub");
        Assert.Empty(await browser.GetJsonAsync<List<ProjectInfo>>("/api/projects"));
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync("/api/projects/hub/archive")).StatusCode);
    }

    [Fact]
    public async Task Invalid_and_duplicate_projects_are_rejected()
    {
        using var browser = await AdminAsync();

        using var invalid = await browser.PostAsync("/api/projects", new { name = " ", slug = "Not A Slug", key = "hub" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var body = await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"name\"", body, StringComparison.Ordinal);
        Assert.Contains("\"slug\"", body, StringComparison.Ordinal);
        Assert.Contains("\"key\"", body, StringComparison.Ordinal);

        (await browser.PostAsync("/api/projects", NewProject())).EnsureSuccessStatusCode();
        using var sameSlug = await browser.PostAsync("/api/projects", NewProject(key: "OTHER"));
        using var sameKey = await browser.PostAsync("/api/projects", NewProject(slug: "other"));

        Assert.Equal(HttpStatusCode.Conflict, sameSlug.StatusCode);
        Assert.Contains("slug already exists", await sameSlug.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, sameKey.StatusCode);
        Assert.Contains("key already exists", await sameKey.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_is_deleted_with_its_repositories_tickets_and_providers_and_others_stay()
    {
        using var browser = await AdminAsync();
        (await browser.PostAsync("/api/projects", NewProject())).EnsureSuccessStatusCode();
        (await browser.PostAsync("/api/projects", new { name = "Other", slug = "other", key = "OTH" })).EnsureSuccessStatusCode();
        foreach (var slug in new[] { "hub", "other" })
        {
            (await browser.PostAsync($"/api/projects/{slug}/repositories", new { name = "api" })).EnsureSuccessStatusCode();
            (await browser.PostAsync($"/api/projects/{slug}/tickets", new { title = "A ticket", description = "", criteria = new[] { "Works" } })).EnsureSuccessStatusCode();
            (await browser.PostAsync($"/api/projects/{slug}/llm-providers", new { name = "Team", defaultModel = "claude-haiku-5-5", apiKey = "team-key-0123456789" })).EnsureSuccessStatusCode();
        }
        string hubMirrors, otherMirrors;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
            var paths = scope.ServiceProvider.GetRequiredService<MirrorPaths>();
            var ids = await db.Projects.ToDictionaryAsync(p => p.Slug, p => p.Id, TestContext.Current.CancellationToken);
            (hubMirrors, otherMirrors) = (Path.Combine(paths.Root, ids["hub"].ToString("D")), Path.Combine(paths.Root, ids["other"].ToString("D")));
        }
        Assert.True(Directory.Exists(hubMirrors));

        await browser.DeleteProjectAsync("hub");

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
            Assert.Equal(["other"], await db.Projects.Select(p => p.Slug).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.Repositories.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.Tickets.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.AcceptanceCriteria.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.LlmProviders.CountAsync(TestContext.Current.CancellationToken));
        }
        // The mirrors go through the git queue.
        var until = DateTimeOffset.UtcNow.AddSeconds(30);
        while (Directory.Exists(hubMirrors) && DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.False(Directory.Exists(hubMirrors));
        Assert.True(Directory.Exists(otherMirrors));
        // The slug and the key are free again.
        Assert.Equal(HttpStatusCode.Created, (await browser.PostAsync("/api/projects", NewProject())).StatusCode);
    }

    private async Task<BrowserClient> AdminAsync()
    {
        var browser = new BrowserClient(_factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        await browser.RefreshAntiforgeryAsync();
        await browser.SignUpAdminAsync();
        return browser;
    }

    private static object NewProject(string slug = "hub", string key = "HUB") => new { name = "Tikai Hub", slug, key };
}
