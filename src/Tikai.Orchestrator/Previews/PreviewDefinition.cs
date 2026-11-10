using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Tikai.Orchestrator.Previews;

/// <summary>
/// How to start a round's app, from <see cref="FileName"/> in the repository (written by agents, so untrusted). The hub
/// parses it into this model and never hands the file to Docker: it may only describe images, commands, a port, an
/// environment and health checks; limits, network and privileges come from the hub. Unknown keys are errors.
/// </summary>
/// <param name="Setup">Run in order before the app, each in its own container on the source, with internet.</param>
/// <param name="Services">Sidecars (stock images) on the preview's network, reachable by their name.</param>
public sealed record PreviewDefinition(
    IReadOnlyList<PreviewSetupStep> Setup,
    PreviewApp App,
    IReadOnlyList<PreviewService> Services,
    int IdleMinutes)
{
    /// <summary>At the repository root (not under .tikai/: it is part of the code and the agent keeps it current).</summary>
    public const string FileName = "tikai-preview.yml";

    public const int MaxBytes = 64 * 1024;
    public const int DefaultIdleMinutes = 30;
    public const int MinIdleMinutes = 5;

    /// <summary>A preview stops after 4 hours at most anyway.</summary>
    public const int MaxIdleMinutes = 240;

    public const int MaxSetupSteps = 10;
    public const int MaxServices = 5;
    public const int MaxEnvironment = 50;
    public const int MaxTextLength = 8 * 1024;

    /// <summary>Parses and checks the file; every problem is reported, with its path in the file.</summary>
    public static PreviewDefinitionParse Parse(string yaml) => new PreviewDefinitionParser().Parse(yaml);
}

public sealed record PreviewSetupStep(string Image, string Run);

/// <param name="Command">A shell command (run with /bin/sh -c).</param>
/// <param name="Generate">Variables the hub fills with a random value per preview.</param>
/// <param name="HealthcheckPath">The hub waits for a 2xx or 3xx answer here before it calls the preview running.</param>
public sealed record PreviewApp(
    string Image,
    string Command,
    int Port,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Generate,
    string? HealthcheckPath);

/// <param name="Command">Arguments for the image's own entrypoint (like compose's list form); null keeps the image's command.</param>
/// <param name="HealthcheckCommand">A shell command Docker runs in the container; the app starts once it succeeds.</param>
public sealed record PreviewService(
    string Name,
    string Image,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string>? Command,
    string? HealthcheckCommand);

/// <summary>Either a definition or the reasons there is none.</summary>
public sealed record PreviewDefinitionParse(PreviewDefinition? Definition, IReadOnlyList<string> Errors);

internal sealed partial class PreviewDefinitionParser
{
    private const string NotAllowed = "not allowed; a preview definition only describes how to start the app, the hub decides what it may do";

    private static readonly HashSet<string> _reservedNames = ["app", "hub"];

    private readonly List<string> _errors = [];

    public PreviewDefinitionParse Parse(string yaml)
    {
        if (Encoding.UTF8.GetByteCount(yaml) > PreviewDefinition.MaxBytes)
        {
            return Fail($"the file is larger than {PreviewDefinition.MaxBytes / 1024} KB");
        }
        YamlNode? root;
        try
        {
            // Aliases are refused before the tree is built: the model has no use for them, and they could make a
            // small file expand into a large tree.
            if (Scan(yaml) is { } problem)
            {
                return Fail(problem);
            }
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            root = stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode;
        }
        catch (YamlException ex)
        {
            var message = ex.InnerException is YamlException inner ? inner.Message : ex.Message;
            return Fail(message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                ? $"duplicate key (line {ex.Start.Line}, column {ex.Start.Column})"
                : $"not valid YAML (line {ex.Start.Line}, column {ex.Start.Column}): {message}");
        }
        if (root is null || root is YamlScalarNode { Value: null or "" })
        {
            return Fail("the file is empty");
        }
        if (root is not YamlMappingNode mapping)
        {
            return Fail("the file must be a mapping");
        }

        IReadOnlyList<PreviewSetupStep> setup = [];
        PreviewApp? app = null;
        IReadOnlyList<PreviewService> services = [];
        var idleMinutes = PreviewDefinition.DefaultIdleMinutes;
        foreach (var (key, value) in Entries(mapping, ""))
        {
            switch (key)
            {
                case "setup":
                    setup = ParseSetup(value);
                    break;
                case "app":
                    app = ParseApp(value);
                    break;
                case "services":
                    services = ParseServices(value);
                    break;
                case "idle_minutes":
                    idleMinutes = Number("idle_minutes", value, PreviewDefinition.MinIdleMinutes, PreviewDefinition.MaxIdleMinutes) ?? idleMinutes;
                    break;
                default:
                    _errors.Add($"{key}: {NotAllowed}");
                    break;
            }
        }
        if (!mapping.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value == "app"))
        {
            _errors.Add("app: required");
        }
        return _errors.Count > 0 || app is null
            ? new PreviewDefinitionParse(null, _errors)
            : new PreviewDefinitionParse(new PreviewDefinition(setup, app, services, idleMinutes), []);
    }

    private static string? Scan(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        var documents = 0;
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case DocumentStart:
                    if (++documents > 1)
                    {
                        return "the file must hold one document";
                    }
                    break;
                case AnchorAlias:
                case NodeEvent { Anchor.IsEmpty: false }:
                    return "anchors and aliases are not allowed";
            }
        }
        return null;
    }

    private List<PreviewSetupStep> ParseSetup(YamlNode node)
    {
        var steps = new List<PreviewSetupStep>();
        if (node is not YamlSequenceNode list)
        {
            _errors.Add("setup: must be a list");
            return steps;
        }
        if (list.Children.Count > PreviewDefinition.MaxSetupSteps)
        {
            _errors.Add($"setup: at most {PreviewDefinition.MaxSetupSteps} steps");
            return steps;
        }
        for (var i = 0; i < list.Children.Count; i++)
        {
            var path = $"setup[{i}]";
            if (list.Children[i] is not YamlMappingNode step)
            {
                _errors.Add($"{path}: must be a mapping with image and run");
                continue;
            }
            string? image = null, run = null;
            foreach (var (key, value) in Entries(step, path + "."))
            {
                switch (key)
                {
                    case "image":
                        image = Image($"{path}.image", value);
                        break;
                    case "run":
                        run = Text($"{path}.run", value);
                        break;
                    default:
                        _errors.Add($"{path}.{key}: {NotAllowed}");
                        break;
                }
            }
            if ((Required($"{path}.image", step, "image") & Required($"{path}.run", step, "run")) && image is not null && run is not null)
            {
                steps.Add(new PreviewSetupStep(image, run));
            }
        }
        return steps;
    }

    private PreviewApp? ParseApp(YamlNode node)
    {
        if (node is not YamlMappingNode app)
        {
            _errors.Add("app: must be a mapping");
            return null;
        }
        string? image = null, command = null, healthcheck = null;
        int? port = null;
        IReadOnlyDictionary<string, string> environment = new Dictionary<string, string>();
        IReadOnlyList<string> generate = [];
        foreach (var (key, value) in Entries(app, "app."))
        {
            switch (key)
            {
                case "image":
                    image = Image("app.image", value);
                    break;
                case "command":
                    command = Text("app.command", value);
                    break;
                case "port":
                    port = Number("app.port", value, 1, 65535);
                    break;
                case "environment":
                    environment = Environment("app.environment", value);
                    break;
                case "generate":
                    generate = Generate(value);
                    break;
                case "healthcheck":
                    healthcheck = Healthcheck("app.healthcheck", value, "path", HealthcheckPath);
                    break;
                default:
                    _errors.Add($"app.{key}: {NotAllowed}");
                    break;
            }
        }
        var complete = Required("app.image", app, "image") & Required("app.command", app, "command") & Required("app.port", app, "port");
        foreach (var name in generate.Where(environment.ContainsKey))
        {
            _errors.Add($"app.generate.{name}: also set in app.environment");
        }
        return complete && image is not null && command is not null && port is not null
            ? new PreviewApp(image, command, port.Value, environment, generate, healthcheck)
            : null;
    }

    private List<PreviewService> ParseServices(YamlNode node)
    {
        var services = new List<PreviewService>();
        if (node is not YamlMappingNode mapping)
        {
            _errors.Add("services: must be a mapping");
            return services;
        }
        if (mapping.Children.Count > PreviewDefinition.MaxServices)
        {
            _errors.Add($"services: at most {PreviewDefinition.MaxServices} services");
            return services;
        }
        foreach (var (name, value) in Entries(mapping, "services."))
        {
            var path = $"services.{name}";
            if (!ServiceName().IsMatch(name) || _reservedNames.Contains(name))
            {
                _errors.Add($"{path}: the name must be a lowercase host name (letters, digits, -) other than app and hub");
                continue;
            }
            if (value is not YamlMappingNode service)
            {
                _errors.Add($"{path}: must be a mapping");
                continue;
            }
            string? image = null, healthcheck = null;
            IReadOnlyList<string>? command = null;
            IReadOnlyDictionary<string, string> environment = new Dictionary<string, string>();
            foreach (var (key, field) in Entries(service, path + "."))
            {
                switch (key)
                {
                    case "image":
                        image = Image($"{path}.image", field);
                        break;
                    case "environment":
                        environment = Environment($"{path}.environment", field);
                        break;
                    case "command":
                        command = Arguments($"{path}.command", field);
                        break;
                    case "healthcheck":
                        healthcheck = Healthcheck($"{path}.healthcheck", field, "command", (p, v) => Text(p, v));
                        break;
                    default:
                        _errors.Add($"{path}.{key}: {NotAllowed}");
                        break;
                }
            }
            if (Required($"{path}.image", service, "image") && image is not null)
            {
                services.Add(new PreviewService(name, image, environment, command, healthcheck));
            }
        }
        return services;
    }

    private string? Healthcheck(string path, YamlNode node, string field, Func<string, YamlNode, string?> read)
    {
        if (node is not YamlMappingNode mapping)
        {
            _errors.Add($"{path}: must be a mapping with {field}");
            return null;
        }
        string? result = null;
        foreach (var (key, value) in Entries(mapping, path + "."))
        {
            if (key == field)
            {
                result = read($"{path}.{field}", value);
            }
            else
            {
                _errors.Add($"{path}.{key}: {NotAllowed}");
            }
        }
        Required($"{path}.{field}", mapping, field);
        return result;
    }

    private string? HealthcheckPath(string path, YamlNode node)
    {
        var text = Text(path, node);
        if (text is not null && !UrlPath().IsMatch(text))
        {
            _errors.Add($"{path}: must be a path starting with /");
            return null;
        }
        return text;
    }

    private Dictionary<string, string> Environment(string path, YamlNode node)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is not YamlMappingNode mapping)
        {
            _errors.Add($"{path}: must be a mapping");
            return environment;
        }
        if (mapping.Children.Count > PreviewDefinition.MaxEnvironment)
        {
            _errors.Add($"{path}: at most {PreviewDefinition.MaxEnvironment} variables");
            return environment;
        }
        foreach (var (name, value) in Entries(mapping, path + "."))
        {
            if (!VariableName().IsMatch(name))
            {
                _errors.Add($"{path}.{name}: not a variable name");
                continue;
            }
            // An empty value is a value: "KEY:" sets KEY to "".
            if (value is YamlScalarNode { Value: null or "" })
            {
                environment[name] = "";
            }
            else if (Text($"{path}.{name}", value) is { } text)
            {
                environment[name] = text;
            }
        }
        return environment;
    }

    private List<string> Generate(YamlNode node)
    {
        var names = new List<string>();
        if (node is not YamlSequenceNode list)
        {
            _errors.Add("app.generate: must be a list of variable names");
            return names;
        }
        foreach (var item in list.Children)
        {
            var name = item is YamlScalarNode scalar ? scalar.Value ?? "" : null;
            if (name is null || !VariableName().IsMatch(name))
            {
                _errors.Add($"app.generate.{name ?? "?"}: not a variable name");
            }
            else if (!names.Contains(name))
            {
                names.Add(name);
            }
        }
        return names;
    }

    private List<string>? Arguments(string path, YamlNode node)
    {
        if (node is not YamlSequenceNode list || list.Children.Any(c => c is not YamlScalarNode))
        {
            _errors.Add($"{path}: must be a list of arguments");
            return null;
        }
        var arguments = new List<string>();
        foreach (var item in list.Children.Cast<YamlScalarNode>())
        {
            var value = item.Value ?? "";
            if (!Plain(path, value))
            {
                return null;
            }
            arguments.Add(value);
        }
        return arguments;
    }

    private string? Image(string path, YamlNode node)
    {
        var text = Text(path, node);
        if (text is not null && (text.Length > 255 || !ImageReference().IsMatch(text)))
        {
            _errors.Add($"{path}: not an image reference");
            return null;
        }
        return text;
    }

    private int? Number(string path, YamlNode node, int min, int max)
    {
        if (node is YamlScalarNode { Value: { } value }
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= min && number <= max)
        {
            return number;
        }
        _errors.Add($"{path}: must be a whole number from {min} to {max}");
        return null;
    }

    /// <summary>A non-empty scalar (numbers and booleans as written).</summary>
    private string? Text(string path, YamlNode node)
    {
        if (node is not YamlScalarNode scalar)
        {
            _errors.Add($"{path}: must be text");
            return null;
        }
        if (string.IsNullOrEmpty(scalar.Value))
        {
            _errors.Add($"{path}: required");
            return null;
        }
        return Plain(path, scalar.Value) ? scalar.Value : null;
    }

    private bool Plain(string path, string value)
    {
        if (value.Length > PreviewDefinition.MaxTextLength)
        {
            _errors.Add($"{path}: longer than {PreviewDefinition.MaxTextLength} characters");
            return false;
        }
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            _errors.Add($"{path}: contains a NUL character");
            return false;
        }
        return true;
    }

    private bool Required(string path, YamlMappingNode mapping, string key)
    {
        if (mapping.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value == key))
        {
            return true;
        }
        _errors.Add($"{path}: required");
        return false;
    }

    /// <summary>The entries of a mapping with text keys; other keys are errors.</summary>
    private IEnumerable<(string Key, YamlNode Value)> Entries(YamlMappingNode mapping, string prefix)
    {
        foreach (var (key, value) in mapping.Children)
        {
            if (key is YamlScalarNode { Value: { } name })
            {
                yield return (name, value);
            }
            else
            {
                _errors.Add($"{prefix}?: keys must be text");
            }
        }
    }

    private static PreviewDefinitionParse Fail(string error) => new(null, [error]);

    // Docker's reference grammar, a little stricter: optional registry (host[:port]/), lowercase path components,
    // optional tag, optional sha256 digest. Nothing that could be read as an option.
    [GeneratedRegex(@"^(?:[a-z0-9]+(?:[.-][a-z0-9]+)*(?::[0-9]{1,5})?/)?[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*(?::[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?(?:@sha256:[0-9a-f]{64})?$")]
    private static partial Regex ImageReference();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex VariableName();

    [GeneratedRegex("^[a-z][a-z0-9-]{0,30}$")]
    private static partial Regex ServiceName();

    [GeneratedRegex(@"^/(?!/)[A-Za-z0-9._~!$&'()*+,;=:@%/?-]{0,500}$")]
    private static partial Regex UrlPath();
}
