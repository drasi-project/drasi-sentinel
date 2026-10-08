using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public sealed record QueryJoinKeySpec(string Label, string Property);

public sealed record QueryJoinSpec(string Id, IReadOnlyList<QueryJoinKeySpec> Keys);

public sealed record AuthoredQuery(
    QueryId Id,
    string Description,
    string Cypher,
    IReadOnlyList<string> SourceIds,
    IReadOnlyList<QueryJoinSpec> Joins);

public sealed record QueryProbe(bool Ok, IReadOnlyList<JsonNode> Rows, string? Error);

public sealed record QueryAuthoringPrompt(IReadOnlyList<string> SourceIds);

public interface IAuthoredQueryStore
{
    Task<IReadOnlyList<AuthoredQuery>> ListAsync(CancellationToken cancellationToken);
    Task<AuthoredQuery?> FindAsync(QueryId id, CancellationToken cancellationToken);
    Task SaveAsync(AuthoredQuery query, CancellationToken cancellationToken);
    Task ReplaceAsync(AuthoredQuery query, CancellationToken cancellationToken);
    Task DeleteAsync(QueryId id, CancellationToken cancellationToken);
}

public interface INativeQueryRuntime
{
    Task<SourceGraph> DescribeAsync(CancellationToken cancellationToken);

    Task<QueryProbe> ProbeAsync(
        string cypher,
        IReadOnlyList<string> sourceIds,
        IReadOnlyList<QueryJoinSpec> joins,
        TimeSpan readyTimeout,
        CancellationToken cancellationToken);

    Task AddAsync(AuthoredQuery query, TimeSpan? readyTimeout, CancellationToken cancellationToken);
    Task UpdateAsync(AuthoredQuery query, TimeSpan? readyTimeout, CancellationToken cancellationToken);
    Task RemoveAsync(QueryId id, CancellationToken cancellationToken);
    Task<JsonNode?> SourceSchemaAsync(string sourceId, CancellationToken cancellationToken);
    Task<JsonNode> GraphSchemaAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ComponentSnapshot>> ListQueriesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ComponentSnapshot>> ListSourcesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<JsonNode>> ResultsAsync(QueryId id, CancellationToken cancellationToken);
    Task<JsonObject> QueryMetricsAsync(QueryId id, CancellationToken cancellationToken);
    Task<IReadOnlyList<RuntimeLogLine>> TailLogsAsync(string target, string id, TimeSpan window, CancellationToken cancellationToken);
    Task<IReadOnlyList<CapturedDiff>> WatchAsync(QueryId id, TimeSpan window, int max, CancellationToken cancellationToken);
}

public sealed record ComponentSnapshot(string Id, string Status);

public sealed record RuntimeLogLine(DateTimeOffset Timestamp, string Level, string Message);

public sealed record CapturedDiff(string Type, JsonNode? Data, JsonNode? Before, JsonNode? After, ulong Sequence);

public interface IQueryAuthoring
{
    Task<string> DescribeJsonAsync(CancellationToken cancellationToken);
    Task<string> AttemptJsonAsync(IReadOnlyDictionary<string, string?> arguments, CancellationToken cancellationToken);
    Task<string> UpdateJsonAsync(IReadOnlyDictionary<string, string?> arguments, CancellationToken cancellationToken);
}

public sealed class LiveCatalog
{
    private readonly ConcurrentDictionary<QueryId, QueryCatalogItem> _queries = new();

    public LiveCatalog(SafeCatalog seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        foreach (var query in seed.Queries)
            _queries[query.Key] = query.Value;
        Catalog = new SafeCatalog(_queries, seed.Reactions);
    }

    public SafeCatalog Catalog { get; }

    public void Add(QueryCatalogItem query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!_queries.TryAdd(query.Id, query))
            throw new SentinelException("query_exists", $"Query '{query.Id.Value}' already exists.");
    }

    public void Remove(QueryId id) => _queries.TryRemove(id, out _);
}

public sealed class InMemoryAuthoredQueryStore : IAuthoredQueryStore
{
    private readonly ConcurrentDictionary<QueryId, AuthoredQuery> _queries = new();

    public Task<IReadOnlyList<AuthoredQuery>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuthoredQuery>>(_queries.Values.OrderBy(query => query.Id.Value, StringComparer.Ordinal).ToArray());

    public Task<AuthoredQuery?> FindAsync(QueryId id, CancellationToken cancellationToken) =>
        Task.FromResult(_queries.TryGetValue(id, out var query) ? query : null);

    public Task SaveAsync(AuthoredQuery query, CancellationToken cancellationToken)
    {
        if (!_queries.TryAdd(query.Id, query))
            throw new SentinelException("query_exists", $"Query '{query.Id.Value}' already exists.");
        return Task.CompletedTask;
    }

    public Task ReplaceAsync(AuthoredQuery query, CancellationToken cancellationToken)
    {
        if (!_queries.ContainsKey(query.Id))
            throw new SentinelException("query_unknown", $"Query '{query.Id.Value}' does not exist.");
        _queries[query.Id] = query;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(QueryId id, CancellationToken cancellationToken)
    {
        _queries.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}

public sealed class QueryAuthoring(
    INativeQueryRuntime runtime,
    IAuthoredQueryStore store,
    LiveCatalog catalog,
    IReadOnlySet<string> sourceIds,
    ReactionId reaction) : IQueryAuthoring
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex QueryIdPattern = new("^[a-z][a-z0-9-]{1,63}$", RegexOptions.CultureInvariant);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan UnfilteredTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(15);

    public static QueryCatalogItem ToCatalogItem(AuthoredQuery query, ReactionId boundReaction) =>
        new(query.Id, query.Description, new HashSet<ReactionId> { boundReaction }, [query.Description]);

    public async Task<string> DescribeJsonAsync(CancellationToken cancellationToken)
    {
        var graph = await runtime.DescribeAsync(cancellationToken);
        return JsonSerializer.Serialize(new
        {
            Kind = "source_schema",
            SourceIds = sourceIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            Nodes = graph.Nodes.Select(View),
            Relations = graph.Relations.Select(View),
            graph.SourcesWithoutSchema,
        }, Json);
    }

    public async Task<string> AttemptJsonAsync(
        IReadOnlyDictionary<string, string?> arguments,
        CancellationToken cancellationToken)
    {
        var sources = ParseSources(Required(arguments, "sources"));
        var joins = ParseJoins(Optional(arguments, "joins"));
        var requested = Optional(arguments, "query")?.Trim();
        var label = Optional(arguments, "label");
        var commitAs = Optional(arguments, "commit_as")?.Trim();
        var description = Optional(arguments, "description")?.Trim();
        var graph = await runtime.DescribeAsync(cancellationToken);
        var scaffolded = false;
        var query = requested;
        if (string.IsNullOrWhiteSpace(query))
        {
            query = QueryText.Scaffold(graph, label);
            scaffolded = query is not null;
            if (query is null)
            {
                return JsonSerializer.Serialize(new
                {
                    Kind = "query_attempt",
                    Scaffolded = false,
                    Query = (string?)null,
                    Note = graph.Nodes.Count == 0
                        ? "No labels were discovered. Pass a draft query."
                        : $"Pass label or a draft query. Labels: {string.Join(", ", graph.Nodes.Select(node => node.Label))}.",
                    Schema = Summary(graph),
                }, Json);
            }
        }

        var analysis = QueryText.Examine(query, graph, joins.Select(join => join.Id).ToArray());
        QueryProbe? probe = null;
        UnfilteredProbe? unfiltered = null;
        if (analysis.Blocking.Count == 0)
        {
            probe = await runtime.ProbeAsync(query, sources, joins, ProbeTimeout, cancellationToken);
            if (probe.Ok && probe.Rows.Count == 0 && query.Contains("WHERE", StringComparison.OrdinalIgnoreCase))
                unfiltered = await ProbeUnfilteredAsync(query, sources, joins, cancellationToken);
        }

        string? skipped = null;
        string? committed = null;
        if (commitAs is not null)
        {
            skipped = await TryCommitAsync(
                commitAs, description, query, sources, joins, analysis, probe, unfiltered, cancellationToken);
            if (skipped is null)
                committed = commitAs;
        }

        return JsonSerializer.Serialize(new
        {
            Kind = "query_attempt",
            Query = query,
            Scaffolded = scaffolded,
            Schema = Summary(graph),
            Validation = new
            {
                analysis.Blocking,
                analysis.Warnings,
                analysis.UnknownLabels,
                analysis.UnknownProperties,
                analysis.Suggestions,
            },
            Test = probe is null
                ? null
                : new
                {
                    Ran = true,
                    probe.Ok,
                    RowCount = probe.Rows.Count,
                    Rows = probe.Rows.Take(50).ToArray(),
                    probe.Error,
                },
            Unfiltered = unfiltered is null ? null : new { PerLabel = unfiltered.Counts },
            Committed = committed is null ? null : new { Id = committed },
            CommitSkippedReason = skipped,
            Hint = Hint(probe, unfiltered),
        }, Json);
    }

    public async Task<string> UpdateJsonAsync(
        IReadOnlyDictionary<string, string?> arguments,
        CancellationToken cancellationToken)
    {
        var id = new QueryId(Required(arguments, "id").Trim());
        var existing = await store.FindAsync(id, cancellationToken)
            ?? throw new SentinelException("query_unknown", $"Query '{id.Value}' is not an authored query.");
        var sources = ParseSources(Required(arguments, "sources"));
        var joins = ParseJoins(Optional(arguments, "joins"));
        var query = Required(arguments, "query").Trim();
        var description = Optional(arguments, "description")?.Trim() ?? existing.Description;
        var graph = await runtime.DescribeAsync(cancellationToken);
        var analysis = QueryText.Examine(query, graph, joins.Select(join => join.Id).ToArray());
        if (analysis.Blocking.Count > 0 || analysis.UnknownLabels.Count > 0)
        {
            return JsonSerializer.Serialize(new
            {
                Kind = "query_attempt",
                Query = query,
                Validation = new { analysis.Blocking, analysis.Warnings, analysis.UnknownLabels, analysis.Suggestions },
                CommitSkippedReason = analysis.Blocking.Count > 0
                    ? string.Join(" | ", analysis.Blocking)
                    : $"Unknown label: {string.Join(", ", analysis.UnknownLabels)}.",
            }, Json);
        }

        var probe = await runtime.ProbeAsync(query, sources, joins, ProbeTimeout, cancellationToken);
        UnfilteredProbe? unfiltered = null;
        if (probe.Ok && probe.Rows.Count == 0 && query.Contains("WHERE", StringComparison.OrdinalIgnoreCase))
            unfiltered = await ProbeUnfilteredAsync(query, sources, joins, cancellationToken);
        if (!probe.Ok || unfiltered is { HasData: true })
        {
            return JsonSerializer.Serialize(new
            {
                Kind = "query_attempt",
                Query = query,
                Test = new { Ran = true, probe.Ok, RowCount = probe.Rows.Count, Rows = probe.Rows.Take(50).ToArray(), probe.Error },
                CommitSkippedReason = unfiltered is { HasData: true }
                    ? "The filter matched no rows, but the label has data. Fix the literal or join before updating."
                    : probe.Error,
            }, Json);
        }

        var updated = new AuthoredQuery(id, description, query, sources, joins);
        await runtime.UpdateAsync(updated, CommitTimeout, cancellationToken);
        await store.ReplaceAsync(updated, cancellationToken);
        return JsonSerializer.Serialize(new { Kind = "query_updated", Id = id.Value, Query = query }, Json);
    }

    private async Task<string?> TryCommitAsync(
        string commitAs,
        string? description,
        string query,
        IReadOnlyList<string> sources,
        IReadOnlyList<QueryJoinSpec> joins,
        QueryAnalysis analysis,
        QueryProbe? probe,
        UnfilteredProbe? unfiltered,
        CancellationToken cancellationToken)
    {
        if (!QueryIdPattern.IsMatch(commitAs) || commitAs.StartsWith("probe", StringComparison.Ordinal))
            throw new SentinelException("query_rejected", "commit_as must be a lowercase id of letters, digits, and hyphens, and must not start with probe.");
        if (analysis.Blocking.Count > 0)
            return string.Join(" | ", analysis.Blocking);
        if (analysis.UnknownLabels.Count > 0)
            return $"Unknown label: {string.Join(", ", analysis.UnknownLabels)}.";
        if (probe is not { Ok: true })
            return probe?.Error ?? "The query was not tested.";
        if (unfiltered is { HasData: true })
            return "The filter matched no rows, but the label has data. Fix the literal or join before committing.";

        var id = new QueryId(commitAs);
        var authored = new AuthoredQuery(id, description ?? "", query, sources, joins);
        var existing = await store.FindAsync(id, cancellationToken);
        if (existing is not null)
        {
            if (!Same(existing, authored))
                throw new SentinelException("query_exists", $"Query '{commitAs}' already exists.");
            await runtime.AddAsync(existing, CommitTimeout, cancellationToken);
            EnsureCatalog(existing);
            return null;
        }

        if (catalog.Catalog.Queries.ContainsKey(id))
            throw new SentinelException("query_exists", $"Query '{commitAs}' already exists.");
        if (string.IsNullOrWhiteSpace(description) || description.Length > 200 || description.Contains('\n') || description.Contains('\r'))
            throw new SentinelException("query_rejected", "Pass a one-line description of at most 200 characters when committing.");

        authored = authored with { Description = description };
        await store.SaveAsync(authored, cancellationToken);
        try
        {
            await runtime.AddAsync(authored, CommitTimeout, cancellationToken);
            catalog.Add(ToCatalogItem(authored, reaction));
        }
        catch
        {
            await store.DeleteAsync(id, CancellationToken.None);
            throw;
        }

        return null;
    }

    private void EnsureCatalog(AuthoredQuery query)
    {
        if (!catalog.Catalog.Queries.ContainsKey(query.Id))
            catalog.Add(ToCatalogItem(query, reaction));
    }

    private async Task<UnfilteredProbe?> ProbeUnfilteredAsync(
        string query,
        IReadOnlyList<string> sources,
        IReadOnlyList<QueryJoinSpec> joins,
        CancellationToken cancellationToken)
    {
        var counts = new List<LabelCount>();
        foreach (var label in QueryText.NodeLabels(query).Take(4))
        {
            if (!Identifier.IsMatch(label))
                continue;
            var probe = await runtime.ProbeAsync($"MATCH (n:{label}) RETURN n", sources, joins, UnfilteredTimeout, cancellationToken);
            if (probe.Ok)
                counts.Add(new LabelCount(label, probe.Rows.Count));
        }

        return counts.Count == 0 ? null : new UnfilteredProbe(counts);
    }

    private IReadOnlyList<string> ParseSources(string text)
    {
        var sources = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (sources.Length == 0)
            throw new SentinelException("invalid_tool_arguments", "Pass at least one source id.");
        var unknown = sources.FirstOrDefault(id => !sourceIds.Contains(id));
        if (unknown is not null)
            throw new SentinelException("source_unknown", $"Source '{unknown}' is not configured.");
        return sources;
    }

    private static IReadOnlyList<QueryJoinSpec> ParseJoins(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        JsonArray array;
        try
        {
            array = JsonNode.Parse(text) as JsonArray
                ?? throw new SentinelException("invalid_tool_arguments", "joins must be a JSON array.");
        }
        catch (JsonException exception)
        {
            throw new SentinelException("invalid_tool_arguments", "joins must be a JSON array.", exception);
        }

        var joins = new List<QueryJoinSpec>();
        foreach (var item in array)
        {
            if (item is not JsonObject join)
                throw new SentinelException("invalid_tool_arguments", "Each join needs an id and keys.");
            var id = join["id"]?.GetValue<string>();
            if (id is null || !Identifier.IsMatch(id) || join["keys"] is not JsonArray keys || keys.Count < 2)
                throw new SentinelException("invalid_tool_arguments", "Each join needs an id and at least two keys.");
            var parsedKeys = new List<QueryJoinKeySpec>();
            foreach (var key in keys)
            {
                if (key is not JsonObject keyObject)
                    throw new SentinelException("invalid_tool_arguments", "Each join key needs a label and property.");
                var keyLabel = keyObject["label"]?.GetValue<string>();
                var property = keyObject["property"]?.GetValue<string>();
                if (keyLabel is null || property is null || !Identifier.IsMatch(keyLabel) || !Identifier.IsMatch(property))
                    throw new SentinelException("invalid_tool_arguments", "Each join key needs a label and property.");
                parsedKeys.Add(new QueryJoinKeySpec(keyLabel, property));
            }

            joins.Add(new QueryJoinSpec(id, parsedKeys));
        }

        return joins;
    }

    private static bool Same(AuthoredQuery existing, AuthoredQuery candidate) =>
        existing.Cypher == candidate.Cypher
        && existing.SourceIds.SequenceEqual(candidate.SourceIds, StringComparer.Ordinal)
        && JsonSerializer.Serialize(existing.Joins) == JsonSerializer.Serialize(candidate.Joins);

    private static string? Hint(QueryProbe? probe, UnfilteredProbe? unfiltered)
    {
        if (probe is not { Ok: true, Rows.Count: 0 })
            return null;
        return unfiltered is { HasData: true }
            ? "The filter matched no rows, but the label has data. Comparison is case-sensitive and exact. Check the literal or a missing join before reporting that nothing was found."
            : "The test returned no rows. A commit can still watch for later rows when the unfiltered query is also empty.";
    }

    private sealed record LabelCount(string Label, int Count);

    private sealed record UnfilteredProbe(IReadOnlyList<LabelCount> Counts)
    {
        public bool HasData => Counts.Any(count => count.Count > 0);
    }

    private static object Summary(SourceGraph graph) => new
    {
        Nodes = graph.Nodes.Select(View),
        Relations = graph.Relations.Select(View),
        graph.SourcesWithoutSchema,
    };

    private static object View(GraphNode node) => new
    {
        node.Label,
        Properties = node.Properties.Select(property => new { property.Name, property.Type }),
        node.Sources,
    };

    private static object View(GraphRelation relation) => new
    {
        relation.Label,
        relation.To,
        Properties = relation.Properties.Select(property => new { property.Name, property.Type }),
        relation.Sources,
    };

    private static string Required(IReadOnlyDictionary<string, string?>? arguments, string name) =>
        Optional(arguments, name) ?? throw new SentinelException("invalid_tool_arguments", $"Pass {name}.");

    private static string? Optional(IReadOnlyDictionary<string, string?>? arguments, string name) =>
        arguments is not null && arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}
