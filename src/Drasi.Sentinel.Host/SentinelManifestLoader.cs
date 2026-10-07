using System.Globalization;
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
        string databaseConnectionString,
        string directory)
    {
        Agent = agent;
        Profile = profile;
        A2aAuthentication = a2aAuthentication;
        SafeCatalog = safeCatalog;
        ExecutableCatalog = executableCatalog;
        DatabaseConnectionString = databaseConnectionString;
        Directory = directory;
    }

    public AgentManifest? Agent { get; }
    public ServiceProfile Profile { get; }
    public A2aAuthenticationManifest A2aAuthentication { get; }
    public SafeCatalog SafeCatalog { get; }
    internal ExecutableCatalog ExecutableCatalog { get; }
    internal string DatabaseConnectionString { get; }
    internal string Directory { get; }
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
        RequireOnlyKeys(root, "manifest", "agent", "service", "sources", "queries", "reactions");
        RejectUnexpectedEnvironmentReferences(root, "manifest", skipAgentReferences: true);

        var agent = ReadAgent(root);
        var service = RequireMapping(Required(root, "service", "manifest"), "service");
        var profile = ReadProfile(service);
        var databaseConnectionString = ReadDatabaseConnectionString(service, manifestPath);
        var a2aAuthentication = ReadA2aAuthentication(service, profile, manifestPath);
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
                    query.Value.AllowedReactions)),
            reactions.ToDictionary(
                reaction => reaction.Key,
                reaction => new ReactionCatalogItem(reaction.Key)));

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
            databaseConnectionString,
            Path.GetDirectoryName(Path.GetFullPath(manifestPath))
                ?? throw new ManifestLoadException("The manifest path has no parent directory."));
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
        var result = new JsonObject();
        foreach (var pair in mapping.Children)
        {
            var key = ScalarKey(pair.Key, path);
            result[key] = StringComparer.Ordinal.Equals(key, "password") &&
                          pair.Value is YamlMappingNode passwordReference
                ? ReadPluginPassword(passwordReference, $"{path}.password", manifestPath)
                : StringComparer.Ordinal.Equals(key, "port") &&
                  pair.Value is YamlMappingNode portReference
                    ? ReadPluginPort(portReference, $"{path}.port", manifestPath)
                : ReadPluginValue(pair.Value, $"{path}.{key}", manifestPath);
        }
        return result;
    }

    private static string ReadPluginPassword(
        YamlMappingNode reference,
        string path,
        string manifestPath)
    {
        RequireOnlyKeys(reference, path, "kind", "path");
        if (!TryScalar(reference, "kind", out var kind) ||
            !StringComparer.Ordinal.Equals(kind, "file"))
        {
            throw new ManifestLoadException($"{path}.kind must be 'file'.");
        }

        return ReadSecretFile(
            RequiredScalar(reference, "path", path),
            manifestPath,
            "PostgreSQL source password");
    }

    private static long ReadPluginPort(
        YamlMappingNode reference,
        string path,
        string manifestPath)
    {
        RequireOnlyKeys(reference, path, "kind", "path");
        if (!TryScalar(reference, "kind", out var kind) ||
            !StringComparer.Ordinal.Equals(kind, "file"))
        {
            throw new ManifestLoadException($"{path}.kind must be 'file'.");
        }

        var value = ReadSecretFile(
            RequiredScalar(reference, "path", path),
            manifestPath,
            "PostgreSQL source port");
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            throw new ManifestLoadException($"{path} must resolve to a TCP port between 1 and 65535.");
        }

        return port;
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
        RequireOnlyKeys(service, "service", "profile", "database", "a2a");
        return RequiredScalar(service, "profile", "service") switch
        {
            "demo" => ServiceProfile.Demo,
            "development" => ServiceProfile.Development,
            "production" => ServiceProfile.Production,
            _ => throw new ManifestLoadException(
                "service.profile must be one of 'demo', 'development', or 'production'."),
        };
    }

    private static string ReadDatabaseConnectionString(YamlMappingNode service, string manifestPath)
    {
        var database = RequireMapping(Required(service, "database", "service"), "service.database");
        RequireOnlyKeys(database, "service.database", "kind", "path");
        if (!TryScalar(database, "kind", out var kind) || !StringComparer.Ordinal.Equals(kind, "file"))
        {
            throw new ManifestLoadException("service.database.kind must be 'file'.");
        }

        return ReadSecretFile(
            RequiredScalar(database, "path", "service.database"),
            manifestPath,
            "database connection string");
    }

    private static A2aAuthenticationManifest ReadA2aAuthentication(
        YamlMappingNode service,
        ServiceProfile profile,
        string manifestPath)
    {
        var a2a = RequireMapping(Required(service, "a2a", "service"), "service.a2a");
        RequireOnlyKeys(a2a, "service.a2a", "authentication");
        var authentication = RequireMapping(
            Required(a2a, "authentication", "service.a2a"),
            "service.a2a.authentication");
        var mode = RequiredScalar(authentication, "mode", "service.a2a.authentication");
        return mode switch
        {
            "jwt" => ReadA2aJwt(authentication, manifestPath),
            "anonymous" => ReadA2aAnonymous(authentication, profile),
            _ => throw new ManifestLoadException(
                "service.a2a.authentication.mode must be either 'jwt' or 'anonymous'."),
        };
    }

    private static A2aJwtManifest ReadA2aJwt(YamlMappingNode authentication, string manifestPath)
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
        RequireOnlyKeys(publicKey, "service.a2a.authentication.public_key", "kind", "path");
        if (!TryScalar(publicKey, "kind", out var kind) || !StringComparer.Ordinal.Equals(kind, "file"))
        {
            throw new ManifestLoadException("service.a2a.authentication.public_key.kind must be 'file'.");
        }

        return new A2aJwtManifest(
            RequiredScalar(authentication, "issuer", "service.a2a.authentication"),
            RequiredScalar(authentication, "audience", "service.a2a.authentication"),
            ReadSecretFile(
                RequiredScalar(publicKey, "path", "service.a2a.authentication.public_key"),
                manifestPath,
                "A2A public key"));
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

    private static string ReadSecretFile(string configuredPath, string manifestPath, string description)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var secretPath = Path.GetFullPath(
            configuredPath,
            Path.GetDirectoryName(fullManifestPath)
            ?? throw new ManifestLoadException("The manifest path has no parent directory."));

        EnsurePrivateRegularFile(secretPath, description);
        try
        {
            return TrimOneTrailingNewline(File.ReadAllText(secretPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ManifestLoadException(
                $"The {description} file '{secretPath}' could not be read.",
                exception);
        }
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

    private static string TrimOneTrailingNewline(string value) =>
        value.EndsWith("\r\n", StringComparison.Ordinal)
            ? value[..^2]
            : value.EndsWith('\n')
                ? value[..^1]
                : value;

    private static Dictionary<QueryId, QueryDefinition> ReadQueries(
        YamlMappingNode root,
        IReadOnlyDictionary<string, ExecutableSource> sources,
        IReadOnlyDictionary<ReactionId, ReactionDefinition> reactions)
    {
        var queries = new Dictionary<QueryId, QueryDefinition>();
        foreach (var entry in ReadEntries(root, "queries"))
        {
            var id = new QueryId(entry.Id);
            var description = RequiredScalar(entry.Configuration, "description", $"queries.{entry.Id}");
            var cypher = RequiredScalar(entry.Configuration, "cypher", $"queries.{entry.Id}");
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

            if (!queries.TryAdd(id, new QueryDefinition(description, cypher, sourceIds, allowedReactions)))
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
        IReadOnlySet<ReactionId> AllowedReactions);
    private sealed record ReactionDefinition;
}
