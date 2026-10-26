using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Tikai.Api.Projects;
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
    public async Task Projects_can_be_created_listed_renamed_and_deleted()
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

        Assert.Equal(HttpStatusCode.NoContent, (await browser.DeleteAsync("/api/projects/hub")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/api/projects/hub")).StatusCode);
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
    public async Task A_project_with_repositories_cannot_be_deleted()
    {
        using var browser = await AdminAsync();
        (await browser.PostAsync("/api/projects", NewProject())).EnsureSuccessStatusCode();
        (await browser.PostAsync("/api/projects/hub/repositories", new { name = "api" })).EnsureSuccessStatusCode();

        using var response = await browser.DeleteAsync("/api/projects/hub");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("still has repositories", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
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
