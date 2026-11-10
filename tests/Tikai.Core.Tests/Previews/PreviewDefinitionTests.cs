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
        Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", definition.Apps[0].Image);
        Assert.Equal("dotnet /preview/out/Tikai.Api.dll", definition.Apps[0].Command);
        Assert.Equal(8080, definition.Apps[0].Port);
        Assert.Equal("Host=db;Database=tikai;Username=tikai;Password=preview", definition.Apps[0].Environment["ConnectionStrings__Tikai"]);
        Assert.Equal(["TIKAI_SECRET"], definition.Apps[0].Generate);
        Assert.Equal("/health", definition.Apps[0].HealthcheckPath);
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
        Assert.Empty(definition.Apps[0].Environment);
        Assert.Empty(definition.Apps[0].Generate);
        Assert.Null(definition.Apps[0].HealthcheckPath);
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

        Assert.Equal("4", definition.Apps[0].Environment["WORKERS"]);
        Assert.Equal("true", definition.Apps[0].Environment["DEBUG"]);
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
        Assert.Contains("app or apps: required", Errors("idle_minutes: 30"));
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

    [Fact]
    public void Tikai_s_own_definition_starts_the_hub_with_its_database_and_agents_off()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Tikai.slnx")))
        {
            root = root.Parent!;
        }

        var definition = Ok(File.ReadAllText(Path.Combine(root.FullName, PreviewDefinition.FileName)));

        Assert.Equal(["db"], definition.Services.Select(s => s.Name));
        Assert.Equal("false", definition.Apps[0].Environment["Tikai__Docker__Enabled"]);
        Assert.Equal(["Tikai__EncryptionSecret"], definition.Apps[0].Generate);
        Assert.Equal("/health/ready", definition.Apps[0].HealthcheckPath);
    }

    private const string TwoApps = """
        apps:
          web:
            image: node:24-trixie-slim
            command: npm run dev --prefix web -- --host 0.0.0.0
            port: 5173
            generate: [SHARED]
          api:
            path: /api
            image: mcr.microsoft.com/dotnet/sdk:10.0
            command: dotnet run --project src/Api
            port: 8080
            generate: [SHARED, API_ONLY]
            healthcheck: { path: /api/health }
        services:
          db:
            image: postgres:17
        """;

    [Fact]
    public void The_single_app_is_one_app_named_app_on_the_root_path()
    {
        var app = Assert.Single(Ok(Minimal).Apps);

        Assert.Equal((PreviewApp.SingleName, "/", "app", "The app"), (app.Name, app.Path, app.LogSource, app.Label));
    }

    [Fact]
    public void Apps_are_read_in_order_with_their_path_prefixes()
    {
        var definition = Ok(TwoApps);

        Assert.Equal(["web", "api"], definition.Apps.Select(a => a.Name));
        Assert.Equal(["/", "/api"], definition.Apps.Select(a => a.Path));
        var api = definition.Apps[1];
        Assert.Equal((8080, "/api/health", "app api", "The app api"), (api.Port, api.HealthcheckPath, api.LogSource, api.Label));
        Assert.Equal(["SHARED", "API_ONLY"], api.Generate);
        Assert.Equal(["SHARED"], definition.Apps[0].Generate);
    }

    [Fact]
    public void Deeper_prefixes_are_accepted()
    {
        var definition = Ok("apps:\n  web: { image: a, command: b, port: 80 }\n  v2: { path: /api/v2.1, image: a, command: b, port: 81 }\n  docs: { path: /_docs~x, image: a, command: b, port: 82 }");

        Assert.Equal(["/", "/api/v2.1", "/_docs~x"], definition.Apps.Select(a => a.Path));
    }

    [Fact]
    public void A_file_has_app_or_apps_but_not_both()
    {
        Assert.Contains("app and apps: use one of them, not both (app is one app on /)",
            Errors(Minimal + "\napps:\n  web: { image: a, command: b, port: 80 }"));
    }

    [Fact]
    public void The_single_app_has_no_path()
    {
        Assert.Contains("app.path: not allowed; app is always on /, use apps for several path prefixes",
            Errors(Minimal + "\n  path: /api"));
    }

    [Fact]
    public void Apps_must_be_a_mapping_of_mappings_with_at_least_one_app()
    {
        Assert.Contains("apps: must be a mapping from a name to an app", Errors("apps: [web, api]"));
        Assert.Contains("apps: at least one app", Errors("apps: {}"));
        Assert.Contains("apps.web: must be a mapping", Errors("apps:\n  web: node server.js"));
    }

    [Fact]
    public void At_most_five_apps()
    {
        var apps = string.Concat(Enumerable.Range(0, PreviewDefinition.MaxApps + 1)
            .Select(i => $"\n  a{i}: {{ path: {(i == 0 ? "/" : $"/p{i}")}, image: a, command: b, port: 80 }}"));

        Assert.Contains($"apps: at most {PreviewDefinition.MaxApps} apps", Errors("apps:" + apps));
    }

    [Theory]
    [InlineData("Web")]
    [InlineData("1web")]
    [InlineData("web_1")]
    [InlineData("hub")]
    [InlineData("a-very-long-name-that-is-no-host-label")]
    public void App_names_are_lowercase_host_names_other_than_hub(string name)
    {
        Assert.Contains($"apps.{name}: the name must be a lowercase host name (letters, digits, -) other than hub",
            Errors($"apps:\n  {name}: {{ image: a, command: b, port: 80 }}"));
    }

    [Fact]
    public void An_app_may_not_share_its_name_with_a_service()
    {
        Assert.Contains("apps.db: also the name of a service; apps and services share the preview's network",
            Errors("apps:\n  web: { image: a, command: b, port: 80 }\n  db: { path: /db, image: a, command: b, port: 81 }\nservices:\n  db: { image: postgres:17 }"));
    }

    [Fact]
    public void Exactly_one_app_is_on_the_root_path()
    {
        Assert.Contains("apps: exactly one app must be on path / (the default)",
            Errors("apps:\n  api: { path: /api, image: a, command: b, port: 80 }"));
        Assert.Contains("apps.api: its path / is also the path of apps.web; each app needs its own prefix (the default is /)",
            Errors("apps:\n  web: { image: a, command: b, port: 80 }\n  api: { image: a, command: b, port: 81 }"));
    }

    [Fact]
    public void Prefixes_are_unique()
    {
        Assert.Contains("apps.b: its path /api is also the path of apps.a; each app needs its own prefix (the default is /)",
            Errors("apps:\n  web: { image: a, command: b, port: 80 }\n  a: { path: /api, image: a, command: b, port: 81 }\n  b: { path: /api, image: a, command: b, port: 82 }"));
    }

    [Theory]
    [InlineData("api")]
    [InlineData("/api/")]
    [InlineData("//api")]
    [InlineData("/a b")]
    [InlineData("/api?x=1")]
    [InlineData("/api#top")]
    [InlineData("/../x")]
    [InlineData("/./x")]
    [InlineData("/api/..")]
    [InlineData("/%2e%2e")]
    [InlineData("/a//b")]
    public void A_prefix_is_a_clean_path_starting_with_a_slash(string prefix)
    {
        Assert.Contains("apps.api.path: must be / or a prefix like /api (segments of letters, digits, . _ ~ -, no trailing slash)",
            Errors($"apps:\n  web: {{ image: a, command: b, port: 80 }}\n  api: {{ path: \"{prefix}\", image: a, command: b, port: 81 }}"));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("volumes")]
    [InlineData("ports")]
    [InlineData("privileged")]
    [InlineData("user")]
    [InlineData("networks")]
    [InlineData("entrypoint")]
    public void Docker_options_are_refused_on_each_app(string key)
    {
        var errors = Errors($"apps:\n  web: {{ image: a, command: b, port: 80 }}\n  api: {{ path: /api, image: a, command: b, port: 81, {key}: x }}");

        Assert.Contains($"apps.api.{key}: not allowed; a preview definition only describes how to start the app, the hub decides what it may do", errors);
    }

    [Fact]
    public void Each_app_needs_image_command_and_port_and_its_own_checks()
    {
        var errors = Errors("apps:\n  web: { path: /, environment: { S: x }, generate: [S, \"bad name\"] }");

        Assert.Contains("apps.web.image: required", errors);
        Assert.Contains("apps.web.command: required", errors);
        Assert.Contains("apps.web.port: required", errors);
        Assert.Contains("apps.web.generate.S: also set in apps.web.environment", errors);
        Assert.Contains("apps.web.generate.bad name: not a variable name", errors);
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
