using Tikai.Orchestrator.Previews;

namespace Tikai.Core.Tests.Previews;

public class PreviewDefinitionTests
{
    private const string Full = """
        setup:
          - image: node:24-trixie-slim
            run: npm ci --prefix web && npm run build --prefix web
          - image: mcr.microsoft.com/dotnet/sdk:10.0
            run: dotnet publish src/Tikai.Api -c Release -o /preview/out
        app:
          image: mcr.microsoft.com/dotnet/sdk:10.0
          command: dotnet /preview/out/Tikai.Api.dll
          port: 8080
          environment: { ConnectionStrings__Tikai: "Host=db;Database=tikai;Username=tikai;Password=preview" }
          generate: [TIKAI_SECRET]
          healthcheck: { path: /health }
        services:
          db:
            image: postgres:17
            environment: { POSTGRES_USER: tikai, POSTGRES_PASSWORD: preview, POSTGRES_DB: tikai }
            healthcheck: { command: pg_isready -U tikai }
        idle_minutes: 30
        """;

    private const string Minimal = """
        app:
          image: python:3.13-slim
          command: python -m http.server 8000
          port: 8000
        """;

    [Fact]
    public void The_full_example_parses_into_the_hub_s_model()
    {
        var definition = Ok(Full);

        Assert.Equal(2, definition.Setup.Count);
        Assert.Equal(new PreviewSetupStep("node:24-trixie-slim", "npm ci --prefix web && npm run build --prefix web"), definition.Setup[0]);
        Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", definition.App.Image);
        Assert.Equal("dotnet /preview/out/Tikai.Api.dll", definition.App.Command);
        Assert.Equal(8080, definition.App.Port);
        Assert.Equal("Host=db;Database=tikai;Username=tikai;Password=preview", definition.App.Environment["ConnectionStrings__Tikai"]);
        Assert.Equal(["TIKAI_SECRET"], definition.App.Generate);
        Assert.Equal("/health", definition.App.HealthcheckPath);
        var db = Assert.Single(definition.Services);
        Assert.Equal("db", db.Name);
        Assert.Equal("postgres:17", db.Image);
        Assert.Equal("tikai", db.Environment["POSTGRES_USER"]);
        Assert.Null(db.Command);
        Assert.Equal("pg_isready -U tikai", db.HealthcheckCommand);
        Assert.Equal(30, definition.IdleMinutes);
    }

    [Fact]
    public void Only_the_app_is_required_and_the_rest_has_defaults()
    {
        var definition = Ok(Minimal);

        Assert.Empty(definition.Setup);
        Assert.Empty(definition.Services);
        Assert.Empty(definition.App.Environment);
        Assert.Empty(definition.App.Generate);
        Assert.Null(definition.App.HealthcheckPath);
        Assert.Equal(PreviewDefinition.DefaultIdleMinutes, definition.IdleMinutes);
    }

    [Fact]
    public void A_service_command_is_a_list_of_arguments_for_the_image_s_entrypoint()
    {
        var definition = Ok(Minimal + """

            services:
              cache:
                image: redis:7
                command: [redis-server, --save, ""]
            """);

        Assert.Equal(["redis-server", "--save", ""], definition.Services[0].Command);
    }

    [Fact]
    public void Numbers_and_booleans_in_the_environment_become_strings()
    {
        var definition = Ok(Minimal + "\n  environment: { WORKERS: 4, DEBUG: true }");

        Assert.Equal("4", definition.App.Environment["WORKERS"]);
        Assert.Equal("true", definition.App.Environment["DEBUG"]);
    }

    // Everything the file could use to make Docker do more than the hub allows.
    [Theory]
    [InlineData("build")]
    [InlineData("volumes")]
    [InlineData("ports")]
    [InlineData("privileged")]
    [InlineData("cap_add")]
    [InlineData("devices")]
    [InlineData("networks")]
    [InlineData("network_mode")]
    [InlineData("user")]
    [InlineData("security_opt")]
    [InlineData("entrypoint")]
    [InlineData("pid")]
    [InlineData("ipc")]
    [InlineData("tmpfs")]
    [InlineData("extra_hosts")]
    [InlineData("env_file")]
    [InlineData("labels")]
    [InlineData("restart")]
    public void Docker_options_are_refused_on_the_app(string key)
    {
        var errors = Errors(Minimal + $"\n  {key}: x");

        Assert.Contains($"app.{key}: not allowed; a preview definition only describes how to start the app, the hub decides what it may do", errors);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("volumes")]
    [InlineData("ports")]
    [InlineData("privileged")]
    [InlineData("cap_add")]
    [InlineData("devices")]
    [InlineData("networks")]
    [InlineData("user")]
    [InlineData("entrypoint")]
    public void Docker_options_are_refused_on_a_service(string key)
    {
        var errors = Errors(Minimal + $"\nservices:\n  db:\n    image: postgres:17\n    {key}: x");

        Assert.Contains($"services.db.{key}: not allowed; a preview definition only describes how to start the app, the hub decides what it may do", errors);
    }

    [Theory]
    [InlineData("volumes")]
    [InlineData("privileged")]
    [InlineData("user")]
    [InlineData("network")]
    [InlineData("environment")]
    public void Docker_options_are_refused_on_a_setup_step(string key)
    {
        var errors = Errors(Minimal + $"\nsetup:\n  - image: node:24\n    run: npm ci\n    {key}: x");

        Assert.Contains($"setup[0].{key}: not allowed; a preview definition only describes how to start the app, the hub decides what it may do", errors);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("volumes")]
    [InlineData("networks")]
    [InlineData("secrets")]
    [InlineData("version")]
    [InlineData("x-anything")]
    public void Unknown_top_level_keys_are_refused(string key)
    {
        var errors = Errors(Minimal + $"\n{key}: x");

        Assert.Contains($"{key}: not allowed; a preview definition only describes how to start the app, the hub decides what it may do", errors);
    }

    [Fact]
    public void An_app_is_required()
    {
        Assert.Contains("app: required", Errors("idle_minutes: 30"));
    }

    [Fact]
    public void Image_command_and_port_of_the_app_are_required()
    {
        var errors = Errors("app: { healthcheck: { path: /health } }");

        Assert.Contains("app.image: required", errors);
        Assert.Contains("app.command: required", errors);
        Assert.Contains("app.port: required", errors);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("80.5")]
    [InlineData("http")]
    public void The_port_must_be_a_tcp_port(string port)
    {
        Assert.Contains("app.port: must be a whole number from 1 to 65535",
            Errors($"app: {{ image: a, command: b, port: {port} }}"));
    }

    [Theory]
    [InlineData("4")]
    [InlineData("241")]
    [InlineData("soon")]
    public void Idle_minutes_stay_within_the_hub_s_limit(string minutes)
    {
        Assert.Contains($"idle_minutes: must be a whole number from {PreviewDefinition.MinIdleMinutes} to {PreviewDefinition.MaxIdleMinutes}",
            Errors(Minimal + $"\nidle_minutes: {minutes}"));
    }

    [Theory]
    [InlineData("-rm")]
    [InlineData("node 24")]
    [InlineData("Node:24")]
    [InlineData("node;rm")]
    public void Image_names_must_look_like_image_references(string image)
    {
        Assert.Contains("app.image: not an image reference",
            Errors($"app: {{ image: \"{image}\", command: b, port: 80 }}"));
    }

    [Fact]
    public void Image_references_with_registry_digest_and_tag_are_accepted()
    {
        Ok("app: { image: \"ghcr.io/org/app@sha256:0123abcd0123abcd0123abcd0123abcd0123abcd0123abcd0123abcd0123abcd\", command: b, port: 80 }");
        Ok("app: { image: \"localhost:5000/team/app:1.2.3\", command: b, port: 80 }");
    }

    [Theory]
    [InlineData("1BAD")]
    [InlineData("BAD-NAME")]
    [InlineData("")]
    public void Environment_names_must_be_variable_names(string name)
    {
        Assert.Contains($"app.environment.{name}: not a variable name",
            Errors(Minimal + $"\n  environment: {{ \"{name}\": x }}"));
    }

    [Fact]
    public void Generated_names_must_be_variable_names_and_not_also_set()
    {
        var errors = Errors(Minimal + "\n  environment: { SECRET: x }\n  generate: [SECRET, \"bad name\"]");

        Assert.Contains("app.generate.SECRET: also set in app.environment", errors);
        Assert.Contains("app.generate.bad name: not a variable name", errors);
    }

    [Theory]
    [InlineData("health")]
    [InlineData("//evil.example/")]
    [InlineData("/a b")]
    public void The_healthcheck_path_is_a_path_on_the_app(string path)
    {
        Assert.Contains("app.healthcheck.path: must be a path starting with /",
            Errors(Minimal + $"\n  healthcheck: {{ path: \"{path}\" }}"));
    }

    [Theory]
    [InlineData("DB")]
    [InlineData("1db")]
    [InlineData("db_1")]
    [InlineData("app")]
    [InlineData("hub")]
    public void Service_names_are_lowercase_host_names_other_than_the_reserved_ones(string name)
    {
        Assert.Contains($"services.{name}: the name must be a lowercase host name (letters, digits, -) other than app and hub",
            Errors(Minimal + $"\nservices:\n  {name}:\n    image: postgres:17"));
    }

    [Fact]
    public void A_setup_step_needs_an_image_and_a_command()
    {
        var errors = Errors(Minimal + "\nsetup:\n  - image: node:24\n  - run: npm ci");

        Assert.Contains("setup[0].run: required", errors);
        Assert.Contains("setup[1].image: required", errors);
    }

    [Fact]
    public void Wrong_shapes_are_errors_not_exceptions()
    {
        Assert.Contains("setup: must be a list", Errors(Minimal + "\nsetup: npm ci"));
        Assert.Contains("services: must be a mapping", Errors(Minimal + "\nservices: [db]"));
        Assert.Contains("app.command: must be text", Errors("app: { image: a, command: [a, b], port: 80 }"));
        Assert.Contains("app.environment: must be a mapping", Errors(Minimal + "\n  environment: [A=b]"));
        Assert.Contains("the file must be a mapping", Errors("- app"));
    }

    [Fact]
    public void Errors_are_collected_not_stopped_at_the_first()
    {
        var errors = Errors("app: { image: a, command: b, port: 0, volumes: [x] }\nidle_minutes: 1");

        Assert.Equal(3, errors.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# only a comment")]
    public void An_empty_file_is_an_error(string yaml)
    {
        Assert.Contains("the file is empty", Errors(yaml));
    }

    [Fact]
    public void Invalid_yaml_is_an_error_with_its_position()
    {
        var error = Assert.Single(Errors("app: { image: a"));

        Assert.StartsWith("not valid YAML", error);
    }

    [Fact]
    public void Anchors_and_aliases_are_refused()
    {
        var errors = Errors("app: &app { image: a, command: b, port: 80 }\nservices:\n  db: *app");

        Assert.Contains("anchors and aliases are not allowed", errors);
    }

    [Fact]
    public void Several_documents_are_refused()
    {
        Assert.Contains("the file must hold one document", Errors(Minimal + "\n---\n" + Minimal));
    }

    [Fact]
    public void Duplicate_keys_are_refused()
    {
        Assert.Contains(Errors(Minimal + "\n  port: 9000"), e => e.Contains("duplicate key", StringComparison.Ordinal));
    }

    [Fact]
    public void Too_many_setup_steps_and_services_are_refused()
    {
        var steps = string.Concat(Enumerable.Range(0, PreviewDefinition.MaxSetupSteps + 1).Select(_ => "\n  - { image: a, run: b }"));
        var services = string.Concat(Enumerable.Range(0, PreviewDefinition.MaxServices + 1).Select(i => $"\n  s{i}: {{ image: a }}"));

        Assert.Contains($"setup: at most {PreviewDefinition.MaxSetupSteps} steps", Errors(Minimal + "\nsetup:" + steps));
        Assert.Contains($"services: at most {PreviewDefinition.MaxServices} services", Errors(Minimal + "\nservices:" + services));
    }

    [Fact]
    public void A_file_over_the_size_limit_is_refused_before_parsing()
    {
        var yaml = Minimal + "\n# " + new string('x', PreviewDefinition.MaxBytes);

        Assert.Equal([$"the file is larger than {PreviewDefinition.MaxBytes / 1024} KB"], Errors(yaml));
    }

    private static PreviewDefinition Ok(string yaml)
    {
        var result = PreviewDefinition.Parse(yaml);
        Assert.True(result.Definition is not null, string.Join("\n", result.Errors));
        return result.Definition!;
    }

    private static IReadOnlyList<string> Errors(string yaml)
    {
        var result = PreviewDefinition.Parse(yaml);
        Assert.Null(result.Definition);
        Assert.NotEmpty(result.Errors);
        return result.Errors;
    }
}
