using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tikai.Api.Auth;
using Tikai.Api.Hosting;
using Tikai.Api.Llm;
using Tikai.Api.Projects;
using Tikai.Api.Security;
using Tikai.Api.Tickets;
using Tikai.Data;
using Tikai.Llm;

var builder = WebApplication.CreateBuilder(args);
if (BuildTimeTools.IsGeneratingOpenApiDocument)
{
    // The document generator builds the host without real settings; the key ring is never used then.
    builder.Configuration.AddInMemoryCollection([new("Tikai:EncryptionSecret", new string('x', KeyRingOptions.MinSecretLength))]);
}

// Resolved lazily: OpenAPI generation at build time builds the host without database settings.
builder.Services.AddTikaiData(sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("Db")
    ?? throw new InvalidOperationException("Connection string 'Db' is missing. Set ConnectionStrings__Db (see .env.example)."));
builder.Services.AddTikaiAuth(builder.Configuration, builder.Environment);
builder.Services.AddTikaiProjects(builder.Configuration, builder.Environment);
builder.Services.AddTikaiLlm();
builder.Services.AddScoped<LlmProviderService>();
builder.Services.AddScoped<TicketService>();
builder.Services.AddProblemDetails();
// Enums travel as snake_case strings ("https_token"), the same values the database stores.
// Numbers are only numbers (the web default also reads "42"), so the OpenAPI types say number, not number | string.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
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
    KeyRingStartup.Prepare(app.Services, DataPaths.Keys(app.Configuration, app.Environment), app.Logger);
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
api.MapProjectEndpoints();
api.MapLlmProviderEndpoints();
api.MapTicketEndpoints();
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
