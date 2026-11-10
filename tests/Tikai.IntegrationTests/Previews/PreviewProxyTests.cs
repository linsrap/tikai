using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tikai.Api.Previews;
using Tikai.Core.Previews;
using Tikai.Data;
using Tikai.IntegrationTests.Auth;
using Tikai.IntegrationTests.Runs;
using Tikai.Orchestrator.Previews;

namespace Tikai.IntegrationTests.Previews;

/// <summary>
/// The preview's own origin through the hub's proxy: entry with a one-time token, a host-only session cookie, the app
/// behind it, and none of the hub's cookies or routes on that host.
/// </summary>
public sealed class PreviewProxyTests(PostgresFixture postgres, AgentHost agents) : IClassFixture<AgentHost>
{
    [Fact]
    public async Task A_preview_is_entered_once_with_its_token_and_the_app_never_sees_the_hub_s_cookies()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("preview");
        var changeSetId = (await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id)).ChangeSetId!.Value;
        var preview = await StartRunningAsync(hub, changeSetId);

        var host = $"{preview.Id:N}.preview.localhost:{hub.Port}";
        Assert.Equal($"http://{host}", preview.Url);
        using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://localhost:{hub.Port}") };

        // Without the preview's cookie nothing is forwarded, also not with the hub's login cookie.
        var hubCookie = await LoginCookieAsync(hub.Port);
        Assert.Contains("tikai.auth=", hubCookie, StringComparison.Ordinal);
        using (var anonymous = await SendAsync(http, host, "/", hubCookie))
        {
            Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
        }

        // The entry link sets a host-only cookie once.
        var entry = await EntryAsync(hub, preview.Id);
        Assert.StartsWith($"http://{host}{PreviewHosts.EntryPath}?t=tikai_entry_", entry, StringComparison.Ordinal);
        var path = new Uri(entry).PathAndQuery;
        string session;
        using (var entered = await SendAsync(http, host, path, null))
        {
            Assert.Equal(HttpStatusCode.Redirect, entered.StatusCode);
            Assert.Equal("/", entered.Headers.Location?.ToString());
            var setCookie = Assert.Single(entered.Headers.GetValues("Set-Cookie"));
            Assert.StartsWith(PreviewHosts.SessionCookie + "=", setCookie, StringComparison.Ordinal);
            Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);
            session = setCookie.Split(';')[0];
        }
        using (var again = await SendAsync(http, host, path, null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        }

        // The app answers; the hub's own routes are not served on this host.
        using (var page = await SendAsync(http, host, "/", session))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync(ct);
            Assert.Contains("Hello from the preview", html, StringComparison.Ordinal);
            Assert.Contains("built by setup", html, StringComparison.Ordinal);
        }
        using (var api = await SendAsync(http, host, "/api/auth/me", $"{session}; {hubCookie}"))
        {
            Assert.Contains("Hello from the preview", await api.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

        // Only the app's own cookies reach it.
        using (var echo = await SendAsync(http, host, "/echo", $"{session}; {hubCookie}; app=1"))
        {
            // Every cookie the hub owns is a tikai* name; XSRF-TOKEN is a name apps use too, and the hub's own is host-only.
            var seen = (await echo.Content.ReadFromJsonAsync<EchoInfo>(ct))!;
            var names = seen.Cookie!.Split("; ").Select(c => c.Split('=')[0]).ToList();
            Assert.Contains("app", names);
            Assert.DoesNotContain(names, n => n.StartsWith("tikai", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(host, seen.ForwardedHost);
        }
        Assert.NotNull((await RowAsync(hub, preview.Id)).LastRequestAt);

        // A session of one preview does not open another's host; names under the suffix that are no preview are 404s.
        using (var other = await SendAsync(http, $"{Guid.NewGuid():N}.preview.localhost:{hub.Port}", "/", session))
        {
            Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        }
        using (var unknown = await SendAsync(http, $"www.preview.localhost:{hub.Port}", "/", session))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        // Once stopped, the host refuses (after the proxy's short cache).
        (await hub.Browser.PostAsync($"/api/previews/{preview.Id}/stop", new { })).EnsureSuccessStatusCode();
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        using (var stopped = await SendAsync(http, host, "/", session))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, stopped.StatusCode);
        }
        using var late = await hub.Browser.PostAsync($"/api/previews/{preview.Id}/entry", new { });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task Paths_go_to_the_app_with_the_longest_matching_prefix_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var hub = await agents.StartHubAsync(postgres);
        var ticket = await hub.CreateTicketAsync("preview-apps");
        var changeSetId = (await hub.WaitForEndAsync((await hub.AssignAsync(ticket)).Id)).ChangeSetId!.Value;
        var preview = await StartRunningAsync(hub, changeSetId);

        var host = $"{preview.Id:N}.preview.localhost:{hub.Port}";
        using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://localhost:{hub.Port}") };
        string session;
        using (var entered = await SendAsync(http, host, new Uri(await EntryAsync(hub, preview.Id)).PathAndQuery, null))
        {
            Assert.Equal(HttpStatusCode.Redirect, entered.StatusCode);
            session = Assert.Single(entered.Headers.GetValues("Set-Cookie")).Split(';')[0];
        }

        async Task<AppAnswer> GetAsync(string path)
        {
            using var response = await SendAsync(http, host, path, session);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<AppAnswer>(ct))!;
        }

        Assert.Equal(("web", "/"), (await GetAsync("/")).Pick());
        Assert.Equal(("web", "/index.html?x=1"), (await GetAsync("/index.html?x=1")).Pick());
        Assert.Equal(("api", "/api"), (await GetAsync("/api")).Pick());
        // The prefix stays on the path.
        Assert.Equal(("api", "/api/hello?q=2"), (await GetAsync("/api/hello?q=2")).Pick());
        Assert.Equal(("web", "/apix"), (await GetAsync("/apix")).Pick());

        // web reaches api by its name; both got the same generated value.
        var web = await GetAsync("/from-api");
        Assert.Equal("api", web.FromApi!.App);
        Assert.Equal(web.Shared, web.FromApi.Shared);
        Assert.Equal(web.Shared, (await GetAsync("/api/x")).Shared);

        var log = await hub.Browser.GetJsonAsync<List<PreviewLogLineInfo>>($"/api/previews/{preview.Id}/log?limit=1000");
        Assert.Contains(log, l => l.Source == "app web" && l.Text == "web listening with [redacted]");
        Assert.Contains(log, l => l.Source == "app api" && l.Text == "api listening with [redacted]");

        (await hub.Browser.PostAsync($"/api/previews/{preview.Id}/stop", new { })).EnsureSuccessStatusCode();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, string host, string path, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = host;
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }
        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>A real login of the hub's admin, as the Cookie header a browser would send to the hub.</summary>
    private static async Task<string> LoginCookieAsync(int port)
    {
        var hubUri = new Uri($"http://localhost:{port}");
        var cookies = new CookieContainer();
        using var client = new HttpClient(new SocketsHttpHandler { CookieContainer = cookies }) { BaseAddress = hubUri };
        var browser = new BrowserClient(client);
        await browser.RefreshAntiforgeryAsync();
        (await browser.LoginAsync("ada@example.org")).EnsureSuccessStatusCode();
        return cookies.GetCookieHeader(hubUri);
    }

    private static async Task<string> EntryAsync(AgentHub hub, Guid previewId)
    {
        using var response = await hub.Browser.PostAsync($"/api/previews/{previewId}/entry", new { });
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<PreviewEntryInfo>(TikaiJson.Options))!.Url;
    }

    private static async Task<PreviewInfo> StartRunningAsync(AgentHub hub, Guid changeSetId)
    {
        (await hub.Browser.PostAsync($"/api/change-sets/{changeSetId}/previews", new { })).EnsureSuccessStatusCode();
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (true)
        {
            var preview = (await hub.Browser.GetJsonAsync<ChangeSetPreviewInfo>($"/api/change-sets/{changeSetId}/preview")).Preview!;
            if (preview.State == PreviewState.Running)
            {
                return preview;
            }
            Assert.True(DateTime.UtcNow < deadline && preview.State != PreviewState.Failed, $"Preview is {preview.State} ({preview.Error})");
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<Preview> RowAsync(AgentHub hub, Guid previewId)
    {
        await using var scope = hub.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TikaiDbContext>().Previews.AsNoTracking().SingleAsync(p => p.Id == previewId);
    }

    private sealed record EchoInfo(string? Cookie, string? Host, string? ForwardedHost);

    private sealed record AppAnswer(string App, string Path, string Shared, AppAnswer? FromApi)
    {
        public (string, string) Pick() => (App, Path);
    }
}
