using System.Text.Json.Nodes;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Tests;

internal static class QueryAuthoringEvalSupport
{
    internal static readonly string[] SourceIds = ["orders-db", "payments-db"];

    internal static SourceGraph Graph() => new(
        [
            new GraphNode("orders", [new GraphProperty("id", "string"), new GraphProperty("status", "string")], ["orders-db"]),
            new GraphNode("payments", [new GraphProperty("order_id", "string")], ["payments-db"]),
        ],
        [],
        []);

    internal static SafeCatalog Catalog()
    {
        var query = new QueryId("sample");
        var reaction = new ReactionId("notify");
        return new SafeCatalog(
            new Dictionary<QueryId, QueryCatalogItem>
            {
                [query] = new(query, "Sample.", new HashSet<ReactionId> { reaction }, ["Show the sample."]),
            },
            new Dictionary<ReactionId, ReactionCatalogItem> { [reaction] = new(reaction) });
    }

    internal static QueryWorkbench Workbench(RecordingRuntime runtime, InMemoryAuthoredQueryStore store, LiveCatalog catalog)
    {
        var sources = SourceIds.ToHashSet(StringComparer.Ordinal);
        return new QueryWorkbench(
            new QueryAuthoring(runtime, store, catalog, sources, new ReactionId("notify")),
            runtime,
            store,
            catalog,
            sources);
    }

    internal static void AssertCrossSourceJoin(AuthoredQuery query) =>
        AssertCrossSourceJoin(query, "PAID_BY");

    internal static void AssertCrossSourceJoin(AuthoredQuery query, string? relationship)
    {
        Assert.Equal(SourceIds, query.SourceIds);
        var join = Assert.Single(query.Joins);
        if (relationship is not null)
            Assert.Equal(relationship, join.Id);
        Assert.Contains(join.Keys, key => key.Label == "orders" && key.Property == "id");
        Assert.Contains(join.Keys, key => key.Label == "payments" && key.Property == "order_id");
        Assert.Contains("orders", query.Cypher, StringComparison.Ordinal);
        Assert.Contains("payments", query.Cypher, StringComparison.Ordinal);
        Assert.Contains(join.Id, query.Cypher, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", query.Cypher, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class RecordingRuntime : INativeQueryRuntime
{
    public List<AuthoredQuery> Added { get; } = [];
    public List<QueryProbeCall> Probes { get; } = [];
    public JsonNode Row { get; set; } = JsonNode.Parse("""{"id":"1","status":"open"}""")!;
    public Dictionary<string, JsonNode?> Schemas { get; } = new(StringComparer.Ordinal);
    public bool FailCombinedProbes { get; set; }

    public SourceGraph? Graph { get; set; }

    public Task<SourceGraph> DescribeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Graph ?? QueryAuthoringEvalSupport.Graph());

    public Task<QueryProbe> ProbeAsync(
        string cypher,
        IReadOnlyList<string> sourceIds,
        IReadOnlyList<QueryJoinSpec> joins,
        TimeSpan readyTimeout,
        CancellationToken cancellationToken)
    {
        Probes.Add(new QueryProbeCall(cypher, sourceIds, joins));
        if (FailCombinedProbes && sourceIds.Count > 1)
            return Task.FromResult(new QueryProbe(false, [], "One source rejected the label."));
        if (cypher.Contains("'OPEN'", StringComparison.Ordinal) || cypher.Contains("'shipped'", StringComparison.Ordinal))
            return Task.FromResult(new QueryProbe(true, [], null));
        var row = cypher.Contains("payments", StringComparison.Ordinal)
            ? JsonNode.Parse("""{"order_id":"1"}""")!
            : Row.DeepClone();
        return Task.FromResult(new QueryProbe(true, [row], null));
    }

    public Exception? AddFailure { get; set; }

    public Task AddAsync(AuthoredQuery query, TimeSpan? readyTimeout, CancellationToken cancellationToken)
    {
        if (AddFailure is not null)
            throw AddFailure;
        if (Added.All(existing => existing.Id != query.Id))
            Added.Add(query);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(AuthoredQuery query, TimeSpan? readyTimeout, CancellationToken cancellationToken)
    {
        Added.RemoveAll(existing => existing.Id == query.Id);
        Added.Add(query);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(QueryId id, CancellationToken cancellationToken)
    {
        Added.RemoveAll(query => query.Id == id);
        return Task.CompletedTask;
    }

    public Task<JsonNode?> SourceSchemaAsync(string sourceId, CancellationToken cancellationToken)
    {
        if (Schemas.TryGetValue(sourceId, out var schema))
            return Task.FromResult(schema?.DeepClone());
        return Task.FromResult<JsonNode?>(sourceId switch
        {
            "orders-db" => JsonNode.Parse("""{"nodes":[{"label":"orders","properties":[{"name":"id","type":"string"},{"name":"status","type":"string"}]}],"relations":[]}"""),
            "payments-db" => JsonNode.Parse("""{"nodes":[{"label":"payments","properties":[{"name":"order_id","type":"string"}]}],"relations":[]}"""),
            _ => null,
        });
    }

    public Task<JsonNode> GraphSchemaAsync(CancellationToken cancellationToken) =>
        Task.FromResult<JsonNode>(JsonNode.Parse("""{"nodes":{"orders":{},"payments":{}},"relations":{},"sourcesWithoutSchema":[]}""")!);

    public Task<IReadOnlyList<ComponentSnapshot>> ListQueriesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ComponentSnapshot>>(Added.Select(query => new ComponentSnapshot(query.Id.Value, "Running")).ToArray());

    public Task<IReadOnlyList<ComponentSnapshot>> ListSourcesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ComponentSnapshot>>(QueryAuthoringEvalSupport.SourceIds.Select(id => new ComponentSnapshot(id, "Running")).ToArray());

    public Task<IReadOnlyList<JsonNode>> ResultsAsync(QueryId id, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<JsonNode>>([JsonNode.Parse("""{"id":"1"}""")!]);

    public Task<JsonObject> QueryMetricsAsync(QueryId id, CancellationToken cancellationToken) =>
        Task.FromResult(new JsonObject { ["liveResultsCount"] = 1, ["resultSeqAdvances"] = 2 });

    public Task<IReadOnlyList<RuntimeLogLine>> TailLogsAsync(string target, string id, TimeSpan window, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RuntimeLogLine>>(id == "payments-db"
            ? [new(DateTimeOffset.UnixEpoch, "error", $"{target}:{id}:replication slot missing")]
            : []);

    public Task<IReadOnlyList<CapturedDiff>> WatchAsync(QueryId id, TimeSpan window, int max, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CapturedDiff>>([new("Add", JsonNode.Parse("""{"id":"1"}"""), null, null, 1)]);
}

internal sealed record QueryProbeCall(string Cypher, IReadOnlyList<string> Sources, IReadOnlyList<QueryJoinSpec> Joins);

internal sealed class RecordingTools(IAgentTools inner) : IAgentTools
{
    public List<(string Name, IReadOnlyDictionary<string, string?>? Arguments)> Calls { get; } = [];
    public IReadOnlyList<AgentTool> Tools => inner.Tools;

    public async Task<string> InvokeAsync(string name, IReadOnlyDictionary<string, string?>? arguments = null)
    {
        Calls.Add((name, arguments));
        return await inner.InvokeAsync(name, arguments);
    }
}
