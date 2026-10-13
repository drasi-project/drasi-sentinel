using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public interface IQueryWorkbench
{
    IReadOnlyList<AgentTool> Tools { get; }

    Task<string> InvokeAsync(
        string name,
        IReadOnlyDictionary<string, string?>? arguments,
        CancellationToken cancellationToken,
        DateTimeOffset? grantedAt = null);

    Task<string?> CoverageGapAsync(string queryId, string callerText, CancellationToken cancellationToken);
}

public sealed class QueryWorkbench(
    IQueryAuthoring authoring,
    INativeQueryRuntime runtime,
    IAuthoredQueryStore store,
    LiveCatalog catalog,
    IReadOnlySet<string> sourceIds,
    Func<QueryId, CancellationToken, Task<bool>>? queryIsWatched = null) : IQueryWorkbench
{
    public const string GetSourceSchemaTool = "get_source_schema";
    public const string GetGraphSchemaTool = "get_graph_schema";
    public const string ProfileSourceSchemaTool = "profile_source_schema";
    public const string TestQueryTool = "test_query";
    public const string GenerateQueryTool = "generate_query";
    public const string AddQueryTool = "add_query";
    public const string UpdateQueryTool = "update_query";
    public const string ListQueriesTool = "list_queries";
    public const string GetQueryResultsTool = "get_query_results";
    public const string RemoveQueryTool = "remove_query";
    public const string DiagnoseQueryTool = "diagnose_query";
    public const string DiagnoseSourceTool = "diagnose_source";
    public const string WatchQueryTool = "watch_query";
    public const string TailLogsTool = "tail_logs";
    public const string GetQueryMetricsTool = "get_query_metrics";
    public const string ListSourcesTool = "list_sources";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex QuotedLiteral = new(@"'([^']+)'", RegexOptions.CultureInvariant);
    private const string JoinArgument = "If a schema relation connects the labels, MATCH it and do not pass joins for that hop. Otherwise pass one joins entry for that hop: [{\"id\":\"LINK\",\"keys\":[{\"label\":\"LabelA\",\"property\":\"key_a\"},{\"label\":\"LabelB\",\"property\":\"key_b\"}]}]. Then MATCH (a:LabelA)-[:LINK]->(b:LabelB). A comma does not join. The id does not have to be LINK.";

    public IReadOnlyList<AgentTool> Tools { get; } =
    [
        new(GetSourceSchemaTool, "Get labels and properties for one configured source. Pass id. If the schema API returns nothing, this samples an unlabeled match and reads labels from the returned nodes. Use those labels. Do not invent labels.", [new AgentToolArgument("id", true)]),
        new(GetGraphSchemaTool, "Get the merged schema. A source that omits a schema is sampled with an unlabeled match. Use every returned label. Do not copy a label onto a source that did not return it.", (string?)null),
        new(ProfileSourceSchemaTool, "Sample a configured source and return property names plus stored example values from current rows, including a value that appears on only one row. Pass id. sampleLabels is an optional comma-separated label list. Use a stored example's exact spelling. A unique token or a unique abbreviation of one stored value is that example. A literal that matches no sample can be a future value. String comparison is case-sensitive.", [new AgentToolArgument("id", true), new AgentToolArgument("sampleLabels", false), new AgentToolArgument("limit", false)]),
        new(TestQueryTool, "Add the query under a temporary id, sample its rows, then remove it. Pass query and sources. This does not save the query. If it returns no rows, do not report that nothing was found. A future value the caller asked to watch can still be committed. " + JoinArgument, [new AgentToolArgument("query", true), new AgentToolArgument("sources", true), new AgentToolArgument("joins", false)]),
        new(GenerateQueryTool, "Validate a draft, or scaffold one when query is omitted. Tests it unless it is not Cypher. Pass commit_as, description, and ttl to save a query that tested without error, including a future value that is not present yet. commit_as is a lowercase id of letters, digits, and hyphens. ttl is none or a positive integer plus s, m, h, or d. sources is required. " + JoinArgument, [new AgentToolArgument("query", false), new AgentToolArgument("sources", true), new AgentToolArgument("joins", false), new AgentToolArgument("label", false), new AgentToolArgument("commit_as", false), new AgentToolArgument("description", false), new AgentToolArgument("ttl", false)]),
        new(AddQueryTool, "Save a tested Cypher query and register it. Pass id, query, sources, description, and ttl. id is a lowercase id of letters, digits, and hyphens. ttl is none or a positive integer plus s, m, h, or d. A taken id is not replaced; the result Committed.Id may differ. The host binds the reaction. Then call subscribe_to_changes with Committed.Id and the same ttl. " + JoinArgument, [new AgentToolArgument("id", true), new AgentToolArgument("query", true), new AgentToolArgument("sources", true), new AgentToolArgument("description", true), new AgentToolArgument("joins", false), new AgentToolArgument("ttl", true)]),
        new(UpdateQueryTool, "Replace the text of an authored query that no watch is using. A watched query is not replaced. Pass id, query, and sources. Curated queries cannot be updated.", [new AgentToolArgument("id", true), new AgentToolArgument("query", true), new AgentToolArgument("sources", true), new AgentToolArgument("joins", false), new AgentToolArgument("description", false)]),
        new(ListQueriesTool, "List queries and whether each is running.", (string?)null),
        new(GetQueryResultsTool, "Return the current rows of a query. Pass id.", [new AgentToolArgument("id", true)]),
        new(RemoveQueryTool, "Remove an authored query. Curated queries cannot be removed. Pass id.", [new AgentToolArgument("id", true)]),
        new(DiagnoseQueryTool, "Status, result metrics, and a short live log window for a query that returns nothing or is not running. Pass id.", [new AgentToolArgument("id", true)]),
        new(DiagnoseSourceTool, "Status, labels, and a short live log window for a configured source. Pass id. expectLabels is optional and is compared with labels from the source schema API.", [new AgentToolArgument("id", true), new AgentToolArgument("expectLabels", false)]),
        new(WatchQueryTool, "Capture add, update, and delete diffs a query emits during a short live window. Pass queryId. This observes the source; it cannot insert rows.", [new AgentToolArgument("queryId", true), new AgentToolArgument("durationMs", false)]),
        new(TailLogsTool, "Read a short live log window for a source or query. Pass target as source or query, and id. level and contains are optional.", [new AgentToolArgument("target", true), new AgentToolArgument("id", true), new AgentToolArgument("level", false), new AgentToolArgument("contains", false)]),
        new(GetQueryMetricsTool, "Return live result count and whether the query is emitting. Pass id. A count of 0 means no row matches yet.", [new AgentToolArgument("id", true)]),
        new(ListSourcesTool, "List configured source ids and status. Connection settings are not included.", (string?)null),
    ];

    public async Task<string?> CoverageGapAsync(string queryId, string callerText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callerText))
            return null;
        var authored = await store.FindAsync(new QueryId(queryId), cancellationToken);
        catalog.Catalog.Queries.TryGetValue(new QueryId(queryId), out var item);
        var covered = authored?.Cypher
            ?? (item is null ? null : item.Description + " " + string.Join(" ", item.Examples));
        if (covered is null)
            return null;
        var labels = (await SchemaLabelsAsync(cancellationToken))
            .Where(label => Mentions(callerText, label))
            .ToArray();
        var missing = labels.Where(label => !covered.Contains(label, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (missing.Length > 0)
            return $"Query '{queryId}' does not cover {string.Join(", ", missing)}. It is only part of the request. Build a new query that includes those labels, commit it, then subscribe to the new id. Do not reuse the narrower query.";
        if (CallerDuration.Condition(callerText) is not null && !covered.Contains("trueFor", StringComparison.OrdinalIgnoreCase))
            return $"Query '{queryId}' does not wait for the duration in this request. Build a new query and subscribe to the new id. Do not replace '{queryId}'.";
        var literals = QuotedLiteral.Matches(covered)
            .Select(match => match.Groups[1].Value)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (literals.Length > 0 &&
            !literals.Any(literal => callerText.Contains(literal, StringComparison.OrdinalIgnoreCase)) &&
            item?.Examples.Any(example => callerText.Contains(example, StringComparison.OrdinalIgnoreCase)) != true)
            return $"Query '{queryId}' filters on {string.Join(", ", literals.Select(literal => "'" + literal + "'"))} and does not match this request. Build a new query and subscribe to the new id. Do not replace '{queryId}'.";
        return null;
    }

    private static bool Mentions(string text, string label)
    {
        if (Regex.IsMatch(text, $@"\b{Regex.Escape(label)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        return label.Length > 4 &&
            label.EndsWith('s') &&
            Regex.IsMatch(text, $@"\b{Regex.Escape(label[..^1])}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public Task<string> InvokeAsync(
        string name,
        IReadOnlyDictionary<string, string?>? arguments,
        CancellationToken cancellationToken,
        DateTimeOffset? grantedAt = null) => name switch
    {
        GetSourceSchemaTool => SourceSchemaAsync(arguments, cancellationToken),
        GetGraphSchemaTool => GraphSchemaAsync(cancellationToken),
        ProfileSourceSchemaTool => ProfileAsync(arguments, cancellationToken),
        TestQueryTool => authoring.AttemptJsonAsync(WithoutCommit(arguments), cancellationToken, grantedAt),
        GenerateQueryTool => authoring.AttemptJsonAsync(arguments ?? new Dictionary<string, string?>(), cancellationToken, grantedAt),
        AddQueryTool => authoring.AttemptJsonAsync(AsCommit(arguments), cancellationToken, grantedAt),
        UpdateQueryTool => authoring.UpdateJsonAsync(arguments ?? new Dictionary<string, string?>(), cancellationToken),
        ListQueriesTool => ListQueriesAsync(cancellationToken),
        GetQueryResultsTool => ResultsAsync(arguments, cancellationToken),
        RemoveQueryTool => RemoveAsync(arguments, cancellationToken),
        DiagnoseQueryTool => DiagnoseQueryAsync(arguments, cancellationToken),
        DiagnoseSourceTool => DiagnoseSourceAsync(arguments, cancellationToken),
        WatchQueryTool => WatchAsync(arguments, cancellationToken),
        TailLogsTool => TailAsync(arguments, cancellationToken),
        GetQueryMetricsTool => MetricsAsync(arguments, cancellationToken),
        ListSourcesTool => ListSourcesAsync(cancellationToken),
        _ => throw new SentinelException("invalid_tool_arguments", "The tool name is not valid."),
    };

    private async Task<string> SourceSchemaAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var id = RequireSource(arguments);
        var reported = await runtime.SourceSchemaAsync(id, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            Kind = "source_schema",
            Id = id,
            Described = reported is not null,
            Method = reported is not null ? "reported" : "none",
            Schema = reported,
        }, Json);
    }

    private async Task<string> GraphSchemaAsync(CancellationToken cancellationToken)
    {
        var schema = await runtime.GraphSchemaAsync(cancellationToken);
        return JsonSerializer.Serialize(new
        {
            Kind = "graph_schema",
            SourceIds = sourceIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            Schema = schema,
        }, Json);
    }

    private async Task<string> ProfileAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var id = RequireSource(arguments);
        var reported = await runtime.SourceSchemaAsync(id, cancellationToken);
        var labels = Labels(arguments, "sampleLabels");
        if (labels.Count == 0 && reported?["nodes"] is JsonArray nodes)
        {
            labels = nodes
                .Select(node => node?["label"]?.GetValue<string>())
                .Where(label => label is not null && Identifier.IsMatch(label))
                .Cast<string>()
                .Take(8)
                .ToArray();
        }

        if (labels.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                Kind = "source_profile",
                Id = id,
                Method = "none",
                Schema = reported,
                Note = "The source schema did not report labels. Do not invent them.",
            }, Json);
        }

        var limit = Math.Clamp(Number(arguments, "limit") ?? 25, 1, 50);
        var sampled = new List<object>();
        foreach (var label in labels.Take(8))
        {
            if (!Identifier.IsMatch(label))
                continue;
            var probe = await runtime.ProbeAsync($"MATCH (n:{label}) RETURN n", [id], [], TimeSpan.FromSeconds(5), cancellationToken);
            var rows = probe.Ok ? probe.Rows.Take(limit).ToArray() : [];
            sampled.Add(new
            {
                Label = label,
                Properties = QuerySamples.PropertyNames(rows),
                Examples = QuerySamples.Examples(rows),
                Sampled = rows.Length,
                Error = probe.Ok ? null : probe.Error,
            });
        }

        return JsonSerializer.Serialize(new
        {
            Kind = "source_profile",
            Id = id,
            Method = reported is null ? "sampled" : "described_and_sampled",
            Schema = reported,
            Nodes = sampled,
            Hint = "Examples are stored values from current rows. Use one of those exact strings. Do not paraphrase the caller.",
        }, Json);
    }

    private async Task<string> ListQueriesAsync(CancellationToken cancellationToken)
    {
        var authored = (await store.ListAsync(cancellationToken)).Select(query => query.Id.Value).ToHashSet(StringComparer.Ordinal);
        var queries = await runtime.ListQueriesAsync(cancellationToken);
        return JsonSerializer.Serialize(new
        {
            Kind = "queries",
            Queries = queries.Select(query => new { query.Id, query.Status, Authored = authored.Contains(query.Id) }).ToArray(),
        }, Json);
    }

    private async Task<string> ResultsAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var id = Required(arguments, "id");
        var rows = await runtime.ResultsAsync(new QueryId(id), cancellationToken);
        return JsonSerializer.Serialize(new { Kind = "query_results", Id = id, RowCount = rows.Count, Rows = rows.Take(50).ToArray() }, Json);
    }

    private async Task<string> RemoveAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var id = new QueryId(Required(arguments, "id"));
        var authored = await store.FindAsync(id, cancellationToken);
        if (authored is null)
        {
            if (catalog.Catalog.Queries.ContainsKey(id))
                throw new SentinelException("query_rejected", "A curated query cannot be removed.");
            throw new SentinelException("query_unknown", $"Query '{id.Value}' does not exist.");
        }

        if (queryIsWatched is not null && await queryIsWatched(id, cancellationToken))
            throw new SentinelException("query_rejected", $"Query '{id.Value}' is already watched. Do not remove it.");

        await runtime.RemoveAsync(id, cancellationToken);
        await store.DeleteAsync(id, cancellationToken);
        catalog.Remove(id);
        return JsonSerializer.Serialize(new { Kind = "query_removed", Id = id.Value }, Json);
    }

    private async Task<string> DiagnoseQueryAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var id = Required(arguments, "id");
        var queries = await runtime.ListQueriesAsync(cancellationToken);
        var entry = queries.FirstOrDefault(query => query.Id == id);
        JsonObject? metrics = null;
        string? metricsError = null;
        try
        {
            metrics = await runtime.QueryMetricsAsync(new QueryId(id), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            metricsError = exception.Message;
        }

        var logs = await SafeLogs("query", id, cancellationToken);
        var running = entry?.Status.Equals("Running", StringComparison.OrdinalIgnoreCase) == true;
        return JsonSerializer.Serialize(new
        {
            Kind = "query_diagnosis",
            Id = id,
            Found = entry is not null,
            Status = entry?.Status ?? "not found",
            Metrics = metrics,
            MetricsError = metricsError,
            RecentLogs = logs,
            Hint = running
                ? "If the result count is 0, sample values with profile_source_schema and retest without the filter. Observe diffs with watch_query."
                : "The query is not running. Run its text through test_query, then update_query.",
        }, Json);
    }

    private async Task<string> DiagnoseSourceAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var id = RequireSource(arguments);
        var sources = await runtime.ListSourcesAsync(cancellationToken);
        var entry = sources.FirstOrDefault(source => source.Id == id);
        JsonNode? schema = null;
        string? schemaError = null;
        try
        {
            schema = await runtime.SourceSchemaAsync(id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            schemaError = exception.Message;
        }

        var labels = schema?["nodes"] is JsonArray nodes
            ? nodes.Select(node => node?["label"]?.GetValue<string>()).Where(label => label is not null).Cast<string>().ToArray()
            : [];
        var expected = Labels(arguments, "expectLabels");
        var missing = expected.Where(label => !labels.Contains(label, StringComparer.Ordinal)).ToArray();
        var logs = await SafeLogs("source", id, cancellationToken);
        var errors = logs.Where(line => line.Level.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(20).ToArray();
        var running = entry?.Status.Equals("Running", StringComparison.OrdinalIgnoreCase) == true;
        var healthy = running && errors.Length == 0 && missing.Length == 0 && schemaError is null;
        return JsonSerializer.Serialize(new
        {
            Kind = "source_diagnosis",
            Id = id,
            Found = entry is not null,
            Status = entry?.Status ?? "not found",
            Running = running,
            SchemaVisible = schema is not null,
            SchemaError = schemaError,
            Labels = labels,
            MissingLabels = missing,
            ErrorCount = errors.Length,
            Errors = errors,
            RecentLogs = logs.Take(40).ToArray(),
            Verdict = healthy ? "healthy" : "problem",
            Hint = healthy
                ? "The source is running and the expected labels are visible."
                : "A running status does not prove rows were loaded. Check missing labels from the source schema and error lines.",
        }, Json);
    }

    private async Task<string> WatchAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var id = Required(arguments, "queryId");
        var window = TimeSpan.FromMilliseconds(Math.Clamp(Number(arguments, "durationMs") ?? 1500, 200, 8000));
        var deltas = await runtime.WatchAsync(new QueryId(id), window, 100, cancellationToken);
        IReadOnlyList<JsonNode> snapshot;
        try
        {
            snapshot = (await runtime.ResultsAsync(new QueryId(id), cancellationToken)).Take(50).ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            snapshot = [];
        }

        return JsonSerializer.Serialize(new
        {
            Kind = "query_watch",
            QueryId = id,
            DeltaCount = deltas.Count,
            Deltas = deltas,
            ResultSnapshot = snapshot,
            Note = "This observes live diffs. It cannot insert rows into a configured source.",
        }, Json);
    }

    private async Task<string> TailAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var target = Required(arguments, "target");
        if (target is not ("source" or "query"))
            throw new SentinelException("invalid_tool_arguments", "target must be source or query.");
        var id = Required(arguments, "id");
        if (target == "source")
            RequireSource(new Dictionary<string, string?> { ["id"] = id });
        var logs = await runtime.TailLogsAsync(target, id, TimeSpan.FromMilliseconds(800), cancellationToken);
        var level = Optional(arguments, "level");
        var contains = Optional(arguments, "contains");
        var filtered = logs.Where(line =>
            (level is null || line.Level.Contains(level, StringComparison.OrdinalIgnoreCase)) &&
            (contains is null || line.Message.Contains(contains, StringComparison.OrdinalIgnoreCase))).Take(80).ToArray();
        return JsonSerializer.Serialize(new
        {
            Kind = "logs",
            Target = target,
            Id = id,
            Count = filtered.Length,
            Logs = filtered,
            Note = "This is a short live window, not a persisted history.",
        }, Json);
    }

    private async Task<string> MetricsAsync(IReadOnlyDictionary<string, string?>? arguments, CancellationToken cancellationToken)
    {
        var id = Required(arguments, "id");
        var metrics = await runtime.QueryMetricsAsync(new QueryId(id), cancellationToken);
        return JsonSerializer.Serialize(new { Kind = "query_metrics", Id = id, Metrics = metrics }, Json);
    }

    private async Task<string> ListSourcesAsync(CancellationToken cancellationToken)
    {
        var sources = await runtime.ListSourcesAsync(cancellationToken);
        var allowed = sources.Where(source => sourceIds.Contains(source.Id)).ToArray();
        return JsonSerializer.Serialize(new { Kind = "sources", Sources = allowed }, Json);
    }

    private async Task<IReadOnlyList<RuntimeLogLine>> SafeLogs(string target, string id, CancellationToken cancellationToken)
    {
        try
        {
            return await runtime.TailLogsAsync(target, id, TimeSpan.FromMilliseconds(800), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [new RuntimeLogLine(DateTimeOffset.UtcNow, "error", exception.Message)];
        }
    }

    private string RequireSource(IReadOnlyDictionary<string, string?>? arguments)
    {
        var id = Required(arguments, "id");
        if (!sourceIds.Contains(id))
            throw new SentinelException("source_unknown", $"Source '{id}' is not configured.");
        return id;
    }

    private static IReadOnlyDictionary<string, string?> AsCommit(IReadOnlyDictionary<string, string?>? arguments)
    {
        var copy = new Dictionary<string, string?>(arguments ?? new Dictionary<string, string?>());
        copy["commit_as"] = Required(arguments, "id");
        return copy;
    }

    private static IReadOnlyDictionary<string, string?> WithoutCommit(IReadOnlyDictionary<string, string?>? arguments)
    {
        var copy = new Dictionary<string, string?>(arguments ?? new Dictionary<string, string?>());
        copy.Remove("commit_as");
        return copy;
    }

    private static IReadOnlyList<string> Labels(IReadOnlyDictionary<string, string?>? arguments, string name) =>
        Optional(arguments, name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private static int? Number(IReadOnlyDictionary<string, string?>? arguments, string name) =>
        int.TryParse(Optional(arguments, name), out var value) ? value : null;

    private static string Required(IReadOnlyDictionary<string, string?>? arguments, string name) =>
        Optional(arguments, name) ?? throw new SentinelException("invalid_tool_arguments", $"Pass {name}.");

    private static string? Optional(IReadOnlyDictionary<string, string?>? arguments, string name) =>
        arguments is not null && arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private async Task<IReadOnlyList<string>> SchemaLabelsAsync(CancellationToken cancellationToken)
    {
        var labels = new List<string>();
        foreach (var id in sourceIds.OrderBy(item => item, StringComparer.Ordinal))
        {
            var schema = await runtime.SourceSchemaAsync(id, cancellationToken);
            if (schema?["nodes"] is not JsonArray nodes)
                continue;
            foreach (var node in nodes)
            {
                var label = node?["label"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(label))
                    labels.Add(label);
            }
        }

        return labels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public static class QuerySamples
{
    private static readonly Regex ElementLabels = new(
        "labels:\\s*\\[(?<labels>[^\\]]*)\\]",
        RegexOptions.CultureInvariant);
    private static readonly Regex QuotedLabel = new(
        "\"(?<label>[A-Za-z_][A-Za-z0-9_]*)\"",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Labels(IReadOnlyList<JsonNode> rows)
    {
        var labels = new List<string>();
        foreach (var row in rows)
            CollectLabels(row, labels);
        return labels;
    }

    private static void CollectLabels(JsonNode? node, List<string> labels)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                foreach (Match match in ElementLabels.Matches(text))
                {
                    foreach (Match quoted in QuotedLabel.Matches(match.Groups["labels"].Value))
                    {
                        var label = quoted.Groups["label"].Value;
                        if (!labels.Contains(label, StringComparer.Ordinal))
                            labels.Add(label);
                    }
                }
                break;
            case JsonObject obj:
                foreach (var property in obj)
                    CollectLabels(property.Value, labels);
                break;
            case JsonArray array:
                foreach (var item in array)
                    CollectLabels(item, labels);
                break;
        }
    }

    public static IReadOnlyList<string> PropertyNames(IReadOnlyList<JsonNode> rows)
    {
        var names = new List<string>();
        foreach (var row in rows)
        {
            foreach (var property in Element(row))
            {
                if (!names.Contains(property.Key, StringComparer.Ordinal))
                    names.Add(property.Key);
            }
        }

        return names;
    }

    public static IReadOnlyList<string> NodeProperties(IReadOnlyList<JsonNode> rows)
    {
        var names = new List<string>();
        foreach (var row in rows)
        {
            foreach (var property in Element(row))
            {
                if (property.Value is JsonObject or JsonArray)
                    continue;
                if (!names.Contains(property.Key, StringComparer.Ordinal))
                    names.Add(property.Key);
            }
        }

        return names;
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<JsonNode>> Examples(IReadOnlyList<JsonNode> rows)
    {
        var values = new Dictionary<string, List<JsonNode>>(StringComparer.Ordinal);
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var property in Element(row))
            {
                if (property.Value is null || property.Value is JsonObject or JsonArray)
                    continue;
                var key = property.Value.ToJsonString();
                if (!seen.TryGetValue(property.Key, out var keys))
                    seen[property.Key] = keys = new HashSet<string>(StringComparer.Ordinal);
                if (!keys.Add(key) || values.GetValueOrDefault(property.Key)?.Count >= 5)
                    continue;
                if (!values.TryGetValue(property.Key, out var list))
                    values[property.Key] = list = [];
                list.Add(property.Value.DeepClone());
            }
        }

        return values.ToDictionary(item => item.Key, item => (IReadOnlyList<JsonNode>)item.Value, StringComparer.Ordinal);
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> Element(JsonNode row)
    {
        if (row is not JsonObject obj)
            yield break;
        if (obj.Count == 1 && obj.First().Value is JsonObject nested)
        {
            foreach (var property in nested)
                yield return property;
            yield break;
        }

        var expanded = false;
        foreach (var property in obj)
        {
            if (property.Value is not JsonValue value ||
                !value.TryGetValue<string>(out var text) ||
                !TryElementProperties(text, out var properties))
                continue;
            expanded = true;
            foreach (var parsed in properties)
                yield return parsed;
        }

        if (expanded)
            yield break;

        foreach (var property in obj)
            yield return property;
    }

    private static bool TryElementProperties(string text, out List<KeyValuePair<string, JsonNode?>> properties)
    {
        properties = [];
        var start = text.IndexOf("values:", StringComparison.Ordinal);
        if (start < 0 || !text.Contains("ElementPropertyMap", StringComparison.Ordinal))
            return false;
        foreach (Match match in ElementProperty.Matches(text[start..]))
            properties.Add(new(match.Groups[1].Value, JsonValue.Create(match.Groups[2].Value)));
        return properties.Count > 0;
    }

    private static readonly Regex ElementProperty = new(
        "\"([A-Za-z_][A-Za-z0-9_]*)\"\\s*:\\s*String\\(\"([^\"]*)\"\\)",
        RegexOptions.CultureInvariant);
}
