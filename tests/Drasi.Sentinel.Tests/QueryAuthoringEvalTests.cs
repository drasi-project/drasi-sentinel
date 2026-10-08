using System.Text.Json;
using System.Text.Json.Nodes;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;
using Drasi.Sentinel.Host;

namespace Drasi.Sentinel.Tests;

public sealed class QueryAuthoringEvalTests
{
    [Fact]
    public void Eval_rejects_sql_and_order_by_and_accepts_a_projected_cypher_query()
    {
        var schema = OrdersGraph();
        var sql = QueryText.Examine("SELECT id FROM orders WHERE status = 'open'", schema, []);
        Assert.NotEmpty(sql.Blocking);
        Assert.Contains("Cypher", sql.Blocking[0], StringComparison.Ordinal);

        var ordered = QueryText.Examine("MATCH (n:orders) RETURN n.id AS id ORDER BY n.id", schema, []);
        Assert.Contains(ordered.Blocking, issue => issue.Contains("ORDER BY", StringComparison.Ordinal));

        var gold = "MATCH (n:orders) WHERE n.status = 'open' RETURN n.id AS id, n.status AS status";
        var analysis = QueryText.Examine(gold, schema, []);
        Assert.Empty(analysis.Blocking);
        Assert.Empty(analysis.UnknownLabels);
        Assert.Empty(analysis.UnknownProperties);
        Assert.Contains("orders", analysis.ReferencedLabels);
        Assert.DoesNotContain("status", analysis.UnknownProperties);
    }

    [Fact]
    public void Eval_flags_unknown_labels_missing_joins_and_keeps_declared_join_ids()
    {
        var schema = OrdersGraph();
        var typo = QueryText.Examine("MATCH (n:order) RETURN n.id AS id", schema, []);
        Assert.Contains("order", typo.UnknownLabels);
        Assert.Equal("orders", typo.Suggestions["order"]);

        var joined = QueryText.Examine(
            "MATCH (a:orders)-[:PLACED]->(b:orders) RETURN a.id AS id",
            schema,
            ["PLACED"]);
        Assert.DoesNotContain("PLACED", joined.UnknownLabels);
        Assert.Empty(joined.Warnings);

        var twoLabels = new SourceGraph(
            [
                new GraphNode("orders", [new GraphProperty("id", "string")], ["ledger"]),
                new GraphNode("shipments", [new GraphProperty("id", "string")], ["ledger"]),
            ],
            [],
            []);
        var missing = QueryText.Examine("MATCH (a:orders), (b:shipments) RETURN a.id AS id", twoLabels, []);
        Assert.NotEmpty(missing.Warnings);
        Assert.Contains("joins", missing.Warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Eval_scaffolds_from_the_only_label_and_not_from_an_unknown_label()
    {
        var schema = OrdersGraph();
        Assert.Equal("MATCH (n:orders) RETURN n.id AS id, n.status AS status", QueryText.Scaffold(schema, null));
        Assert.Null(QueryText.Scaffold(schema, "missing"));
    }

    [Fact]
    public void Skill_states_the_rules_the_eval_depends_on_only_when_authoring_is_enabled()
    {
        var catalog = Catalog();
        var disabled = AgentTurn.Render(catalog);
        Assert.DoesNotContain(AgentTurn.AuthorQueryTool, disabled, StringComparison.Ordinal);

        var enabled = AgentTurn.Render(catalog, new QueryAuthoringPrompt(["ledger"]));
        foreach (var rule in new[]
        {
            "get_graph_schema",
            "get_source_schema",
            "profile_source_schema",
            "test_query",
            "generate_query",
            "add_query",
            "diagnose_query",
            "diagnose_source",
            "watch_query",
            "tail_logs",
            "Cypher",
            "ORDER BY",
            "joins",
            "case-sensitive",
            "subscribe_to_changes",
            "Do not invent source ids",
        })
        {
            Assert.Contains(rule, enabled, StringComparison.Ordinal);
        }

        Assert.Contains("ledger", enabled, StringComparison.Ordinal);
        Assert.DoesNotContain("password", enabled, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tools_are_absent_until_authoring_is_enabled()
    {
        var disabled = AgentToolSession.Create(Unused(), Principal(), new("task", "message"), CancellationToken.None);
        Assert.DoesNotContain(disabled.Tools, tool => tool.Name is QueryWorkbench.TestQueryTool or QueryWorkbench.GetGraphSchemaTool);

        var enabled = AgentToolSession.Create(
            Unused(), Principal(), new("task", "message"), CancellationToken.None, Workbench(new FakeQueryRuntime(), new InMemoryAuthoredQueryStore(), new LiveCatalog(Catalog())));
        Assert.Contains(enabled.Tools, tool => tool.Name == QueryWorkbench.ProfileSourceSchemaTool);
        Assert.Contains(enabled.Tools, tool => tool.Name == QueryWorkbench.DiagnoseQueryTool);
        Assert.Contains(enabled.Tools, tool => tool.Name == QueryWorkbench.WatchQueryTool);
        var author = Assert.Single(enabled.Tools, tool => tool.Name == QueryWorkbench.AddQueryTool);
        Assert.Contains(author.Arguments, argument => argument.Name == "sources" && argument.Required);
        Assert.DoesNotContain(author.Arguments, argument =>
            argument.Name.Contains("url", StringComparison.OrdinalIgnoreCase) ||
            argument.Name.Contains("filter", StringComparison.OrdinalIgnoreCase) ||
            argument.Name.Contains("destination", StringComparison.OrdinalIgnoreCase) ||
            argument.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Authoring_commits_a_tested_query_and_subscribe_uses_it()
    {
        var runtime = new FakeQueryRuntime();
        var store = new InMemoryAuthoredQueryStore();
        var catalog = new LiveCatalog(Catalog());
        var workbench = Workbench(runtime, store, catalog);
        var engine = new FakeNativeEngine();
        await engine.StartAsync(CancellationToken.None);
        var subscriptions = new ConversationSubscriptions(
            catalog.Catalog,
            engine,
            new InMemorySentinelRepository(),
            new TaskEventNotifier(),
            TimeProvider.System);
        var tools = AgentToolSession.Create(
            subscriptions, Principal(), new("task", "message"), CancellationToken.None, workbench);

        var described = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.GetGraphSchemaTool));
        Assert.Equal("graph_schema", described.RootElement.GetProperty("Kind").GetString());
        Assert.Equal("ledger", described.RootElement.GetProperty("SourceIds")[0].GetString());
        Assert.DoesNotContain("password", described.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var profile = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.ProfileSourceSchemaTool, new Dictionary<string, string?>
        {
            ["id"] = "ledger",
            ["sampleLabels"] = "orders",
        }));
        Assert.Equal("open", profile.RootElement.GetProperty("Nodes")[0].GetProperty("Examples").GetProperty("status")[0].GetString());

        var tested = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.TestQueryTool, new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:orders) WHERE n.status = 'open' RETURN n.id AS id",
            ["sources"] = "ledger",
        }));
        Assert.Equal("query_attempt", tested.RootElement.GetProperty("Kind").GetString());
        Assert.Empty(runtime.Added);

        var rejected = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.GenerateQueryTool, new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:orders) WHERE n.status = 'open' RETURN n.id AS id",
            ["sources"] = "other",
            ["commit_as"] = "open-orders",
            ["description"] = "Reports open orders.",
        }));
        Assert.Equal("source_unknown", rejected.RootElement.GetProperty("Code").GetString());

        var sql = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.GenerateQueryTool, new Dictionary<string, string?>
        {
            ["query"] = "SELECT id FROM orders",
            ["sources"] = "ledger",
            ["commit_as"] = "open-orders",
            ["description"] = "Reports open orders.",
        }));
        Assert.Equal("query_attempt", sql.RootElement.GetProperty("Kind").GetString());
        Assert.False(sql.RootElement.TryGetProperty("Committed", out _));
        Assert.Empty(runtime.Added);

        var mismatch = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.GenerateQueryTool, new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:orders) WHERE n.status = 'OPEN' RETURN n.id AS id",
            ["sources"] = "ledger",
            ["commit_as"] = "open-orders",
            ["description"] = "Reports open orders.",
        }));
        Assert.False(mismatch.RootElement.TryGetProperty("Committed", out _));
        Assert.Contains("label has data", mismatch.RootElement.GetProperty("CommitSkippedReason").GetString());
        Assert.Empty(runtime.Added);

        var committed = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.AddQueryTool, new Dictionary<string, string?>
        {
            ["id"] = "open-orders",
            ["query"] = "MATCH (n:orders) WHERE n.status = 'open' RETURN n.id AS id",
            ["sources"] = "ledger",
            ["description"] = "Reports open orders.",
        }));
        Assert.Equal("open-orders", committed.RootElement.GetProperty("Committed").GetProperty("Id").GetString());
        Assert.Single(runtime.Added);

        var diagnosis = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.DiagnoseQueryTool, new Dictionary<string, string?>
        {
            ["id"] = "open-orders",
        }));
        Assert.Equal("Running", diagnosis.RootElement.GetProperty("Status").GetString());

        var replay = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.AddQueryTool, new Dictionary<string, string?>
        {
            ["id"] = "open-orders",
            ["query"] = "MATCH (n:orders) WHERE n.status = 'open' RETURN n.id AS id",
            ["sources"] = "ledger",
            ["description"] = "Reports open orders.",
        }));
        Assert.Equal("open-orders", replay.RootElement.GetProperty("Committed").GetProperty("Id").GetString());
        Assert.Single(runtime.Added);

        var result = await tools.InvokeAsync(AgentTurn.SubscribeTool, new Dictionary<string, string?> { ["query"] = "open-orders" });
        using var subscription = JsonDocument.Parse(result);
        Assert.Equal("subscription_created", subscription.RootElement.GetProperty("Kind").GetString());
        Assert.Equal("open-orders", subscription.RootElement.GetProperty("Subscription").GetProperty("QueryId").GetString());
    }

    [Fact]
    public void Card_explains_authoring_without_asking_for_cypher_or_a_destination()
    {
        using var card = JsonDocument.Parse(JsonSerializer.Serialize(AgentCard.Create(
            ServiceProfile.Demo,
            new A2aAnonymousManifest(),
            new Uri("http://127.0.0.1:8080/"),
            Catalog(),
            queryAuthoring: true)));
        var skills = card.RootElement.GetProperty("skills").EnumerateArray().Select(skill => skill.GetProperty("id").GetString()).ToArray();
        Assert.Contains("author-continuous-query", skills);
        var description = card.RootElement.GetProperty("description").GetString();
        Assert.Contains("describe that change", description);
        Assert.Contains("Do not send Cypher", description);
        Assert.False(card.RootElement.GetProperty("capabilities").GetProperty("pushNotifications").GetBoolean());
    }

    private static SourceGraph OrdersGraph() => new(
        [new GraphNode("orders", [new GraphProperty("id", "string"), new GraphProperty("status", "string")], ["ledger"])],
        [],
        []);

    private static SafeCatalog Catalog()
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

    private static QueryWorkbench Workbench(FakeQueryRuntime runtime, InMemoryAuthoredQueryStore store, LiveCatalog catalog) =>
        new(
            new QueryAuthoring(runtime, store, catalog, new HashSet<string>(["ledger"]), new ReactionId("notify")),
            runtime,
            store,
            catalog,
            new HashSet<string>(["ledger"]));

    private static Principal Principal() => new(new("tenant"), new("owner"));

    private static ConversationSubscriptions Unused() => new(
        Catalog(),
        new FakeNativeEngine(),
        new InMemorySentinelRepository(),
        new TaskEventNotifier(),
        TimeProvider.System);

    private sealed class FakeQueryRuntime : INativeQueryRuntime
    {
        public List<AuthoredQuery> Added { get; } = [];

        public Task<SourceGraph> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OrdersGraph());

        public Task<QueryProbe> ProbeAsync(
            string cypher,
            IReadOnlyList<string> sourceIds,
            IReadOnlyList<QueryJoinSpec> joins,
            TimeSpan readyTimeout,
            CancellationToken cancellationToken)
        {
            if (cypher.Contains("'OPEN'", StringComparison.Ordinal))
                return Task.FromResult(new QueryProbe(true, [], null));
            var row = JsonNode.Parse("""{"id":"1","status":"open"}""")!;
            return Task.FromResult(new QueryProbe(true, [row], null));
        }

        public Task AddAsync(AuthoredQuery query, TimeSpan? readyTimeout, CancellationToken cancellationToken)
        {
            if (Added.Any(existing => existing.Id == query.Id))
                return Task.CompletedTask;
            Added.Add(query);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(AuthoredQuery query, TimeSpan? readyTimeout, CancellationToken cancellationToken) =>
            AddAsync(query, readyTimeout, cancellationToken);

        public Task RemoveAsync(QueryId id, CancellationToken cancellationToken)
        {
            Added.RemoveAll(query => query.Id == id);
            return Task.CompletedTask;
        }

        public Task<JsonNode?> SourceSchemaAsync(string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult<JsonNode?>(JsonNode.Parse("""{"nodes":[{"label":"orders","properties":[{"name":"status","type":"string"}]}],"relations":[]}"""));

        public Task<JsonNode> GraphSchemaAsync(CancellationToken cancellationToken) =>
            Task.FromResult<JsonNode>(JsonNode.Parse("""{"nodes":{},"relations":{},"sourcesWithoutSchema":[]}""")!);

        public Task<IReadOnlyList<ComponentSnapshot>> ListQueriesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ComponentSnapshot>>([new("open-orders", "Running")]);

        public Task<IReadOnlyList<ComponentSnapshot>> ListSourcesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ComponentSnapshot>>([new("ledger", "Running")]);

        public Task<IReadOnlyList<JsonNode>> ResultsAsync(QueryId id, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<JsonNode>>([JsonNode.Parse("""{"id":"1"}""")!]);

        public Task<JsonObject> QueryMetricsAsync(QueryId id, CancellationToken cancellationToken) =>
            Task.FromResult(new JsonObject { ["liveResultsCount"] = 1 });

        public Task<IReadOnlyList<RuntimeLogLine>> TailLogsAsync(string target, string id, TimeSpan window, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RuntimeLogLine>>([]);

        public Task<IReadOnlyList<CapturedDiff>> WatchAsync(QueryId id, TimeSpan window, int max, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapturedDiff>>([]);
    }
}
