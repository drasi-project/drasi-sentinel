using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Drasi.Sentinel.Host;

public sealed record AgentManifest(Uri Url, string Model);
public enum ServiceProfile
{
    Demo,
    Development,
    Production,
}

public abstract record A2aAuthenticationManifest;
public sealed record A2aJwtManifest(string Issuer, string Audience, string PublicKeyPem)
    : A2aAuthenticationManifest;
public sealed record A2aAnonymousManifest : A2aAuthenticationManifest;

public sealed class SentinelManifest
{
    internal SentinelManifest(
        AgentManifest? agent,
        ServiceProfile profile,
        A2aAuthenticationManifest a2aAuthentication,
        SafeCatalog safeCatalog,
        ExecutableCatalog executableCatalog,
        string directory,
        bool queryAuthoringEnabled,
        IReadOnlyList<string> plugins,
        SecretStoreManifest? secretStore,
        string? dataDirectory = null,
        TtlPolicy? ttlPolicy = null)
    {
        Agent = agent;
        Profile = profile;
        A2aAuthentication = a2aAuthentication;
        SafeCatalog = safeCatalog;
        ExecutableCatalog = executableCatalog;
        Directory = directory;
        QueryAuthoringEnabled = queryAuthoringEnabled;
        Plugins = plugins;
        SecretStore = secretStore;
        DataDirectory = dataDirectory;
        TtlPolicy = ttlPolicy ?? TtlPolicy.Uncapped;
    }

    public AgentManifest? Agent { get; }
    public ServiceProfile Profile { get; }
    public A2aAuthenticationManifest A2aAuthentication { get; }
    public SafeCatalog SafeCatalog { get; }
    public bool QueryAuthoringEnabled { get; }
    public IReadOnlyList<string> Plugins { get; }
    internal SecretStoreManifest? SecretStore { get; }
    internal ExecutableCatalog ExecutableCatalog { get; }
    internal string Directory { get; }
    internal string? DataDirectory { get; }
    public TtlPolicy TtlPolicy { get; }
}

public sealed class ManifestLoadException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed record ExecutableSource(
    string Id,
    string Kind,
    JsonObject Configuration,
    JsonObject? Bootstrap);
internal sealed record ExecutableQuery(QueryId Id, string Cypher, IReadOnlyList<string> SourceIds);
internal sealed record ExecutableCatalog(
    IReadOnlyDictionary<string, ExecutableSource> Sources,
    IReadOnlyDictionary<QueryId, ExecutableQuery> Queries);
internal sealed record SecretStoreManifest(string Kind, JsonObject Configuration);

public static class SentinelManifestLoader
{
    private static readonly Regex EnvironmentName = new("^[A-Z][A-Z0-9_]*$", RegexOptions.Compiled);

    public static SentinelManifest Load(string manifestPath)
    {
        if (!IsYamlPath(manifestPath))
        {
            throw new ManifestLoadException("The manifest path must end in .yaml or .yml.");
        }

        string yaml;
        try
        {
            yaml = File.ReadAllText(manifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ManifestLoadException($"The manifest '{manifestPath}' could not be read.", exception);
        }

        RejectDuplicateKeys(yaml);
        var document = ParseSingleDocument(yaml);
        var root = RequireMapping(document.RootNode, "manifest");
        RequireOnlyKeys(root, "manifest", "agent", "service", "plugins", "secretStore", "sources", "queries", "reactions");
        RejectUnexpectedEnvironmentReferences(root, "manifest", skipAgentReferences: true);

        var plugins = ReadPlugins(root);
        var secretStore = ReadSecretStore(root, manifestPath, plugins);
        var agent = ReadAgent(root);
        var service = RequireMapping(Required(root, "service", "manifest"), "service");
        var profile = ReadProfile(service);
        var queryAuthoringEnabled = ReadQueryAuthoring(service);
        var ttlPolicy = ReadMaxTtl(service);
        var dataDirectory = ReadDataDirectory(service, manifestPath);
        var a2aAuthentication = ReadA2aAuthentication(service, profile, secretStore);
        var sources = ReadEntries(root, "sources")
            .Select(entry => ReadSource(entry, manifestPath))
            .ToDictionary(source => source.Id, StringComparer.Ordinal);
        var reactions = ReadReactions(root);
        var queries = ReadQueries(root, sources, reactions);
        var catalog = new SafeCatalog(
            queries.ToDictionary(
                query => query.Key,
                query => new QueryCatalogItem(
                    query.Key,
                    query.Value.Description,
                    query.Value.AllowedReactions,
                    query.Value.Examples)),
            reactions.ToDictionary(
                reaction => reaction.Key,
                reaction => new ReactionCatalogItem(reaction.Key)));

        var unbound = CatalogBinding.From(catalog).OfType<UnboundQuery>().FirstOrDefault();
        if (unbound is not null)
            throw new ManifestLoadException(unbound.Reason switch
            {
                UnboundQueryReason.UnknownReaction =>
                    $"Query '{unbound.Query.Value}' allows unknown reaction '{unbound.Missing?.Value}'.",
                _ => $"Query '{unbound.Query.Value}' must allow exactly one reaction.",
            });
        if (queryAuthoringEnabled && reactions.Count != 1)
            throw new ManifestLoadException("service.query_authoring.enabled requires exactly one reaction.");
        if (sources.Count > 0 && plugins.Count == 0)
            throw new ManifestLoadException("plugins must list the source and bootstrap plugins to install.");
        foreach (var source in sources.Values)
        {
            RequireSecretReferences(source.Configuration, $"sources.{source.Id}.config", secretStore);
            if (source.Bootstrap is not null)
                RequireSecretReferences(source.Bootstrap, $"sources.{source.Id}.bootstrap", secretStore);
        }

        return new SentinelManifest(
            agent,
            profile,
            a2aAuthentication,
            catalog,
            new ExecutableCatalog(
                sources,
                queries.ToDictionary(
                    query => query.Key,
                    query => new ExecutableQuery(query.Key, query.Value.Cypher, query.Value.SourceIds))),
            Path.GetDirectoryName(Path.GetFullPath(manifestPath))
                ?? throw new ManifestLoadException("The manifest path has no parent directory."),
            queryAuthoringEnabled,
            plugins,
            secretStore?.Manifest,
            dataDirectory,
            ttlPolicy);
    }

    private static IReadOnlyList<string> ReadPlugins(YamlMappingNode root)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("plugins"), out var node))
            return [];
        if (node is not YamlSequenceNode sequence)
            throw new ManifestLoadException("plugins must be a list of ref entries.");

        var references = new List<string>();
        var index = 0;
        foreach (var item in sequence)
        {
            var path = $"plugins[{index}]";
            var mapping = RequireMapping(item, path);
            RequireOnlyKeys(mapping, path, "ref");
            var reference = RequiredScalar(mapping, "ref", path).Trim();
            if (reference.Length == 0)
                throw new ManifestLoadException($"{path}.ref must not be empty.");
            if (references.Contains(reference, StringComparer.Ordinal))
                throw new ManifestLoadException($"Duplicate plugin ref '{reference}' is not allowed.");
            references.Add(reference);
            index++;
        }

        return references;
    }

    private static bool IsYamlPath(string path) =>
        path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    private static ExecutableSource ReadSource(Entry entry, string manifestPath)
    {
        var path = $"sources.{entry.Id}";
        var hasConfig = entry.Configuration.Children.TryGetValue(new YamlScalarNode("config"), out var configNode);
        if (hasConfig)
        {
            RequireOnlyKeys(entry.Configuration, path, "id", "kind", "config", "bootstrap");
        }

        var configuration = hasConfig
            ? ReadPluginConfiguration(
                RequireMapping(configNode!, $"{path}.config"),
                $"{path}.config",
                manifestPath)
            : ReadLegacyPluginConfiguration(entry.Configuration, path, manifestPath);
        var bootstrap = entry.Configuration.Children.TryGetValue(new YamlScalarNode("bootstrap"), out var bootstrapNode)
            ? ReadPluginConfiguration(
                RequireMapping(bootstrapNode, $"{path}.bootstrap"),
                $"{path}.bootstrap",
                manifestPath)
            : null;
        return new ExecutableSource(
            entry.Id,
            RequiredScalar(entry.Configuration, "kind", path),
            configuration,
            bootstrap);
    }

    private static JsonObject ReadLegacyPluginConfiguration(
        YamlMappingNode mapping,
        string path,
        string manifestPath)
    {
        var result = new JsonObject();
        foreach (var pair in mapping.Children)
        {
            var key = ScalarKey(pair.Key, path);
            if (key is "id" or "kind" or "bootstrap")
            {
                continue;
            }

            result[key] = ReadPluginValue(pair.Value, $"{path}.{key}", manifestPath);
        }
        return result;
    }

    private static JsonObject ReadPluginConfiguration(
        YamlMappingNode mapping,
        string path,
        string manifestPath)
    {
        if (TryScalar(mapping, "kind", out var kind) && StringComparer.Ordinal.Equals(kind, "file"))
        {
            throw new ManifestLoadException(
                $"{path}.kind 'file' is not a Drasi config value. Use a plain value, ${{VAR}}, kind: Secret, or kind: EnvironmentVariable.");
        }

        var result = new JsonObject();
        foreach (var pair in mapping.Children)
        {
            var key = ScalarKey(pair.Key, path);
            result[key] = ReadPluginValue(pair.Value, $"{path}.{key}", manifestPath);
        }
        return result;
    }

    private static JsonNode? ReadPluginValue(YamlNode node, string path, string manifestPath) =>
        node switch
        {
            YamlMappingNode mapping => ReadPluginConfiguration(mapping, path, manifestPath),
            YamlSequenceNode sequence => new JsonArray(
                sequence.Children
                    .Select((child, index) => ReadPluginValue(child, $"{path}[{index}]", manifestPath))
                    .ToArray()),
            YamlScalarNode { Value: { } value } => ReadPluginScalar(value),
            _ => throw new ManifestLoadException($"{path} has an unsupported YAML value."),
        };

    private static JsonNode ReadPluginScalar(string value)
    {
        if (bool.TryParse(value, out var flag))
        {
            return JsonValue.Create(flag)!;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return JsonValue.Create(integer)!;
        }

        return JsonValue.Create(value)!;
    }

    private static void RejectDuplicateKeys(string yaml)
    {
        try
        {
            var parser = new Parser(new StringReader(yaml));
            var containers = new Stack<ContainerState>();
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case Scalar scalar:
                        ConsumeScalar(containers, scalar.Value ?? string.Empty);
                        break;
                    case MappingStart:
                        ConsumeContainer(containers);
                        containers.Push(new MappingState());
                        break;
                    case SequenceStart:
                        ConsumeContainer(containers);
                        containers.Push(new SequenceState());
                        break;
                    case MappingEnd:
                    case SequenceEnd:
                        containers.Pop();
                        break;
                    case AnchorAlias:
                        throw new ManifestLoadException("YAML aliases are not supported in manifests.");
                }
            }

            if (containers.Count != 0)
            {
                throw new ManifestLoadException("The YAML manifest has an incomplete mapping.");
            }
        }
        catch (YamlException exception)
        {
            throw new ManifestLoadException($"The YAML manifest is invalid: {exception.Message}", exception);
        }
    }

    private static void ConsumeScalar(Stack<ContainerState> containers, string value)
    {
        if (!containers.TryPeek(out var container) || container is not MappingState mapping)
        {
            return;
        }

        if (mapping.ExpectingKey)
        {
            if (!mapping.Keys.Add(value))
            {
                throw new ManifestLoadException($"Duplicate YAML key '{value}' is not allowed.");
            }

            mapping.ExpectingKey = false;
            return;
        }

        mapping.ExpectingKey = true;
    }

    private static void ConsumeContainer(Stack<ContainerState> containers)
    {
        if (!containers.TryPeek(out var container) || container is not MappingState mapping)
        {
            return;
        }

        if (mapping.ExpectingKey)
        {
            throw new ManifestLoadException("YAML mapping keys must be scalars.");
        }

        mapping.ExpectingKey = true;
    }

    private static YamlDocument ParseSingleDocument(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1)
            {
                throw new ManifestLoadException("A manifest must contain exactly one YAML document.");
            }

            return stream.Documents[0];
        }
        catch (YamlException exception)
        {
            throw new ManifestLoadException($"The YAML manifest is invalid: {exception.Message}", exception);
        }
    }

    private static AgentManifest? ReadAgent(YamlMappingNode root)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("agent"), out var agentNode))
        {
            return null;
        }

        var agent = RequireMapping(agentNode, "agent");
        RequireOnlyKeys(agent, "agent", "url", "model");
        var url = ResolveEnvironmentReference(Required(agent, "url", "agent"), "agent.url");
        var model = ResolveEnvironmentReference(Required(agent, "model", "agent"), "agent.model");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
        {
            throw new ManifestLoadException("agent.url must resolve to an absolute URL.");
        }

        return new AgentManifest(endpoint, model);
    }

    private static ServiceProfile ReadProfile(YamlMappingNode service)
    {
        RequireOnlyKeys(service, "service", "profile", "a2a", "query_authoring", "data", "max_ttl");
        return RequiredScalar(service, "profile", "service") switch
        {
            "demo" => ServiceProfile.Demo,
            "development" => ServiceProfile.Development,
            "production" => ServiceProfile.Production,
            _ => throw new ManifestLoadException(
                "service.profile must be one of 'demo', 'development', or 'production'."),
        };
    }

    private static TtlPolicy ReadMaxTtl(YamlMappingNode service)
    {
        if (!service.Children.TryGetValue(new YamlScalarNode("max_ttl"), out var node))
            return TtlPolicy.Uncapped;
        var token = node is YamlScalarNode scalar ? scalar.Value : null;
        if (string.IsNullOrWhiteSpace(token))
            throw new ManifestLoadException("service.max_ttl must be none or a positive integer plus s, m, h, or d.");
        try
        {
            return new TtlPolicy(TtlToken.ParseMaximum(token));
        }
        catch (FormatException exception)
        {
            throw new ManifestLoadException(exception.Message, exception);
        }
    }

    private static bool ReadQueryAuthoring(YamlMappingNode service)
    {
        if (!service.Children.TryGetValue(new YamlScalarNode("query_authoring"), out var node))
            return false;
        var authoring = RequireMapping(node, "service.query_authoring");
        RequireOnlyKeys(authoring, "service.query_authoring", "enabled");
        return RequiredScalar(authoring, "enabled", "service.query_authoring") switch
        {
            "true" => true,
            "false" => false,
            _ => throw new ManifestLoadException("service.query_authoring.enabled must be true or false."),
        };
    }

    private static string? ReadDataDirectory(YamlMappingNode service, string manifestPath)
    {
        if (!service.Children.TryGetValue(new YamlScalarNode("data"), out var node))
            return null;
        if (node is not YamlScalarNode { Value: { Length: > 0 } value })
            throw new ManifestLoadException("service.data must be a non-empty path.");
        return ResolveManifestPath(value, manifestPath);
    }

    private static A2aAuthenticationManifest ReadA2aAuthentication(
        YamlMappingNode service,
        ServiceProfile profile,
        LoadedSecretStore? secretStore)
    {
        var a2a = RequireMapping(Required(service, "a2a", "service"), "service.a2a");
        RequireOnlyKeys(a2a, "service.a2a", "authentication");
        var authentication = RequireMapping(
            Required(a2a, "authentication", "service.a2a"),
            "service.a2a.authentication");
        var mode = RequiredScalar(authentication, "mode", "service.a2a.authentication");
        return mode switch
        {
            "jwt" => ReadA2aJwt(authentication, secretStore),
            "anonymous" => ReadA2aAnonymous(authentication, profile),
            _ => throw new ManifestLoadException(
                "service.a2a.authentication.mode must be either 'jwt' or 'anonymous'."),
        };
    }

    private static A2aJwtManifest ReadA2aJwt(YamlMappingNode authentication, LoadedSecretStore? secretStore)
    {
        RequireOnlyKeys(
            authentication,
            "service.a2a.authentication",
            "mode",
            "issuer",
            "audience",
            "public_key");
        var publicKey = RequireMapping(
            Required(authentication, "public_key", "service.a2a.authentication"),
            "service.a2a.authentication.public_key");
        return new A2aJwtManifest(
            RequiredScalar(authentication, "issuer", "service.a2a.authentication"),
            RequiredScalar(authentication, "audience", "service.a2a.authentication"),
            ReadHostConfigValue(publicKey, "service.a2a.authentication.public_key", secretStore));
    }

    private static A2aAnonymousManifest ReadA2aAnonymous(
        YamlMappingNode authentication,
        ServiceProfile profile)
    {
        RequireOnlyKeys(authentication, "service.a2a.authentication", "mode");
        if (profile != ServiceProfile.Demo)
        {
            throw new ManifestLoadException(
                "service.a2a.authentication.mode 'anonymous' is valid only when service.profile is 'demo'.");
        }

        return new A2aAnonymousManifest();
    }

    private sealed record LoadedSecretStore(
        SecretStoreManifest Manifest,
        IReadOnlyDictionary<string, string>? FileSecrets);

    private static LoadedSecretStore? ReadSecretStore(
        YamlMappingNode root,
        string manifestPath,
        IReadOnlyList<string> plugins)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("secretStore"), out var node))
            return null;

        var mapping = RequireMapping(node, "secretStore");
        var kind = RequiredScalar(mapping, "kind", "secretStore");
        var reference = $"secret-store/{kind}";
        if (!plugins.Contains(reference, StringComparer.Ordinal))
        {
            throw new ManifestLoadException(
                $"plugins must include '{reference}' when secretStore.kind is '{kind}'.");
        }

        var configuration = new JsonObject();
        foreach (var pair in mapping.Children)
        {
            var key = ScalarKey(pair.Key, "secretStore");
            if (StringComparer.Ordinal.Equals(key, "kind"))
                continue;
            configuration[key] = ReadPluginValue(pair.Value, $"secretStore.{key}", manifestPath);
        }

        IReadOnlyDictionary<string, string>? fileSecrets = null;
        if (StringComparer.Ordinal.Equals(kind, "file"))
        {
            if (configuration["path"] is not JsonValue pathValue ||
                pathValue.GetValueKind() != JsonValueKind.String)
            {
                throw new ManifestLoadException("secretStore.path is required when secretStore.kind is file.");
            }

            var path = ResolveManifestPath(pathValue.GetValue<string>(), manifestPath);
            EnsurePrivateRegularFile(path, "secret store");
            configuration["path"] = path;
            fileSecrets = ReadFileSecrets(path);
        }

        return new LoadedSecretStore(new SecretStoreManifest(kind, configuration), fileSecrets);
    }

    private static IReadOnlyDictionary<string, string> ReadFileSecrets(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ManifestLoadException($"The secret store file '{path}' could not be read.", exception);
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ManifestLoadException($"The secret store file '{path}' is not a JSON object.", exception);
        }

        if (parsed is not JsonObject secrets)
            throw new ManifestLoadException($"The secret store file '{path}' must be a JSON object of strings.");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in secrets)
        {
            if (pair.Value is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
                throw new ManifestLoadException($"Secret '{pair.Key}' in '{path}' must be a string.");
            result[pair.Key] = value.GetValue<string>();
        }

        return result;
    }

    private static string ReadHostConfigValue(
        YamlMappingNode node,
        string path,
        LoadedSecretStore? secretStore)
    {
        var kind = RequiredScalar(node, "kind", path);
        switch (kind)
        {
            case "Secret":
                RequireOnlyKeys(node, path, "kind", "name");
                var name = RequiredScalar(node, "name", path);
                if (secretStore?.FileSecrets is not { } secrets)
                {
                    throw new ManifestLoadException(
                        $"{path} kind Secret requires secretStore.kind file. The host reads that file because the engine does not expose secret lookup.");
                }

                return secrets.TryGetValue(name, out var value)
                    ? value
                    : throw new ManifestLoadException($"{path} secret '{name}' is not in the file secret store.");
            case "EnvironmentVariable":
                RequireOnlyKeys(node, path, "kind", "name", "default");
                var variable = RequiredScalar(node, "name", path);
                if (!EnvironmentName.IsMatch(variable))
                {
                    throw new ManifestLoadException(
                        $"{path}.name must be an uppercase identifier containing only A-Z, 0-9, and _.");
                }

                var environment = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrWhiteSpace(environment))
                    return environment;
                if (TryScalar(node, "default", out var fallback))
                    return fallback;
                throw new ManifestLoadException($"Environment variable '{variable}' for {path} is not set.");
            case "file":
                throw new ManifestLoadException(
                    $"{path}.kind 'file' is not a Drasi config value. Use kind: Secret or kind: EnvironmentVariable.");
            default:
                throw new ManifestLoadException($"{path}.kind must be Secret or EnvironmentVariable.");
        }
    }

    private static void RequireSecretReferences(JsonNode? node, string path, LoadedSecretStore? secretStore)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["kind"] is JsonValue kind &&
                    kind.GetValueKind() == JsonValueKind.String &&
                    StringComparer.Ordinal.Equals(kind.GetValue<string>(), "Secret"))
                {
                    if (secretStore is null)
                        throw new ManifestLoadException($"{path} uses kind Secret, which requires secretStore.");
                    if (obj["name"] is not JsonValue name ||
                        name.GetValueKind() != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(name.GetValue<string>()))
                    {
                        throw new ManifestLoadException($"{path}.name is required.");
                    }

                    if (secretStore.FileSecrets is { } secrets &&
                        !secrets.ContainsKey(name.GetValue<string>()))
                    {
                        throw new ManifestLoadException(
                            $"{path} secret '{name.GetValue<string>()}' is not in the file secret store.");
                    }
                }

                foreach (var pair in obj)
                    RequireSecretReferences(pair.Value, $"{path}.{pair.Key}", secretStore);
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                    RequireSecretReferences(array[index], $"{path}[{index}]", secretStore);
                break;
        }
    }

    private static string ResolveManifestPath(string configuredPath, string manifestPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))
            ?? throw new ManifestLoadException("The manifest path has no parent directory.");
        return Path.GetFullPath(configuredPath, directory);
    }

    private static void EnsurePrivateRegularFile(string path, string description)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & FileAttributes.Directory) != 0)
            {
                throw new ManifestLoadException($"The {description} file '{path}' must be a regular file.");
            }

            if (file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ManifestLoadException($"The {description} file '{path}' may not be a symlink.");
            }

            if (OperatingSystem.IsWindows())
            {
                throw new ManifestLoadException(
                    $"{char.ToUpperInvariant(description[0])}{description[1..]} file permissions cannot be verified on Windows.");
            }

            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0)
            {
                throw new ManifestLoadException(
                    $"The {description} file '{path}' may not be readable by group or others.");
            }
        }
        catch (ManifestLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new ManifestLoadException(
                $"The {description} file '{path}' could not be inspected.",
                exception);
        }
    }

    private static Dictionary<QueryId, QueryDefinition> ReadQueries(
        YamlMappingNode root,
        IReadOnlyDictionary<string, ExecutableSource> sources,
        IReadOnlyDictionary<ReactionId, ReactionDefinition> reactions)
    {
        var queries = new Dictionary<QueryId, QueryDefinition>();
        foreach (var entry in ReadEntries(root, "queries"))
        {
            var id = new QueryId(entry.Id);
            RequireOnlyKeys(
                entry.Configuration,
                $"queries.{entry.Id}",
                "id",
                "description",
                "cypher",
                "sources",
                "source_ids",
                "reactions",
                "allowed_reactions",
                "examples");
            var description = RequiredScalar(entry.Configuration, "description", $"queries.{entry.Id}");
            var cypher = RequiredScalar(entry.Configuration, "cypher", $"queries.{entry.Id}");
            var examples = OptionalStringList(entry.Configuration, $"queries.{entry.Id}", "examples");
            var sourceIds = StringList(entry.Configuration, $"queries.{entry.Id}", "sources", "source_ids");
            var allowedReactions = StringList(entry.Configuration, $"queries.{entry.Id}", "reactions", "allowed_reactions")
                .Select(value => new ReactionId(value))
                .ToHashSet();

            foreach (var sourceId in sourceIds)
            {
                if (!sources.ContainsKey(sourceId))
                {
                    throw new ManifestLoadException($"queries.{entry.Id} references unknown source '{sourceId}'.");
                }
            }

            foreach (var reactionId in allowedReactions)
            {
                if (!reactions.ContainsKey(reactionId))
                {
                    throw new ManifestLoadException($"queries.{entry.Id} references unknown reaction '{reactionId.Value}'.");
                }
            }

            if (!queries.TryAdd(id, new QueryDefinition(description, cypher, sourceIds, allowedReactions, examples)))
            {
                throw new ManifestLoadException($"Duplicate query id '{entry.Id}' is not allowed.");
            }
        }

        return queries;
    }

    private static Dictionary<ReactionId, ReactionDefinition> ReadReactions(YamlMappingNode root)
    {
        var reactions = new Dictionary<ReactionId, ReactionDefinition>();
        foreach (var entry in ReadEntries(root, "reactions"))
        {
            var id = new ReactionId(entry.Id);
            RequireOnlyKeys(entry.Configuration, $"reactions.{entry.Id}", "id");
            if (!reactions.TryAdd(id, new ReactionDefinition()))
            {
                throw new ManifestLoadException($"Duplicate reaction id '{entry.Id}' is not allowed.");
            }
        }

        return reactions;
    }


    private static IReadOnlyList<Entry> ReadEntries(YamlMappingNode root, string section)
    {
        var node = Required(root, section, "manifest");
        var entries = new List<Entry>();
        switch (node)
        {
            case YamlSequenceNode sequence:
                foreach (var item in sequence.Children)
                {
                    var configuration = RequireMapping(item, section);
                    entries.Add(new Entry(RequiredScalar(configuration, "id", section), configuration));
                }
                break;
            case YamlMappingNode mapping:
                foreach (var pair in mapping.Children)
                {
                    var id = ScalarKey(pair.Key, section);
                    var configuration = RequireMapping(pair.Value, $"{section}.{id}");
                    if (TryScalar(configuration, "id", out var explicitId) &&
                        !StringComparer.Ordinal.Equals(id, explicitId))
                    {
                        throw new ManifestLoadException($"{section}.{id}.id must match its mapping key.");
                    }

                    entries.Add(new Entry(id, configuration));
                }
                break;
            default:
                throw new ManifestLoadException($"{section} must be a YAML sequence or mapping.");
        }

        var duplicate = entries
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ManifestLoadException($"Duplicate {section} id '{duplicate.Key}' is not allowed.");
        }

        return entries;
    }

    private static string ResolveEnvironmentReference(YamlNode node, string path)
    {
        var reference = RequireMapping(node, path);
        RequireOnlyKeys(reference, path, "env");
        var name = RequiredScalar(reference, "env", path);
        if (!EnvironmentName.IsMatch(name))
        {
            throw new ManifestLoadException(
                $"{path}.env must be an uppercase identifier containing only A-Z, 0-9, and _.");
        }

        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ManifestLoadException($"Environment variable '{name}' for {path} is not set.");
        }

        return value;
    }

    private static void RejectUnexpectedEnvironmentReferences(
        YamlNode node,
        string path,
        bool skipAgentReferences)
    {
        if (node is YamlMappingNode mapping)
        {
            if (IsEnvironmentReference(mapping))
            {
                throw new ManifestLoadException($"{path} may not use an environment reference.");
            }

            foreach (var pair in mapping.Children)
            {
                var key = ScalarKey(pair.Key, path);
                if (skipAgentReferences &&
                    StringComparer.Ordinal.Equals(path, "manifest") &&
                    StringComparer.Ordinal.Equals(key, "agent"))
                {
                    continue;
                }

                RejectUnexpectedEnvironmentReferences(pair.Value, $"{path}.{key}", false);
            }
            return;
        }

        if (node is YamlSequenceNode sequence)
        {
            for (var index = 0; index < sequence.Children.Count; index++)
            {
                RejectUnexpectedEnvironmentReferences(sequence.Children[index], $"{path}[{index}]", false);
            }
        }
    }

    private static bool IsEnvironmentReference(YamlMappingNode mapping) =>
        mapping.Children.Count == 1 &&
        mapping.Children.Keys.Single() is YamlScalarNode { Value: "env" };

    private static YamlMappingNode RequireMapping(YamlNode node, string path) =>
        node as YamlMappingNode ??
        throw new ManifestLoadException($"{path} must be a YAML mapping.");

    private static YamlNode Required(YamlMappingNode mapping, string key, string path)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode(key), out var value))
        {
            throw new ManifestLoadException($"{path}.{key} is required.");
        }

        return value;
    }

    private static string RequiredScalar(YamlMappingNode mapping, string key, string path) =>
        TryScalar(mapping, key, out var value)
            ? value
            : throw new ManifestLoadException($"{path}.{key} must be a non-empty scalar.");

    private static bool TryScalar(YamlMappingNode mapping, string key, out string value)
    {
        if (mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) &&
            node is YamlScalarNode { Value: { Length: > 0 } scalarValue })
        {
            value = scalarValue;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static IReadOnlyList<string> StringList(YamlMappingNode mapping, string path, params string[] keys) =>
        OptionalStringList(mapping, path, keys) is { Count: > 0 } values
            ? values
            : throw new ManifestLoadException($"{path}.{keys[0]} must be a non-empty YAML sequence.");

    private static IReadOnlyList<string> OptionalStringList(
        YamlMappingNode mapping,
        string path,
        params string[] keys)
    {
        var matchingKeys = keys.Where(key => mapping.Children.ContainsKey(new YamlScalarNode(key))).ToArray();
        if (matchingKeys.Length > 1)
        {
            throw new ManifestLoadException($"{path} must use only one of {string.Join(", ", keys)}.");
        }
        if (matchingKeys.Length == 0)
        {
            return [];
        }

        if (mapping.Children[new YamlScalarNode(matchingKeys[0])] is not YamlSequenceNode sequence)
        {
            throw new ManifestLoadException($"{path}.{matchingKeys[0]} must be a YAML sequence.");
        }

        var values = new List<string>();
        foreach (var child in sequence.Children)
        {
            if (child is not YamlScalarNode { Value: { Length: > 0 } value })
            {
                throw new ManifestLoadException($"{path}.{matchingKeys[0]} must contain non-empty scalar values.");
            }
            values.Add(value);
        }
        return values;
    }

    private static string ScalarKey(YamlNode node, string path) =>
        node is YamlScalarNode { Value: { Length: > 0 } value }
            ? value
            : throw new ManifestLoadException($"{path} has a non-scalar or empty YAML key.");

    private static void RequireOnlyKeys(YamlMappingNode mapping, string path, params string[] expected)
    {
        var allowed = expected.ToHashSet(StringComparer.Ordinal);
        foreach (var key in mapping.Children.Keys.Select(node => ScalarKey(node, path)))
        {
            if (!allowed.Contains(key))
            {
                throw new ManifestLoadException($"{path}.{key} is not supported.");
            }
        }
    }

    private abstract class ContainerState
    {
    }

    private sealed class MappingState : ContainerState
    {
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
        public bool ExpectingKey { get; set; } = true;
    }

    private sealed class SequenceState : ContainerState
    {
    }

    private sealed record Entry(string Id, YamlMappingNode Configuration);
    private sealed record QueryDefinition(
        string Description,
        string Cypher,
        IReadOnlyList<string> SourceIds,
        IReadOnlySet<ReactionId> AllowedReactions,
        IReadOnlyList<string> Examples);
    private sealed record ReactionDefinition;
}
