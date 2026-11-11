
using System.Net.Http.Json;

namespace Tikai.IntegrationTests.Auth;

/// <summary>Talks to the API like the web app: keeps cookies and sends the antiforgery token on unsafe requests.</summary>
public sealed class BrowserClient(HttpClient http) : IDisposable
{
    public HttpClient Http { get; } = http;

    /// <summary>Fetches a fresh antiforgery token (needed again after login and logout: tokens are bound to the user).</summary>
    public async Task RefreshAntiforgeryAsync()
    {
        using var response = await Http.GetAsync(new Uri("/api/auth/antiforgery", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        var token = response.Headers.GetValues("Set-Cookie")
            .Select(c => c.Split(';')[0])
            .Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal))["XSRF-TOKEN=".Length..];
        Http.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        Http.DefaultRequestHeaders.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(token));
    }

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) =>
        Http.PostAsJsonAsync(new Uri(path, UriKind.Relative), body ?? new { }, TikaiJson.Options);

    public Task<HttpResponseMessage> SignUpAsync(string email, string password = "correct horse battery", string displayName = "Ada") =>
        PostAsync("/api/auth/signup", new { email, displayName, password });

    public Task<HttpResponseMessage> LoginAsync(string email, string password = "correct horse battery") =>
        PostAsync("/api/auth/login", new { email, password });

    public Task<HttpResponseMessage> GetAsync(string path) => Http.GetAsync(new Uri(path, UriKind.Relative));

    public async Task<T> GetJsonAsync<T>(string path) =>
        (await Http.GetFromJsonAsync<T>(new Uri(path, UriKind.Relative), TikaiJson.Options))!;

    public Task<HttpResponseMessage> PutAsync(string path, object body) => Http.PutAsJsonAsync(new Uri(path, UriKind.Relative), body, TikaiJson.Options);

    public Task<HttpResponseMessage> PatchAsync(string path, object body) => Http.PatchAsJsonAsync(new Uri(path, UriKind.Relative), body, TikaiJson.Options);

    public Task<HttpResponseMessage> DeleteAsync(string path) => Http.DeleteAsync(new Uri(path, UriKind.Relative));

    /// <summary>Deletes the project with everything in it (its name typed to confirm) and waits until it is gone.</summary>
    public async Task DeleteProjectAsync(string slug, int seconds = 60)
    {
        var name = (await GetJsonAsync<Tikai.Api.Projects.ProjectInfo>($"/api/projects/{slug}")).Name;
        using (var accepted = await PostAsync($"/api/projects/{slug}/delete", new { name }))
        {
            Assert.True(accepted.StatusCode == System.Net.HttpStatusCode.Accepted, $"{(int)accepted.StatusCode} {await accepted.Content.ReadAsStringAsync()}");
        }
        var until = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (true)
        {
            using var response = await GetAsync($"/api/projects/{slug}");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return;
            }
            Assert.True(DateTimeOffset.UtcNow < until, $"The project {slug} was not deleted within {seconds} s.");
            await Task.Delay(200);
        }
    }

    /// <summary>Signs up the first (admin) user and keeps the session, ready for API calls.</summary>
    public async Task SignUpAdminAsync()
    {
        (await SignUpAsync("ada@example.org")).EnsureSuccessStatusCode();
        await RefreshAntiforgeryAsync();
    }

    public void Dispose() => Http.Dispose();
}
