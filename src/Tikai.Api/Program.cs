using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tikai.Api.Auth;
using Tikai.Api.Hosting;
using Tikai.Data;

var builder = WebApplication.CreateBuilder(args);

// Resolved lazily: OpenAPI generation at build time builds the host without database settings.
builder.Services.AddTikaiData(sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("Db")
    ?? throw new InvalidOperationException("Connection string 'Db' is missing. Set ConnectionStrings__Db (see .env.example)."));
builder.Services.AddTikaiAuth(builder.Configuration, builder.Environment);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<TikaiDbContext>("database", tags: ["ready"]);

if (BuildTimeTools.IsGeneratingOpenApiDocument)
{
    // The document generator starts the host; background workers would try to reach the database.
    builder.Services.RemoveAll<IHostedService>();
}

var app = builder.Build();

if (!BuildTimeTools.IsGeneratingOpenApiDocument)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<TikaiDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// Liveness: the process runs. Readiness: the database is reachable.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

var api = app.MapGroup("/api").AddEndpointFilter<AntiforgeryFilter>();
api.MapAuthEndpoints();
// Unknown API routes are 404s, not the web app's index.html.
api.Map("/{**path}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found"));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// The web app (built into wwwroot in the hub image) handles every other route.
app.MapFallbackToFile("index.html");

await app.RunAsync();

/// <summary>Entry point; public for WebApplicationFactory in tests.</summary>
public partial class Program;
