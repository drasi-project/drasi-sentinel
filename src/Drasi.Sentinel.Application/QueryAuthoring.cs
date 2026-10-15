using System.Collections.Concurrent;
using System.Globalization;
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
    IReadOnlyList<QueryJoinSpec> Joins,
    Lifetime Expires);

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
    Task<string> AttemptJsonAsync(
        IReadOnlyDictionary<string, string?> arguments,
        CancellationToken cancellationToken,
        DateTimeOffset? grantedAt = null);
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
    ReactionId reaction,
    Func<QueryId, CancellationToken, Task<bool>>? queryIsWatched = null,
    TtlPolicy? ttlPolicy = null,
    TimeProvider? time = null) : IQueryAuthoring
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex VariableLabel = new(
        @"\(\s*(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?<label>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.CultureInvariant);
    private static readonly Regex CrossEquality = new(
        @"\b(?<leftVar>[A-Za-z_][A-Za-z0-9_]*)\.(?<leftProp>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<rightVar>[A-Za-z_][A-Za-z0-9_]*)\.(?<rightProp>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.CultureInvariant);
    private static readonly Regex RelationshipType = new(
        @"\[:\s*(?<id>[A-Za-z_][A-Za-z0-9_]*)\s*\]",
        RegexOptions.CultureInvariant);
    private static readonly Regex RoundCue = new(
        @"\b(round|rounded|rounding|whole number)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BranchCue = new(
        @"\b(otherwise|else)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LiteralEquality = new(
        @"\b(?<variable>[A-Za-z_][A-Za-z0-9_]*)\.(?<property>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*'(?<literal>[^']*)'",
        RegexOptions.CultureInvariant);
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
        CancellationToken cancellationToken,
        DateTimeOffset? grantedAt = null)
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

        var joinSkip = JoinSkipReason(query, joins, analysis);
        var callerText = Optional(arguments, "caller_text");
        var durationSkip = DurationSkip(query, callerText);
        var literalSkip = analysis.Blocking.Count == 0
            ? await SampledLiteralSkipAsync(query, sources, callerText, cancellationToken)
            : null;
        var projectionSkip = ProjectionSkip(query, graph);
        string? skipped = null;
        string? committed = null;
        if (commitAs is not null)
        {
            var commit = await TryCommitAsync(
                commitAs, description, query, sources, joins, analysis, probe, callerText, literalSkip, projectionSkip, Optional(arguments, "ttl"), grantedAt, cancellationToken);
            skipped = commit.Skipped;
            committed = commit.Committed;
        }
        else if (joinSkip is not null)
        {
            skipped = joinSkip;
        }
        else if (literalSkip is not null)
        {
            skipped = literalSkip;
        }
        else if (durationSkip is not null)
        {
            skipped = durationSkip;
        }
        else if (projectionSkip is not null)
        {
            skipped = projectionSkip;
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
            Hint = joinSkip ?? literalSkip ?? durationSkip ?? projectionSkip ?? Hint(probe, unfiltered),
        }, Json);
    }

    public async Task<string> UpdateJsonAsync(
        IReadOnlyDictionary<string, string?> arguments,
        CancellationToken cancellationToken)
    {
        var id = new QueryId(Required(arguments, "id").Trim());
        var existing = await store.FindAsync(id, cancellationToken)
            ?? throw new SentinelException("query_unknown", $"Query '{id.Value}' is not an authored query.");
        if (queryIsWatched is not null && await queryIsWatched(id, cancellationToken))
        {
            throw new SentinelException(
                "query_rejected",
                $"Query '{id.Value}' is already watched. Do not replace it. Call add_query and subscribe only to the id in Committed.");
        }
        var sources = ParseSources(Required(arguments, "sources"));
        var joins = ParseJoins(Optional(arguments, "joins"));
        var query = Required(arguments, "query").Trim();
        var description = Optional(arguments, "description")?.Trim() ?? existing.Description;
        var graph = await runtime.DescribeAsync(cancellationToken);
        var analysis = QueryText.Examine(query, graph, joins.Select(join => join.Id).ToArray());
        if (UnrequestedFunction(query, Optional(arguments, "caller_text")) is { } functionSkip)
        {
            return JsonSerializer.Serialize(new
            {
                Kind = "query_attempt",
                Query = query,
                CommitSkippedReason = functionSkip,
                Hint = functionSkip,
            }, Json);
        }

        if (DurationSkip(query, Optional(arguments, "caller_text")) is { } durationSkip)
        {
            return JsonSerializer.Serialize(new
            {
                Kind = "query_attempt",
                Query = query,
                CommitSkippedReason = durationSkip,
                Hint = durationSkip,
            }, Json);
        }

        if (await OpenPropertyChoiceAsync(Optional(arguments, "caller_text"), cancellationToken) is { } propertyChoice)
        {
            return JsonSerializer.Serialize(new
            {
                Kind = "query_attempt",
                Query = query,
                CommitSkippedReason = propertyChoice,
                Hint = propertyChoice,
            }, Json);
        }

        if (JoinSkipReason(query, joins, analysis) is { } joinSkip)
        {
            return JsonSerializer.Serialize(new
            {
                Kind = "query_attempt",
                Query = query,
                CommitSkippedReason = joinSkip,
                Hint = joinSkip,
            }, Json);
        }

        if (await SampledLiteralSkipAsync(query, sources, Optional(arguments, "caller_text"), cancellationToken) is { } literalSkip)
        {
            return JsonSerializer.Serialize(new
            {
                Kind = "query_attempt",
                Query = query,
                CommitSkippedReason = literalSkip,
                Hint = literalSkip,
            }, Json);
        }

        if (ProjectionSkip(query, graph) is { } projectionSkip)
        {
            return JsonSerializer.Serialize(new
            {
                Kind = "query_attempt",
                Query = query,
                CommitSkippedReason = projectionSkip,
                Hint = projectionSkip,
            }, Json);
        }

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
        if (!probe.Ok)
        {
            return JsonSerializer.Serialize(new
            {
                Kind = "query_attempt",
                Query = query,
                Test = new { Ran = true, probe.Ok, RowCount = probe.Rows.Count, Rows = probe.Rows.Take(50).ToArray(), probe.Error },
                CommitSkippedReason = probe.Error,
            }, Json);
        }

        var updated = new AuthoredQuery(id, description, query, sources, joins, existing.Expires);
        await runtime.UpdateAsync(updated, CommitTimeout, cancellationToken);
        await store.ReplaceAsync(updated, cancellationToken);
        return JsonSerializer.Serialize(new { Kind = "query_updated", Id = id.Value, Query = query }, Json);
    }

    private async Task<(string? Skipped, string? Committed)> TryCommitAsync(
        string commitAs,
        string? description,
        string query,
        IReadOnlyList<string> sources,
        IReadOnlyList<QueryJoinSpec> joins,
        QueryAnalysis analysis,
        QueryProbe? probe,
        string? callerText,
        string? literalSkip,
        string? projectionSkip,
        string? ttl,
        DateTimeOffset? grantedAt,
        CancellationToken cancellationToken)
    {
        if (!QueryIdPattern.IsMatch(commitAs) || commitAs.StartsWith("probe", StringComparison.Ordinal))
            throw new SentinelException("query_rejected", "commit_as must be a lowercase id of letters, digits, and hyphens, and must not start with probe.");
        if (analysis.Blocking.Count > 0)
            return (string.Join(" | ", analysis.Blocking), null);
        if (await OpenPropertyChoiceAsync(callerText, cancellationToken) is { } propertyChoice)
            return (propertyChoice, null);
        if (JoinSkipReason(query, joins, analysis) is { } joinSkip)
            return (joinSkip, null);
        if (analysis.UnknownLabels.Count > 0)
            return ($"Unknown label: {string.Join(", ", analysis.UnknownLabels)}.", null);
        if (analysis.UnknownProperties.Count > 0)
            return ($"Unknown property: {string.Join(", ", analysis.UnknownProperties)}. Use a property name from the schema.", null);
        if (UnrequestedFunction(query, callerText) is { } functionSkip)
            return (functionSkip, null);
        if (DurationSkip(query, callerText) is { } durationSkip)
            return (durationSkip, null);
        if (literalSkip is not null)
            return (literalSkip, null);
        if (AggregationOnly(query, callerText) is { } aggregation)
            return (aggregation, null);
        if (MappingSkip(query, description, callerText) is { } mappingSkip)
            return (mappingSkip, null);
        if (projectionSkip is not null)
            return (projectionSkip, null);
        if (probe is not { Ok: true })
            return (probe?.Error ?? "The query was not tested.", null);

        var admitted = ttlPolicy is null
            ? new Unbounded()
            : TtlToken.Admit(ttl, ttlPolicy, grantedAt ?? (time ?? TimeProvider.System).GetUtcNow());
        var id = new QueryId(commitAs);
        var authored = new AuthoredQuery(id, description ?? "", query, sources, joins, admitted);
        var existing = await store.FindAsync(id, cancellationToken);
        if (existing is not null && Same(existing, authored))
        {
            if (await DeployAsync(existing, cancellationToken) is { } existingFailure)
                return (existingFailure, null);
            EnsureCatalog(existing);
            return (null, existing.Id.Value);
        }

        if (string.IsNullOrWhiteSpace(description) || description.Length > 200 || description.Contains('\n') || description.Contains('\r'))
            throw new SentinelException("query_rejected", "Pass a one-line description of at most 200 characters when committing.");
        if (existing is not null || catalog.Catalog.Queries.ContainsKey(id))
            id = new QueryId(await AllocateIdAsync(commitAs, cancellationToken));

        authored = new AuthoredQuery(id, description, query, sources, joins, admitted);
        await store.SaveAsync(authored, cancellationToken);
        if (await DeployAsync(authored, cancellationToken) is { } failure)
        {
            await store.DeleteAsync(id, CancellationToken.None);
            return (failure, null);
        }

        catalog.Add(ToCatalogItem(authored, reaction));
        return (null, id.Value);
    }

    private async Task<string?> DeployAsync(AuthoredQuery query, CancellationToken cancellationToken)
    {
        try
        {
            await runtime.AddAsync(query, CommitTimeout, cancellationToken);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var detail = string.IsNullOrWhiteSpace(exception.Message)
                ? "The query could not be deployed."
                : exception.Message;
            return $"The query was not deployed: {detail}. Tell the caller this error. Do not say the watch is active and do not subscribe.";
        }
    }

    private async Task<string> AllocateIdAsync(string requested, CancellationToken cancellationToken)
    {
        var stem = requested.Length > 60 ? requested[..60].TrimEnd('-') : requested;
        for (var suffix = 2; suffix < 100; suffix++)
        {
            var tail = "-" + suffix.ToString(CultureInfo.InvariantCulture);
            var candidate = stem.Length + tail.Length <= 64
                ? stem + tail
                : stem[..(64 - tail.Length)].TrimEnd('-') + tail;
            if (!QueryIdPattern.IsMatch(candidate))
                continue;
            var id = new QueryId(candidate);
            if (await store.FindAsync(id, cancellationToken) is null && !catalog.Catalog.Queries.ContainsKey(id))
                return candidate;
        }

        throw new SentinelException(
            "query_exists",
            $"Query '{requested}' already exists. Do not replace it, and do not subscribe to it.");
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
        var tokens = SourceTokens(text);
        if (tokens.Count == 0)
            throw new SentinelException("invalid_tool_arguments", "Pass at least one configured source id, comma-separated or as a JSON array.");
        var unknown = tokens.FirstOrDefault(id => !sourceIds.Contains(id));
        if (unknown is not null)
            throw new SentinelException("source_unknown", $"Source '{unknown}' is not configured. Configured ids: {string.Join(", ", sourceIds.OrderBy(id => id, StringComparer.Ordinal))}.");
        return tokens;
    }

    private static IReadOnlyList<string> SourceTokens(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
        {
            try
            {
                var node = Unwrap(JsonNode.Parse(trimmed));
                if (node is JsonArray array)
                {
                    return array.Select(SourceToken).Where(id => id is not null).Cast<string>()
                        .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                }

                if (SourceToken(node) is { } single)
                    return [single];
            }
            catch (JsonException)
            {
            }
        }

        return trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? SourceToken(JsonNode? node) => node switch
    {
        JsonValue => node.GetValue<string>(),
        JsonObject obj => Text(obj, "sourceId", "id"),
        _ => null,
    };

    private static IReadOnlyList<QueryJoinSpec> ParseJoins(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        JsonNode? node;
        try
        {
            node = Unwrap(JsonNode.Parse(text));
        }
        catch (JsonException exception)
        {
            throw new SentinelException("invalid_tool_arguments", JoinShape, exception);
        }

        var array = node as JsonArray ?? (node is JsonObject ? new JsonArray(node.DeepClone()) : null);
        if (array is null || array.Count == 0)
            throw new SentinelException("invalid_tool_arguments", JoinShape);
        var joins = new List<QueryJoinSpec>();
        foreach (var item in array)
        {
            if (item is not JsonObject join)
                throw new SentinelException("invalid_tool_arguments", JoinShape);
            var id = Text(join, "id");
            var keys = Field(join, "keys") as JsonArray;
            if (id is null || !Identifier.IsMatch(id) || keys is null || keys.Count < 2)
                throw new SentinelException("invalid_tool_arguments", JoinShape);
            var parsedKeys = new List<QueryJoinKeySpec>();
            foreach (var key in keys)
            {
                var parsed = ParseJoinKey(key);
                if (parsed is null)
                    throw new SentinelException("invalid_tool_arguments", JoinShape);
                parsedKeys.Add(parsed);
            }

            joins.Add(new QueryJoinSpec(id, parsedKeys));
        }

        return joins;
    }

    private static QueryJoinKeySpec? ParseJoinKey(JsonNode? key)
    {
        if (key is JsonValue && key.GetValue<string>() is { } dotted && dotted.Split('.', 2) is [var label, var property]
            && Identifier.IsMatch(label) && Identifier.IsMatch(property))
            return new QueryJoinKeySpec(label, property);
        if (key is not JsonObject keyObject)
            return null;
        var keyLabel = Text(keyObject, "label");
        var keyProperty = Text(keyObject, "property");
        return keyLabel is not null && keyProperty is not null && Identifier.IsMatch(keyLabel) && Identifier.IsMatch(keyProperty)
            ? new QueryJoinKeySpec(keyLabel, keyProperty)
            : null;
    }

    private static JsonNode? Unwrap(JsonNode? node) =>
        node is JsonValue && node.GetValue<string>() is { } inner && inner.TrimStart().StartsWith('[')
            ? JsonNode.Parse(inner)
            : node;

    private static string? Text(JsonObject obj, params string[] names) =>
        Field(obj, names) is JsonValue value ? value.GetValue<string>() : null;

    private static JsonNode? Field(JsonObject obj, params string[] names)
    {
        foreach (var pair in obj)
        {
            if (names.Any(name => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return pair.Value;
        }

        return null;
    }

    private const string JoinShape =
        "joins must be a JSON array like [{\"id\":\"LINK\",\"keys\":[{\"label\":\"LabelA\",\"property\":\"key_a\"},{\"label\":\"LabelB\",\"property\":\"key_b\"}]}]. The id is the relationship type in MATCH (a)-[:LINK]->(b).";

    private static bool Same(AuthoredQuery existing, AuthoredQuery candidate) =>
        existing.Cypher == candidate.Cypher
        && existing.SourceIds.SequenceEqual(candidate.SourceIds, StringComparer.Ordinal)
        && JsonSerializer.Serialize(existing.Joins) == JsonSerializer.Serialize(candidate.Joins);

    private static string? Hint(QueryProbe? probe, UnfilteredProbe? unfiltered)
    {
        if (probe is not { Ok: true, Rows.Count: 0 })
            return null;
        return unfiltered is { HasData: true }
            ? "The filter matched no current rows, but the label has data. Comparison is case-sensitive. If the literal is a case or spelling mismatch of a profiled example, fix it. If it is the future value the caller asked to watch, commit and subscribe. Do not report that nothing was found."
            : "The test returned no rows. A commit can still watch for later rows when the unfiltered query is also empty.";
    }

    private async Task<string?> SampledLiteralSkipAsync(
        string query,
        IReadOnlyList<string> sources,
        string? callerText,
        CancellationToken cancellationToken)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in VariableLabel.Matches(query))
            variables[match.Groups["variable"].Value] = match.Groups["label"].Value;
        var samples = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
        var problems = new List<string>();
        var named = new List<string>();
        foreach (Match match in LiteralEquality.Matches(query))
        {
            var variable = match.Groups["variable"].Value;
            var property = match.Groups["property"].Value;
            var literal = match.Groups["literal"].Value;
            if (!variables.TryGetValue(variable, out var label) || !Identifier.IsMatch(label))
                continue;
            if (!samples.TryGetValue(label, out var properties))
            {
                properties = await SampleLabelAsync(label, sources, cancellationToken);
                samples[label] = properties;
            }

            if (!properties.TryGetValue(property, out var examples) || examples.Count == 0)
                continue;
            if (examples.Contains(literal, StringComparer.Ordinal))
                continue;
            var exactCase = examples.FirstOrDefault(example => example.Equals(literal, StringComparison.OrdinalIgnoreCase));
            if (exactCase is not null)
            {
                problems.Add($"'{literal}' is not the stored spelling of {label}.{property}. Use '{exactCase}'.");
                continue;
            }

            if (IsNearMiss(literal, examples))
            {
                problems.Add(
                    $"'{literal}' is not a stored {label}.{property} value. Stored values: {string.Join(", ", examples)}. Use one of those exact strings.");
                continue;
            }

            if (await GuessedComplementAsync(callerText, label, property, literal, cancellationToken))
                problems.Add(
                    $"'{literal}' is not a stored {label}.{property} value. These are uncovered words. Do not guess that mapping. Stored values: {string.Join(", ", examples)}.");
        }

        foreach (var label in variables.Values.Distinct(StringComparer.Ordinal))
        {
            if (!Identifier.IsMatch(label) || samples.ContainsKey(label))
                continue;
            samples[label] = await SampleLabelAsync(label, sources, cancellationToken);
        }

        var labels = samples.Keys.ToArray();
        foreach (var example in samples.Values.SelectMany(properties => properties.Values).SelectMany(values => values).Distinct(StringComparer.Ordinal))
        {
            if (NamesStoredValue(callerText, example, labels) && !query.Contains($"'{example}'", StringComparison.Ordinal))
                named.Add(example);
        }

        if (named.Count > 0)
            problems.Add($"The caller named stored values missing from the query: {string.Join(", ", named.Select(value => "'" + value + "'"))}. Filter with those exact strings.");
        return problems.Count == 0
            ? null
            : string.Join(" ", problems) + " Commit again with a stored value.";
    }

    private static string? ProjectionSkip(string query, SourceGraph graph)
    {
        var returns = Regex.Match(query, @"\bRETURN\b(?<projection>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!returns.Success)
            return null;
        var projection = returns.Groups["projection"].Value;
        var aggregatedOnly = Regex.IsMatch(projection, @"\b(count|sum|avg|min|max|collect)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && !Regex.IsMatch(projection, @"\b[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*\b");
        if (aggregatedOnly)
            return null;
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in VariableLabel.Matches(query))
            variables[match.Groups["variable"].Value] = match.Groups["label"].Value;
        var returned = variables
            .Where(pair => Regex.IsMatch(projection, $@"\b{Regex.Escape(pair.Key)}\s*\.", RegexOptions.CultureInvariant))
            .Select(pair => pair.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var labels = returned.Length == 0 ? QueryText.NodeLabels(query) : returned;
        var missing = new List<string>();
        foreach (var label in labels)
        {
            var node = graph.Nodes.FirstOrDefault(item => item.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
            if (node is null)
                continue;
            var identity = IdentityName(node);
            if (identity is not null)
            {
                if (!Regex.IsMatch(projection, $@"\b{Regex.Escape(identity)}\b", RegexOptions.CultureInvariant))
                    missing.Add($"{label}.{identity}");
                continue;
            }

            var projected = node.Properties.Any(property =>
                Regex.IsMatch(projection, $@"\b{Regex.Escape(property.Name)}\b", RegexOptions.CultureInvariant)
                && !Regex.IsMatch(query, $@"\b{Regex.Escape(property.Name)}\s*=\s*'", RegexOptions.CultureInvariant));
            if (!projected && node.Properties.Count > 0)
                missing.Add($"{label}.{node.Properties[0].Name}");
        }

        return missing.Count == 0
            ? null
            : $"RETURN is missing the identity property {string.Join(", ", missing)}. Project that property. Do not project the other schema properties. Commit again in this turn.";
    }

    private static string? IdentityName(GraphNode node)
    {
        var id = node.Properties.FirstOrDefault(property => property.Name.Equals("id", StringComparison.OrdinalIgnoreCase));
        if (id is not null)
            return id.Name;
        var suffixed = node.Properties.Where(property =>
            property.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase)
            || property.Name.EndsWith("Id", StringComparison.Ordinal)).ToArray();
        return suffixed.Length == 1 ? suffixed[0].Name : null;
    }

    private static string? UnrequestedFunction(string query, string? callerText)
    {
        if (string.IsNullOrWhiteSpace(callerText))
            return null;
        if (Regex.IsMatch(query, @"\bfloor\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && !RoundCue.IsMatch(callerText))
            return "The caller did not ask to round a number down. Remove floor(). Commit again in this turn.";
        if (Regex.IsMatch(query, @"\bCASE\b", RegexOptions.CultureInvariant) && !BranchCue.IsMatch(callerText))
            return "The caller did not ask for a conditional value. Remove CASE. Commit again in this turn.";
        return null;
    }

    private static string? AggregationOnly(string query, string? callerText)
    {
        if (string.IsNullOrWhiteSpace(callerText) ||
            Regex.IsMatch(callerText, @"\b(count|how many|number of|total|average|avg|mean|minimum|maximum)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return null;
        var returns = Regex.Match(query, @"\bRETURN\b(?<projection>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!returns.Success)
            return null;
        var projection = returns.Groups["projection"].Value;
        var aggregated = Regex.IsMatch(projection, @"\b(count|sum|avg|min|max|collect)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var projectedProperty = Regex.IsMatch(projection, @"\b[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*\b");
        return aggregated && !projectedProperty
            ? "The caller asked to be told about a row, not a count. RETURN the identifying properties. Do not return only an aggregation. Commit again in this turn."
            : null;
    }

    private static bool NamesStoredValue(string? text, string example, IReadOnlyCollection<string> labels)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (Regex.IsMatch(text, $@"\b{Regex.Escape(example)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        foreach (Match word in Regex.Matches(text, @"\b[A-Za-z][A-Za-z0-9]{3,}\b"))
        {
            if (IsLabelWord(word.Value, labels))
                continue;
            if (example.StartsWith(word.Value, StringComparison.OrdinalIgnoreCase) && example.Length > word.Value.Length)
                return true;
        }

        return false;
    }

    private static bool IsLabelWord(string word, IReadOnlyCollection<string> labels)
    {
        foreach (var label in labels)
        {
            if (label.Equals(word, StringComparison.OrdinalIgnoreCase))
                return true;
            if (word.EndsWith('s') && label.Equals(word[..^1], StringComparison.OrdinalIgnoreCase))
                return true;
            if (label.Equals(word + "s", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> SampleLabelAsync(
        string label,
        IReadOnlyList<string> sources,
        CancellationToken cancellationToken)
    {
        var merged = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var probe = await runtime.ProbeAsync($"MATCH (n:{label}) RETURN n", [source], [], UnfilteredTimeout, cancellationToken);
            if (!probe.Ok)
                continue;
            foreach (var pair in QuerySamples.Examples(probe.Rows))
            {
                if (!merged.TryGetValue(pair.Key, out var values))
                    merged[pair.Key] = values = [];
                foreach (var value in pair.Value.Select(StringValue).Where(value => value is not null).Cast<string>())
                {
                    if (!values.Contains(value, StringComparer.Ordinal) && values.Count < 8)
                        values.Add(value);
                }
            }
        }

        return merged.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.Ordinal);
    }

    private static string? StringValue(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private async Task<bool> GuessedComplementAsync(
        string? callerText,
        string label,
        string property,
        string literal,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callerText))
            return false;
        var stored = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var graph = await runtime.DescribeAsync(cancellationToken);
        foreach (var node in graph.Nodes)
        {
            if (!Identifier.IsMatch(node.Label))
                continue;
            foreach (var pair in await SampleLabelAsync(node.Label, sourceIds.ToArray(), cancellationToken))
            {
                if (!stored.TryGetValue(pair.Key, out var existing))
                    stored[pair.Key] = pair.Value;
                else
                    stored[pair.Key] = existing.Concat(pair.Value).Distinct(StringComparer.Ordinal).ToArray();
            }
        }

        return QueryWorkbench.GuessedComplementLiteral(
            callerText,
            $"MATCH (n:{label}) WHERE n.{property} = '{literal}' RETURN n",
            stored);
    }

    private static bool IsNearMiss(string literal, IReadOnlyList<string> examples)
    {
        var tokens = Regex.Split(literal, "[^A-Za-z0-9]+")
            .Where(token => token.Length > 0)
            .ToArray();
        foreach (var example in examples)
        {
            if (tokens.Any(token => token.Equals(example, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (literal.Length >= 4 &&
                example.StartsWith(literal, StringComparison.OrdinalIgnoreCase) &&
                example.Length > literal.Length)
                return true;
        }

        return false;
    }

    private static string? MappingSkip(string query, string? description, string? callerText)
    {
        if (string.IsNullOrWhiteSpace(callerText))
            return null;
        var uncovered = QueryWorkbench.UncoveredConditions(callerText, query);
        if (uncovered.Length == 0)
            return null;
        return $"The query does not cover {string.Join(", ", uncovered)}. These are uncovered words. Do not commit a guessed mapping. Ask which available labels, properties, or stored values correspond to them. Include every configured label, including labels the caller did not name.";
    }

    private static string? DurationSkip(string query, string? callerText)
    {
        if (CallerDuration.HasOnlyLifetime(callerText) && query.Contains("trueFor", StringComparison.OrdinalIgnoreCase))
            return "The duration after keep the watch is ttl, not trueFor. Remove trueFor and pass that ttl. Commit again in this turn.";
        if (CallerDuration.Condition(callerText) is not { } condition)
            return null;
        if (!query.Contains("trueFor", StringComparison.OrdinalIgnoreCase))
            return $"The caller asked for a condition that must hold for {condition.Amount} {condition.Unit}. Use drasi.trueFor(<that condition>, duration({{ {condition.Unit}: {condition.Amount} }})). A WHERE comparison does not wait. Commit again in this turn.";
        return Regex.IsMatch(
            query,
            $@"duration\s*\(\s*\{{\s*{condition.Unit}\s*:\s*{condition.Amount}\s*\}}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? null
            : $"drasi.trueFor must use duration({{ {condition.Unit}: {condition.Amount} }}), the duration the caller asked for. Commit again in this turn.";
    }

    private async Task<string?> OpenPropertyChoiceAsync(string? callerText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callerText))
            return null;
        var lines = callerText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || lines[^1].Length > 40)
            return null;
        var latest = lines[^1];
        var stored = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var graph = await runtime.DescribeAsync(cancellationToken);
        foreach (var node in graph.Nodes)
        {
            if (!Identifier.IsMatch(node.Label))
                continue;
            foreach (var pair in await SampleLabelAsync(node.Label, sourceIds.ToArray(), cancellationToken))
            {
                if (!stored.TryGetValue(pair.Key, out var list))
                    stored[pair.Key] = list = [];
                foreach (var value in pair.Value)
                {
                    if (!list.Contains(value, StringComparer.Ordinal))
                        list.Add(value);
                }
            }
        }

        var open = stored
            .Where(pair => pair.Value.Count > 1 &&
                Regex.IsMatch(latest, $@"\b{Regex.Escape(pair.Key)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                !pair.Value.Any(value => Regex.IsMatch(latest, $@"\b{Regex.Escape(value)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            .Select(pair => pair.Key + ": " + string.Join(", ", pair.Value))
            .ToArray();
        var values = stored.Values.SelectMany(item => item).ToArray();
        var unmapped = QueryWorkbench.UncoveredConditions(string.Join('\n', lines[..^1]), "")
            .Where(word => !values.Any(value => value.Equals(word, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return open.Length == 0 || unmapped.Length == 0
            ? null
            : $"The caller named {string.Join("; ", open)} and has not chosen one stored value. These are uncovered words. Ask which stored value. Do not commit.";
    }

    private static string? JoinSkipReason(string query, IReadOnlyList<QueryJoinSpec> joins, QueryAnalysis analysis)
    {
        if (joins.Count == 0 &&
            analysis.Warnings.FirstOrDefault(warning => warning.Contains("declares no joins", StringComparison.Ordinal)) is { } missingJoin)
            return missingJoin + " " + JoinRepair(query);
        if (joins.Count == 0 &&
            analysis.Warnings.FirstOrDefault(warning => warning.Contains("reported relationship", StringComparison.Ordinal)) is { } reported)
            return reported;
        if (joins.Count == 0 &&
            analysis.UnknownLabels.FirstOrDefault(label => RelationshipIds(query).Contains(label, StringComparer.Ordinal)) is { } relationship)
            return $"'{relationship}' is a join id, not a node label. " + JoinRepair(query);
        var unused = joins.FirstOrDefault(join => !query.Contains(":" + join.Id, StringComparison.Ordinal));
        return unused is null
            ? null
            : $"joins id {unused.Id} must appear in MATCH as -[:{unused.Id}]->. A comma or second MATCH does not use the join. Rewrite MATCH and commit again in this turn.";
    }

    private static string JoinRepair(string query)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in VariableLabel.Matches(query))
            variables[match.Groups["variable"].Value] = match.Groups["label"].Value;
        var id = RelationshipIds(query).FirstOrDefault() ?? "LINK";
        foreach (Match match in CrossEquality.Matches(query))
        {
            var leftVar = match.Groups["leftVar"].Value;
            var rightVar = match.Groups["rightVar"].Value;
            if (leftVar.Equals(rightVar, StringComparison.Ordinal))
                continue;
            if (!variables.TryGetValue(leftVar, out var leftLabel) || !variables.TryGetValue(rightVar, out var rightLabel))
                continue;
            if (leftLabel.Equals(rightLabel, StringComparison.OrdinalIgnoreCase))
                continue;
            var leftProp = match.Groups["leftProp"].Value;
            var rightProp = match.Groups["rightProp"].Value;
            return $"Pass joins as [{{\"id\":\"{id}\",\"keys\":[{{\"label\":\"{leftLabel}\",\"property\":\"{leftProp}\"}},{{\"label\":\"{rightLabel}\",\"property\":\"{rightProp}\"}}]}}] and rewrite MATCH to ({leftVar}:{leftLabel})-[:{id}]->({rightVar}:{rightLabel}). Do not use a comma or a second MATCH. Commit again in this turn.";
        }

        var labels = QueryText.NodeLabels(query);
        return labels.Count >= 2
            ? $"Pass joins for the shared property between {labels[0]} and {labels[1]} and MATCH (a:{labels[0]})-[:{id}]->(b:{labels[1]}). A comma or second MATCH does not join. Commit again in this turn."
            : "Declare joins and MATCH the synthetic relationship. A comma or second MATCH does not join. Commit again in this turn.";
    }

    private static IReadOnlyList<string> RelationshipIds(string query)
    {
        var ids = new List<string>();
        foreach (Match match in RelationshipType.Matches(query))
        {
            var id = match.Groups["id"].Value;
            if (!ids.Contains(id, StringComparer.Ordinal))
                ids.Add(id);
        }

        return ids;
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
