
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
        Http.PostAsJsonAsync(new Uri(path, UriKind.Relative), body ?? new { });

    public Task<HttpResponseMessage> SignUpAsync(string email, string password = "correct horse battery", string displayName = "Ada") =>
        PostAsync("/api/auth/signup", new { email, displayName, password });

    public Task<HttpResponseMessage> LoginAsync(string email, string password = "correct horse battery") =>
        PostAsync("/api/auth/login", new { email, password });

    public Task<HttpResponseMessage> GetAsync(string path) => Http.GetAsync(new Uri(path, UriKind.Relative));

    public void Dispose() => Http.Dispose();
}
