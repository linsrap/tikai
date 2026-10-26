using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Tikai.Api.Projects;
using Tikai.Core.Projects;
using Tikai.IntegrationTests.Auth;
using Tikai.IntegrationTests.Git;

namespace Tikai.IntegrationTests.Projects;

public sealed class RepositoryApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Repos = "/api/projects/hub/repositories";
    private const string Token = "tok_0a1b2c3d4e5f";
    private readonly TempDirectory _remotes = new();
    private TikaiApiFactory _factory = null!;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
        _remotes.Dispose();
    }

    [Fact]
    public async Task A_local_repository_is_ready_at_once_with_a_main_branch()
    {
        using var browser = await StartAsync();

        using var response = await browser.PostAsync(Repos, new { name = "api" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var repository = (await response.Content.ReadFromJsonAsync<RepositoryInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
        Assert.Equal((RepositoryStatus.Ready, "main", null, CredentialKind.None), (repository.Status, repository.DefaultBranch, repository.RemoteUrl, repository.CredentialKind));
        var branch = Assert.Single(await browser.GetJsonAsync<List<BranchInfo>>($"{Repos}/{repository.Id}/branches"));
        Assert.Equal(("main", false), (branch.Name, branch.IsRemote));
        Assert.True(Directory.Exists(await MirrorPathAsync(browser, repository.Id)));
        Assert.Equal(HttpStatusCode.Conflict, (await browser.PostAsync($"{Repos}/{repository.Id}/fetch")).StatusCode);
    }

    [Fact]
    public async Task A_remote_repository_is_cloned_by_a_job()
    {
        using var browser = await StartAsync();
        var remote = await TestGit.InitBareAsync(_remotes.Combine("remote.git"), "trunk");
        var head = await TestGit.CommitAsync(remote, "trunk", "First", ("README.md", "hi\n"));
        await TestGit.CommitAsync(remote, "feature", "Feature", ("f.txt", "1\n"));

        using var response = await browser.PostAsync(Repos, new { name = "api", remoteUrl = TestGit.FileUrl(remote) });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<RepositoryInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
        Assert.Equal(RepositoryStatus.Cloning, created.Status);
        var ready = await WaitForAsync(browser, created.Id, r => r.Status != RepositoryStatus.Cloning);
        Assert.Equal((RepositoryStatus.Ready, "trunk", null), (ready.Status, ready.DefaultBranch, ready.LastError));
        Assert.NotNull(ready.LastFetchedAt);
        Assert.Equal(
            [new BranchInfo("trunk", head, false), new BranchInfo("feature", (await TestGit.TryResolveAsync(remote, "feature"))!, true), new BranchInfo("trunk", head, true)],
            await browser.GetJsonAsync<List<BranchInfo>>($"{Repos}/{created.Id}/branches"));
    }

    [Fact]
    public async Task The_token_is_write_only_and_stored_encrypted()
    {
        using var browser = await StartAsync();
        await using var server = await StartGitServerAsync();

        using var response = await browser.PostAsync(Repos, new
        {
            name = "api",
            remoteUrl = $"{server.BaseUrl}/remote.git",
            credential = new { kind = "https_token", token = Token },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<RepositoryInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
        var ready = await WaitForAsync(browser, created.Id, r => r.Status != RepositoryStatus.Cloning);
        Assert.Equal((RepositoryStatus.Ready, CredentialKind.HttpsToken), (ready.Status, ready.CredentialKind));
        foreach (var path in new[] { Repos, $"{Repos}/{created.Id}" })
        {
            Assert.DoesNotContain(Token, await (await browser.GetAsync(path)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var query = new NpgsqlCommand("SELECT encrypted_credential, credential_kind FROM repositories", connection);
        await using var row = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await row.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("https_token", row.GetString(1));
        Assert.DoesNotContain(Token, row.GetString(0), StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Token)), row.GetString(0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_clone_shows_the_git_error_and_can_be_retried_with_a_new_credential()
    {
        using var browser = await StartAsync();
        await using var server = await StartGitServerAsync();
        using var response = await browser.PostAsync(Repos, new
        {
            name = "api",
            remoteUrl = $"{server.BaseUrl}/remote.git",
            credential = new { kind = "https_token", token = "wrong" },
        });
        var created = (await response.Content.ReadFromJsonAsync<RepositoryInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;

        var failed = await WaitForAsync(browser, created.Id, r => r.Status != RepositoryStatus.Cloning);
        Assert.Equal(RepositoryStatus.Failed, failed.Status);
        Assert.Contains("The remote asked for a login", failed.LastError, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await browser.GetAsync($"{Repos}/{created.Id}/branches")).StatusCode);
        Assert.False(Directory.Exists(await MirrorPathAsync(browser, created.Id)));

        using (var update = await browser.PutAsync($"{Repos}/{created.Id}/credential", new { kind = "https_token", token = Token }))
        {
            Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        }
        using (var retry = await browser.PostAsync($"{Repos}/{created.Id}/fetch"))
        {
            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        }
        var ready = await WaitForAsync(browser, created.Id, r => r.Status == RepositoryStatus.Ready);
        Assert.Null(ready.LastError);
    }

    [Fact]
    public async Task The_scheduler_keeps_mirrors_current()
    {
        using var browser = await StartAsync(new Dictionary<string, string>
        {
            ["Tikai:Git:FetchInterval"] = "00:00:01",
            ["Tikai:Git:SchedulerInterval"] = "00:00:00.200",
        });
        var remote = await TestGit.InitBareAsync(_remotes.Combine("remote.git"));
        await TestGit.CommitAsync(remote, "main", "First", ("a.txt", "1\n"));
        using var response = await browser.PostAsync(Repos, new { name = "api", remoteUrl = TestGit.FileUrl(remote) });
        var created = (await response.Content.ReadFromJsonAsync<RepositoryInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
        var ready = await WaitForAsync(browser, created.Id, r => r.Status == RepositoryStatus.Ready);

        var second = await TestGit.CommitAsync(remote, "main", "Second", ("a.txt", "2\n"));

        await WaitUntilAsync(async () =>
            (await browser.GetJsonAsync<List<BranchInfo>>($"{Repos}/{created.Id}/branches")).Any(b => b is { Name: "main", IsRemote: false } && b.Sha == second));
        var fetched = await browser.GetJsonAsync<RepositoryInfo>($"{Repos}/{created.Id}");
        Assert.True(fetched.LastFetchedAt > ready.LastFetchedAt);
    }

    [Fact]
    public async Task Deleting_a_repository_removes_its_mirror()
    {
        using var browser = await StartAsync();
        using var response = await browser.PostAsync(Repos, new { name = "api" });
        var created = (await response.Content.ReadFromJsonAsync<RepositoryInfo>(TikaiJson.Options, TestContext.Current.CancellationToken))!;
        var path = await MirrorPathAsync(browser, created.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await browser.DeleteAsync($"{Repos}/{created.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"{Repos}/{created.Id}")).StatusCode);
        await WaitUntilAsync(() => Task.FromResult(!Directory.Exists(path)));
        Assert.Equal(HttpStatusCode.NoContent, (await browser.DeleteAsync("/api/projects/hub")).StatusCode);
    }

    [Theory]
    [InlineData("""{"name":"api","remoteUrl":"ext::sh -c touch% /tmp/pwned"}""", "remoteUrl")]
    [InlineData("""{"name":"api","remoteUrl":"/etc"}""", "remoteUrl")]
    [InlineData("""{"name":"api","remoteUrl":"https://user:pw@example.org/repo.git"}""", "remoteUrl")]
    [InlineData("""{"name":"api","remoteUrl":"git@example.org:repo.git"}""", "credential")]
    [InlineData("""{"name":"api","remoteUrl":"git@example.org:repo.git","credential":{"kind":"https_token","token":"x"}}""", "credential")]
    [InlineData("""{"name":"api","credential":{"kind":"https_token","token":"x"}}""", "credential")]
    [InlineData("""{"name":"api","remoteUrl":"https://example.org/repo.git","credential":{"kind":"ssh_key","privateKey":"nope"}}""", "credential")]
    [InlineData("""{"name":"api","defaultBranch":"bad..name"}""", "defaultBranch")]
    [InlineData("""{"name":""}""", "name")]
    public async Task Invalid_repositories_are_rejected(string json, string field)
    {
        using var browser = await StartAsync();

        using var response = await browser.Http.PostAsync(new Uri(Repos, UriKind.Relative), new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Empty(await browser.GetJsonAsync<List<RepositoryInfo>>(Repos));
    }

    [Fact]
    public async Task Protocols_outside_the_allowlist_are_rejected()
    {
        using var browser = await StartAsync(new Dictionary<string, string> { ["Tikai:Git:AllowedProtocols"] = "https,ssh" });

        using var response = await browser.PostAsync(Repos, new { name = "api", remoteUrl = TestGit.FileUrl(_remotes.Path) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not allowed on this hub", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repository_names_are_unique_per_project()
    {
        using var browser = await StartAsync();
        (await browser.PostAsync(Repos, new { name = "api" })).EnsureSuccessStatusCode();

        using var response = await browser.PostAsync(Repos, new { name = "api" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(Directory.GetDirectories(Directory.GetDirectories(Path.Combine(_factory.DataPath, "repos")).Single()));
    }

    private async Task<BrowserClient> StartAsync(IReadOnlyDictionary<string, string>? settings = null)
    {
        _factory = await TikaiApiFactory.CreateAsync(postgres, settings);
        var browser = new BrowserClient(_factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        await browser.RefreshAntiforgeryAsync();
        await browser.SignUpAdminAsync();
        (await browser.PostAsync("/api/projects", new { name = "Tikai Hub", slug = "hub", key = "HUB" })).EnsureSuccessStatusCode();
        return browser;
    }

    /// <summary>A plain-HTTP git server with one repository that accepts only <see cref="Token"/>.</summary>
    private async Task<GitHttpServer> StartGitServerAsync()
    {
        var root = _remotes.Combine("served");
        var remote = await TestGit.InitBareAsync(Path.Combine(root, "remote.git"));
        await TestGit.CommitAsync(remote, "main", "First", ("a.txt", "1\n"));
        return await GitHttpServer.StartAsync(root, "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{Token}")));
    }

    private async Task<string> MirrorPathAsync(BrowserClient browser, Guid repositoryId)
    {
        var project = await browser.GetJsonAsync<ProjectInfo>("/api/projects/hub");
        return Path.Combine(_factory.DataPath, "repos", project.Id.ToString("D"), repositoryId.ToString("D") + ".git");
    }

    private static async Task<RepositoryInfo> WaitForAsync(BrowserClient browser, Guid id, Func<RepositoryInfo, bool> condition)
    {
        RepositoryInfo? repository = null;
        await WaitUntilAsync(async () => condition(repository = await browser.GetJsonAsync<RepositoryInfo>($"{Repos}/{id}")));
        return repository!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met within 30 s.");
            }
            await Task.Delay(100);
        }
    }
}
