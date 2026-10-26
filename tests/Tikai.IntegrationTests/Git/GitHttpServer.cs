using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Tikai.Git.Processes;

namespace Tikai.IntegrationTests.Git;

/// <summary>
/// A git smart-HTTP server for tests: Kestrel in front of `git http-backend` (as CGI), serving the bare
/// repositories below a root directory. It answers 401 unless the request carries the expected Authorization header.
/// </summary>
internal sealed class GitHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private GitHttpServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    /// <summary>The Authorization header of every request (null when there was none).</summary>
    public ConcurrentQueue<string?> AuthorizationHeaders { get; } = new();

    public static async Task<GitHttpServer> StartAsync(string root, string expectedAuthorization)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        GitHttpServer? server = null;
        app.Run(context => server!.HandleAsync(context, root, expectedAuthorization));
        await app.StartAsync();
        server = new GitHttpServer(app, app.Urls.Single());
        return server;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private async Task HandleAsync(HttpContext context, string root, string expectedAuthorization)
    {
        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        AuthorizationHeaders.Enqueue(authorization);
        if (authorization != expectedAuthorization)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"tikai-test\"";
            return;
        }

        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body);
        var env = new Dictionary<string, string?>
        {
            ["GIT_PROJECT_ROOT"] = root,
            ["GIT_HTTP_EXPORT_ALL"] = "1",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["PATH_INFO"] = context.Request.Path.Value,
            ["REQUEST_METHOD"] = context.Request.Method,
            ["QUERY_STRING"] = context.Request.QueryString.Value?.TrimStart('?') ?? "",
            ["CONTENT_TYPE"] = context.Request.ContentType ?? "",
            ["CONTENT_LENGTH"] = body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["HTTP_CONTENT_ENCODING"] = context.Request.Headers.ContentEncoding.FirstOrDefault() ?? "",
            ["HTTP_GIT_PROTOCOL"] = context.Request.Headers["Git-Protocol"].FirstOrDefault() ?? "",
            ["REMOTE_USER"] = "tikai",
            ["REMOTE_ADDR"] = "127.0.0.1",
        };
        var result = await new ProcessRunner().RunAsync(
            new ProcessRequest("git", ["http-backend"], TimeSpan.FromMinutes(1), env, StandardInput: body.ToArray()));

        // CGI output: headers, a blank line, then the body.
        var output = result.StandardOutput;
        var (headerEnd, separator) = FindHeaderEnd(output);
        foreach (var line in Encoding.ASCII.GetString(output, 0, headerEnd).Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var (name, value) = (line[..colon], line[(colon + 1)..].Trim());
            if (name.Equals("Status", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = int.Parse(value.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                context.Response.Headers[name] = value;
            }
        }
        await context.Response.Body.WriteAsync(output.AsMemory(headerEnd + separator));
    }

    private static (int Index, int SeparatorLength) FindHeaderEnd(byte[] output)
    {
        for (var i = 0; i < output.Length - 1; i++)
        {
            if (output[i] == '\n' && output[i + 1] == '\n')
            {
                return (i, 2);
            }
            if (i < output.Length - 3 && output[i] == '\r' && output[i + 1] == '\n' && output[i + 2] == '\r' && output[i + 3] == '\n')
            {
                return (i, 4);
            }
        }
        throw new InvalidOperationException("git http-backend wrote no header block: " + Encoding.UTF8.GetString(output));
    }
}
