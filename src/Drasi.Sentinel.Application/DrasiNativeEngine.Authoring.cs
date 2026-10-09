using System.Text.Json.Nodes;
using Drasi;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public sealed partial class DrasiNativeEngine : INativeQueryRuntime
{
    private readonly Dictionary<string, IReadOnlyList<(string Label, IReadOnlyList<string> Properties)>> _discovered =
        new(StringComparer.Ordinal);

    public async Task<SourceGraph> DescribeAsync(CancellationToken cancellationToken)
    {
        var engine = RequireEngine();
        var nodes = new Dictionary<string, MutableLabel>(StringComparer.Ordinal);
        var relations = new Dictionary<string, MutableRelation>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var source in topology.Sources.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            SourceSchema? schema;
            try
            {
                schema = await engine.GetSourceSchemaAsync(source.Id, cancellationToken);
            }
            catch (DrasiException)
            {
                missing.Add(source.Id);
                continue;
            }

            if (schema is null)
            {
                var discovered = await DiscoverLabelsAsync(source.Id, cancellationToken);
                if (discovered.Count == 0)
                {
                    missing.Add(source.Id);
                    continue;
                }

                foreach (var (label, properties) in discovered)
                {
                    if (!nodes.TryGetValue(label, out var merged))
                        nodes[label] = merged = new MutableLabel(label);
                    merged.Sources.Add(source.Id);
                    foreach (var property in properties)
                        merged.Properties.TryAdd(property, "unknown");
                }

                continue;
            }

            foreach (var node in schema.Nodes)
            {
                if (!nodes.TryGetValue(node.Label, out var merged))
                    nodes[node.Label] = merged = new MutableLabel(node.Label);
                merged.Sources.Add(source.Id);
                foreach (var property in node.Properties)
                    merged.Properties.TryAdd(property.Name, property.DataType);
            }

            foreach (var relation in schema.Relations)
            {
                if (!relations.TryGetValue(relation.Label, out var merged))
                    relations[relation.Label] = merged = new MutableRelation(relation.Label, relation.To);
                merged.To ??= relation.To;
                merged.Sources.Add(source.Id);
                foreach (var property in relation.Properties)
                    merged.Properties.TryAdd(property.Name, property.DataType);
            }
        }

        return new SourceGraph(
            nodes.Values.OrderBy(item => item.Label, StringComparer.Ordinal).Select(item => item.ToNode()).ToArray(),
            relations.Values.OrderBy(item => item.Label, StringComparer.Ordinal).Select(item => item.ToRelation()).ToArray(),
            missing);
    }

    public async Task<QueryProbe> ProbeAsync(
        string cypher,
        IReadOnlyList<string> sourceIds,
        IReadOnlyList<QueryJoinSpec> joins,
        TimeSpan readyTimeout,
        CancellationToken cancellationToken)
    {
        var engine = RequireEngine();
        var id = $"query-probe-{Guid.NewGuid():N}";
        try
        {
            await engine.AddQueryAsync(id, cypher, sourceIds, Options(joins), cancellationToken);
            await engine.WaitForQueryAsync(id, readyTimeout, cancellationToken);
            var rows = await engine.GetQueryResultsAsync(id, cancellationToken);
            return new QueryProbe(true, rows.Select(row => (JsonNode)row.DeepClone()).ToArray(), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new QueryProbe(false, [], string.IsNullOrWhiteSpace(exception.Message) ? "The query could not be tested." : exception.Message);
        }
        finally
        {
            try
            {
                await engine.RemoveQueryAsync(id, CancellationToken.None);
            }
            catch (Exception)
            {
                // The probe query may never have been added.
            }
        }
    }

    public async Task AddAsync(AuthoredQuery query, TimeSpan? readyTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var engine = RequireEngine();
        var nativeId = NativeQueryId(query.Id);
        try
        {
            await engine.AddQueryAsync(nativeId, query.Cypher, query.SourceIds, Options(query.Joins), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!await ReadyAsync(engine, nativeId, readyTimeout, cancellationToken))
                throw;
            return;
        }

        try
        {
            await engine.WaitForQueryAsync(nativeId, readyTimeout, cancellationToken);
        }
        catch
        {
            try
            {
                await engine.RemoveQueryAsync(nativeId, CancellationToken.None);
            }
            catch (Exception)
            {
                // Removal is best-effort after a failed start.
            }

            throw;
        }
    }

    private static async Task<bool> ReadyAsync(Engine engine, string nativeId, TimeSpan? readyTimeout, CancellationToken cancellationToken)
    {
        try
        {
            await engine.WaitForQueryAsync(nativeId, readyTimeout, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task UpdateAsync(AuthoredQuery query, TimeSpan? readyTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var engine = RequireEngine();
        var nativeId = NativeQueryId(query.Id);
        await engine.UpdateQueryAsync(nativeId, query.Cypher, query.SourceIds, Options(query.Joins), cancellationToken);
        await engine.WaitForQueryAsync(nativeId, readyTimeout, cancellationToken);
    }

    public Task RemoveAsync(QueryId id, CancellationToken cancellationToken) =>
        RequireEngine().RemoveQueryAsync(NativeQueryId(id), cancellationToken);

    public async Task<JsonNode?> SourceSchemaAsync(string sourceId, CancellationToken cancellationToken)
    {
        SourceSchema? schema;
        try
        {
            schema = await RequireEngine().GetSourceSchemaAsync(sourceId, cancellationToken);
        }
        catch (DrasiException)
        {
            schema = null;
        }

        if (schema is not null)
            return SchemaJson(schema);
        var discovered = await DiscoverLabelsAsync(sourceId, cancellationToken);
        return discovered.Count == 0 ? null : DiscoveredSchema(discovered);
    }

    public async Task<JsonNode> GraphSchemaAsync(CancellationToken cancellationToken)
    {
        var schema = await RequireEngine().GetGraphSchemaAsync(cancellationToken);
        var nodes = schema.Nodes.DeepClone() as JsonObject ?? new JsonObject();
        var missing = new JsonArray();
        foreach (var id in schema.SourcesWithoutSchema)
        {
            var discovered = await DiscoverLabelsAsync(id, cancellationToken);
            if (discovered.Count == 0)
            {
                missing.Add(id);
                continue;
            }

            foreach (var (label, properties) in discovered)
            {
                if (nodes[label] is not JsonObject entry)
                {
                    entry = new JsonObject
                    {
                        ["sources"] = new JsonArray(),
                        ["properties"] = new JsonArray(properties.Select(name => (JsonNode)new JsonObject
                        {
                            ["name"] = name,
                            ["dataType"] = "unknown",
                        }).ToArray()),
                    };
                    nodes[label] = entry;
                }

                var sources = entry["sources"] as JsonArray ?? new JsonArray();
                if (!sources.Select(item => item?.GetValue<string>()).Contains(id, StringComparer.Ordinal))
                    sources.Add(id);
                entry["sources"] = sources;
            }
        }

        return new JsonObject
        {
            ["nodes"] = nodes,
            ["relations"] = schema.Relations.DeepClone(),
            ["sourcesWithoutSchema"] = missing,
        };
    }

    public async Task<IReadOnlyList<ComponentSnapshot>> ListQueriesAsync(CancellationToken cancellationToken)
    {
        var queries = await RequireEngine().ListQueriesAsync(cancellationToken);
        return queries
            .Select(query => (NativeId: query.Id, CatalogId: CatalogId(query.Id), query.Status))
            .Where(query => query.CatalogId is not null)
            .Select(query => new ComponentSnapshot(query.CatalogId!, query.Status.ToString()))
            .ToArray();
    }

    public async Task<IReadOnlyList<ComponentSnapshot>> ListSourcesAsync(CancellationToken cancellationToken)
    {
        var sources = await RequireEngine().ListSourcesAsync(cancellationToken);
        return sources.Select(source => new ComponentSnapshot(source.Id, source.Status.ToString())).ToArray();
    }

    public async Task<IReadOnlyList<JsonNode>> ResultsAsync(QueryId id, CancellationToken cancellationToken)
    {
        var rows = await RequireEngine().GetQueryResultsAsync(NativeQueryId(id), cancellationToken);
        return rows.Select(row => (JsonNode)row.DeepClone()).ToArray();
    }

    public async Task<JsonObject> QueryMetricsAsync(QueryId id, CancellationToken cancellationToken)
    {
        var metrics = await RequireEngine().GetQueryMetricsAsync(NativeQueryId(id), cancellationToken);
        return new JsonObject
        {
            ["liveResultsCount"] = JsonValue.Create(metrics.LiveResultsCount),
            ["resultSeqAdvances"] = JsonValue.Create(metrics.ResultSeqAdvances),
            ["outboxSize"] = JsonValue.Create(metrics.OutboxSize),
            ["snapshotFetchCount"] = JsonValue.Create(metrics.SnapshotFetchCount),
        };
    }

    public Task<IReadOnlyList<RuntimeLogLine>> TailLogsAsync(
        string target,
        string id,
        TimeSpan window,
        CancellationToken cancellationToken) =>
        CollectLogsAsync(target, id, window, cancellationToken);

    public async Task<IReadOnlyList<CapturedDiff>> WatchAsync(
        QueryId id,
        TimeSpan window,
        int max,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(window);
        var captured = new List<CapturedDiff>();
        try
        {
            await foreach (var change in RequireEngine().QueryResultsAsync(NativeQueryId(id), cancellationToken: timeout.Token))
            {
                foreach (var diff in change.Results)
                {
                    captured.Add(new CapturedDiff(
                        diff.Type.ToString(),
                        diff.Data?.DeepClone(),
                        diff.Before?.DeepClone(),
                        diff.After?.DeepClone(),
                        change.Sequence));
                    if (captured.Count >= max)
                        return captured;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return captured;
    }

    private async Task<IReadOnlyList<RuntimeLogLine>> CollectLogsAsync(
        string target,
        string id,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(window);
        var engine = RequireEngine();
        var stream = target == "source"
            ? engine.SourceLogsAsync(id, timeout.Token)
            : engine.QueryLogsAsync(NativeQueryId(new QueryId(id)), timeout.Token);
        var lines = new List<RuntimeLogLine>();
        try
        {
            await foreach (var line in stream)
            {
                lines.Add(new RuntimeLogLine(line.Timestamp, line.Level, line.Message));
                if (lines.Count >= 200)
                    break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return lines;
    }

    private async Task<IReadOnlyList<(string Label, IReadOnlyList<string> Properties)>> DiscoverLabelsAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        if (_discovered.TryGetValue(sourceId, out var cached))
            return cached;

        var probe = await ProbeAsync("MATCH (n) RETURN n", [sourceId], [], TimeSpan.FromSeconds(20), cancellationToken);
        if (!probe.Ok)
            return [];

        var grouped = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in probe.Rows)
        {
            var names = QuerySamples.NodeProperties([row]);
            foreach (var label in QuerySamples.Labels([row]))
            {
                if (!grouped.TryGetValue(label, out var properties))
                    grouped[label] = properties = new HashSet<string>(StringComparer.Ordinal);
                foreach (var name in names)
                    properties.Add(name);
            }
        }

        var discovered = grouped
            .Select(item => (item.Key, (IReadOnlyList<string>)item.Value.OrderBy(name => name, StringComparer.Ordinal).ToArray()))
            .ToArray();
        _discovered[sourceId] = discovered;
        return discovered;
    }

    private static JsonObject DiscoveredSchema(
        IReadOnlyList<(string Label, IReadOnlyList<string> Properties)> discovered) => new()
    {
        ["nodes"] = new JsonArray(discovered.Select(item => (JsonNode)new JsonObject
        {
            ["label"] = item.Label,
            ["properties"] = new JsonArray(item.Properties.Select(name => (JsonNode)new JsonObject
            {
                ["name"] = name,
                ["type"] = "unknown",
            }).ToArray()),
        }).ToArray()),
        ["relations"] = new JsonArray(),
    };

    private static JsonObject SchemaJson(SourceSchema schema) => new()
    {
        ["nodes"] = new JsonArray(schema.Nodes.Select(node => (JsonNode)new JsonObject
        {
            ["label"] = node.Label,
            ["properties"] = new JsonArray(node.Properties.Select(property => (JsonNode)new JsonObject
            {
                ["name"] = property.Name,
                ["type"] = property.DataType is null ? null : JsonValue.Create(property.DataType),
            }).ToArray()),
        }).ToArray()),
        ["relations"] = new JsonArray(schema.Relations.Select(relation => (JsonNode)new JsonObject
        {
            ["label"] = relation.Label,
            ["to"] = relation.To is null ? null : JsonValue.Create(relation.To),
            ["properties"] = new JsonArray(relation.Properties.Select(property => (JsonNode)new JsonObject
            {
                ["name"] = property.Name,
                ["type"] = property.DataType is null ? null : JsonValue.Create(property.DataType),
            }).ToArray()),
        }).ToArray()),
    };

    private static string? CatalogId(string nativeId) =>
        nativeId.StartsWith("query-probe-", StringComparison.Ordinal) ? null
        : nativeId.StartsWith("query-", StringComparison.Ordinal) ? nativeId["query-".Length..]
        : nativeId;

    private static QueryOptions Options(IReadOnlyList<QueryJoinSpec> joins) => new()
    {
        EnableBootstrap = true,
        Joins = joins.Count == 0
            ? null
            : joins.Select(join => new QueryJoin
            {
                Id = join.Id,
                Keys = join.Keys.Select(key => new QueryJoinKey { Label = key.Label, Property = key.Property }).ToArray(),
            }).ToArray(),
    };

    private static string NativeQueryId(QueryId id) => $"query-{id.Value}";

    private sealed class MutableLabel(string label)
    {
        public string Label { get; } = label;
        public Dictionary<string, string?> Properties { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Sources { get; } = new(StringComparer.Ordinal);

        public GraphNode ToNode() => new(
            Label,
            Properties.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new GraphProperty(item.Key, item.Value)).ToArray(),
            Sources.OrderBy(item => item, StringComparer.Ordinal).ToArray());
    }

    private sealed class MutableRelation(string label, string? to)
    {
        public string Label { get; } = label;
        public string? To { get; set; } = to;
        public Dictionary<string, string?> Properties { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Sources { get; } = new(StringComparer.Ordinal);

        public GraphRelation ToRelation() => new(
            Label,
            To,
            Properties.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new GraphProperty(item.Key, item.Value)).ToArray(),
            Sources.OrderBy(item => item, StringComparer.Ordinal).ToArray());
    }
}
