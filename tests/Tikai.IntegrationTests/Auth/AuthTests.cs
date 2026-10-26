using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Tikai.Api.Auth;

namespace Tikai.IntegrationTests.Auth;

public sealed class AuthTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TikaiApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await TikaiApiFactory.CreateAsync(postgres);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task The_first_sign_up_becomes_admin_and_closes_sign_up()
    {
        using var browser = await NewBrowserAsync();
        Assert.True((await StatusAsync(browser)).SignUpOpen);

        using var response = await browser.SignUpAsync("ada@example.org");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<CurrentUser>(TestContext.Current.CancellationToken);
        Assert.NotNull(user);
        Assert.True(user.IsAdmin);
        Assert.Equal("ada@example.org", user.Email);
        Assert.Equal("Ada", user.DisplayName);

        var status = await StatusAsync(browser);
        Assert.False(status.SignUpOpen);
        Assert.Equal(user, status.User); // signed in right away
    }

    [Fact]
    public async Task A_second_sign_up_is_rejected()
    {
        using var first = await NewBrowserAsync();
        (await first.SignUpAsync("ada@example.org")).EnsureSuccessStatusCode();

        using var second = await NewBrowserAsync();
        using var response = await second.SignUpAsync("eve@example.org");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Sign-up is closed", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Parallel_first_sign_ups_create_exactly_one_user()
    {
        var browsers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => NewBrowserAsync()));
        var responses = await Task.WhenAll(browsers.Select((b, i) => b.SignUpAsync($"user{i}@example.org")));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Forbidden));
        foreach (var disposable in responses.Cast<IDisposable>().Concat(browsers))
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task Login_and_logout_work_and_me_needs_a_session()
    {
        using var setup = await NewBrowserAsync();
        (await setup.SignUpAsync("ada@example.org")).EnsureSuccessStatusCode();

        using var browser = await NewBrowserAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/me")).StatusCode);

        using (var login = await browser.LoginAsync("ada@example.org"))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/auth/me")).StatusCode);

        await browser.RefreshAntiforgeryAsync(); // the token is bound to the now logged-in user
        using (var logout = await browser.PostAsync("/api/auth/logout"))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_is_rejected_and_five_failures_lock_the_account()
    {
        using var setup = await NewBrowserAsync();
        (await setup.SignUpAsync("ada@example.org")).EnsureSuccessStatusCode();

        using var browser = await NewBrowserAsync();
        for (var i = 0; i < 5; i++)
        {
            using var wrong = await browser.LoginAsync("ada@example.org", "not the password at all");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var locked = await browser.LoginAsync("ada@example.org");
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Contains("Too many failed logins", await locked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unknown_emails_get_the_same_answer_as_wrong_passwords()
    {
        using var browser = await NewBrowserAsync();
        using var response = await browser.LoginAsync("nobody@example.org");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Wrong email or password", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unsafe_requests_without_an_antiforgery_token_are_rejected()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/signup", UriKind.Relative),
            new { email = "ada@example.org", displayName = "Ada", password = "correct horse battery" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("antiforgery", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);

        using var browser = await NewBrowserAsync();
        Assert.True((await StatusAsync(browser)).SignUpOpen, "the rejected request must not have created a user");
    }

    [Fact]
    public async Task Short_passwords_are_rejected_with_a_validation_problem()
    {
        using var browser = await NewBrowserAsync();
        using var response = await browser.SignUpAsync("ada@example.org", password: "short");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"password\"", body);
        Assert.Contains("12", body);
        Assert.True((await StatusAsync(browser)).SignUpOpen);
    }

    [Fact]
    public async Task Unknown_api_routes_are_404_not_the_web_app()
    {
        using var browser = await NewBrowserAsync();
        using var response = await browser.GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_auth_cookie_is_http_only()
    {
        using var browser = await NewBrowserAsync();
        using var response = await browser.SignUpAsync("ada@example.org");

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AuthSetup.AuthCookie + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<BrowserClient> NewBrowserAsync()
    {
        var browser = new BrowserClient(_factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        await browser.RefreshAntiforgeryAsync();
        return browser;
    }

    private static async Task<AuthStatus> StatusAsync(BrowserClient browser) =>
        (await browser.Http.GetFromJsonAsync<AuthStatus>(new Uri("/api/auth/status", UriKind.Relative)))!;
}
