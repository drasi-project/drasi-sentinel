using System.Text.Json;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Tests;

public sealed class QueryAuthoringCoverageTests
{
    [Fact]
    public async Task Agent_can_navigate_two_sources_and_commit_a_join()
    {
        var runtime = new RecordingRuntime();
        var store = new InMemoryAuthoredQueryStore();
        var catalog = new LiveCatalog(QueryAuthoringEvalSupport.Catalog());
        var tools = Session(runtime, store, catalog);

        var listed = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.ListSourcesTool));
        var sourceIds = listed.RootElement.GetProperty("Sources").EnumerateArray().Select(source => source.GetProperty("Id").GetString()).ToArray();
        Assert.Equal(QueryAuthoringEvalSupport.SourceIds, sourceIds);
        Assert.DoesNotContain("password", listed.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var orders = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.GetSourceSchemaTool, Args("id", "orders-db")));
        var payments = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.GetSourceSchemaTool, Args("id", "payments-db")));
        Assert.Equal("orders", orders.RootElement.GetProperty("Schema").GetProperty("nodes")[0].GetProperty("label").GetString());
        Assert.Equal("payments", payments.RootElement.GetProperty("Schema").GetProperty("nodes")[0].GetProperty("label").GetString());

        var joins = """[{"id":"PAID_BY","keys":[{"label":"orders","property":"id"},{"label":"payments","property":"order_id"}]}]""";
        var cypher = "MATCH (o:orders)-[:PAID_BY]->(p:payments) WHERE o.status = 'open' RETURN o.id AS id, o.status AS status, p.order_id AS order_id";
        var tested = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.TestQueryTool, new Dictionary<string, string?>
        {
            ["query"] = cypher,
            ["sources"] = "orders-db,payments-db",
            ["joins"] = joins,
        }));
        Assert.Equal("query_attempt", tested.RootElement.GetProperty("Kind").GetString());
        Assert.Empty(runtime.Added);
        var probe = Assert.Single(runtime.Probes, call => call.Cypher == cypher);
        Assert.Equal(QueryAuthoringEvalSupport.SourceIds, probe.Sources);
        Assert.Equal("PAID_BY", Assert.Single(probe.Joins).Id);

        var rejected = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.AddQueryTool, new Dictionary<string, string?>
        {
            ["id"] = "open-paid",
            ["query"] = cypher,
            ["sources"] = "orders-db,missing",
            ["joins"] = joins,
            ["description"] = "Reports an open order with a matching payment.",
        }));
        Assert.Equal("source_unknown", rejected.RootElement.GetProperty("Code").GetString());
        Assert.Empty(runtime.Added);

        var committed = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.AddQueryTool, new Dictionary<string, string?>
        {
            ["id"] = "open-paid",
            ["query"] = cypher,
            ["sources"] = "payments-db,orders-db",
            ["joins"] = joins,
            ["description"] = "Reports an open order with a matching payment.",
        }));
        Assert.Equal("open-paid", committed.RootElement.GetProperty("Committed").GetProperty("Id").GetString());
        QueryAuthoringEvalSupport.AssertCrossSourceJoin(Assert.Single(runtime.Added));

        runtime.Added.Clear();
        var alternate = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.AddQueryTool, new Dictionary<string, string?>
        {
            ["id"] = "open-linked",
            ["query"] = "MATCH (o:orders)-[:LINK]->(p:payments) RETURN o.id AS id, o.status AS status, p.order_id AS order_id",
            ["sources"] = """["orders-db","payments-db"]""",
            ["joins"] = """{"Id":"LINK","Keys":["orders.id","payments.order_id"]}""",
            ["description"] = "Reports an order linked to a payment.",
        }));
        Assert.Equal("open-linked", alternate.RootElement.GetProperty("Committed").GetProperty("Id").GetString());
        Assert.Equal("LINK", Assert.Single(runtime.Added).Joins[0].Id);
    }

    [Fact]
    public async Task Debugging_tools_report_status_logs_and_diffs_and_can_replace_an_authored_query()
    {
        var runtime = new RecordingRuntime();
        var store = new InMemoryAuthoredQueryStore();
        var catalog = new LiveCatalog(QueryAuthoringEvalSupport.Catalog());
        var tools = Session(runtime, store, catalog);
        var joins = """[{"id":"PAID_BY","keys":[{"label":"orders","property":"id"},{"label":"payments","property":"order_id"}]}]""";
        await tools.InvokeAsync(QueryWorkbench.AddQueryTool, new Dictionary<string, string?>
        {
            ["id"] = "open-paid",
            ["query"] = "MATCH (o:orders)-[:PAID_BY]->(p:payments) RETURN o.id AS id, o.status AS status, p.order_id AS order_id",
            ["sources"] = "orders-db,payments-db",
            ["joins"] = joins,
            ["description"] = "Reports an order with a matching payment.",
        });

        var diagnosis = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.DiagnoseQueryTool, Args("id", "open-paid")));
        Assert.Equal("Running", diagnosis.RootElement.GetProperty("Status").GetString());
        var source = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.DiagnoseSourceTool, new Dictionary<string, string?>
        {
            ["id"] = "orders-db",
            ["expectLabels"] = "orders",
        }));
        Assert.Equal("healthy", source.RootElement.GetProperty("Verdict").GetString());
        var missing = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.DiagnoseSourceTool, new Dictionary<string, string?>
        {
            ["id"] = "orders-db",
            ["expectLabels"] = "payments",
        }));
        Assert.Equal("problem", missing.RootElement.GetProperty("Verdict").GetString());
        Assert.Contains("payments", missing.RootElement.GetProperty("MissingLabels").EnumerateArray().Select(label => label.GetString()));

        var metrics = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.GetQueryMetricsTool, Args("id", "open-paid")));
        Assert.Equal(1, metrics.RootElement.GetProperty("Metrics").GetProperty("liveResultsCount").GetInt32());
        var logs = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.TailLogsTool, new Dictionary<string, string?>
        {
            ["target"] = "source",
            ["id"] = "payments-db",
            ["contains"] = "replication",
        }));
        Assert.Contains("replication slot missing", logs.RootElement.GetProperty("Logs")[0].GetProperty("Message").GetString());
        var watch = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.WatchQueryTool, Args("queryId", "open-paid")));
        Assert.Equal("Add", watch.RootElement.GetProperty("Deltas")[0].GetProperty("Type").GetString());

        var updated = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.UpdateQueryTool, new Dictionary<string, string?>
        {
            ["id"] = "open-paid",
            ["query"] = "MATCH (o:orders)-[:PAID_BY]->(p:payments) WHERE o.status = 'open' RETURN o.id AS id, o.status AS status, p.order_id AS order_id",
            ["sources"] = "orders-db,payments-db",
            ["joins"] = joins,
        }));
        Assert.Equal("query_updated", updated.RootElement.GetProperty("Kind").GetString());
        Assert.Contains("WHERE", Assert.Single(runtime.Added).Cypher, StringComparison.Ordinal);

        var curated = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.RemoveQueryTool, Args("id", "sample")));
        Assert.Equal("query_rejected", curated.RootElement.GetProperty("Code").GetString());
        var removed = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.RemoveQueryTool, Args("id", "open-paid")));
        Assert.Equal("query_removed", removed.RootElement.GetProperty("Kind").GetString());
        Assert.Empty(runtime.Added);
    }

    [Fact]
    public async Task A_named_shared_property_is_a_join_key_not_a_stored_value()
    {
        var shipments = new GraphNode(
            "shipments",
            [new GraphProperty("status", "string"), new GraphProperty("bay_code", "string"), new GraphProperty("ref", "string")],
            ["yard"]);
        var docks = new GraphNode(
            "docks",
            [new GraphProperty("zone", "string"), new GraphProperty("bay_code", "string"), new GraphProperty("ref", "string")],
            ["yard"]);
        var stored = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = ["held", "moving"],
            ["zone"] = ["north", "south"],
            ["bay_code"] = ["B1", "B2"],
            ["ref"] = ["R1", "R2"],
        };
        var mapped = """
            Tell me when a shipment is held and the dock is quiet.
            dock zone
            north
            """;
        var asking = JoinKeys.FollowUp(mapped, QueryWorkbench.ComplementWords(mapped), [shipments, docks], stored);
        Assert.Null(asking.Key);
        Assert.Contains("bay_code", asking.Choices);
        Assert.Contains("ref", asking.Choices);
        Assert.Contains("Do not ask which stored value", asking.Instruction);

        var named = mapped + "\nthe bay code";
        var chosen = JoinKeys.FollowUp(named, QueryWorkbench.ComplementWords(named), [shipments, docks], stored);
        Assert.Equal("bay_code", chosen.Key);
        Assert.Contains("shipments", chosen.Holders);
        Assert.Contains("docks", chosen.Holders);
        Assert.DoesNotContain("B1", chosen.Instruction);
        Assert.Null(JoinKeys.CommitSkip(
            named,
            "MATCH (s:shipments)-[:LINK]->(d:docks) WHERE s.status = 'held' AND d.zone = 'north' RETURN s.status AS status",
            [new QueryJoinSpec("LINK", [new QueryJoinKeySpec("shipments", "bay_code"), new QueryJoinKeySpec("docks", "bay_code")])],
            QueryWorkbench.ComplementWords(named),
            [shipments, docks],
            stored));
        Assert.Contains(
            "join key",
            JoinKeys.CommitSkip(
                named,
                "MATCH (s:shipments) WHERE s.bay_code = 'B1' RETURN s.status AS status",
                [],
                QueryWorkbench.ComplementWords(named),
                [shipments, docks],
                stored));

        var runtime = PickupRuntime();
        var sources = new HashSet<string>(["orders-db", "fleet-db"], StringComparer.Ordinal);
        var authoring = new QueryAuthoring(
            runtime,
            new InMemoryAuthoredQueryStore(),
            new LiveCatalog(QueryAuthoringEvalSupport.Catalog()),
            sources,
            new ReactionId("notify"));
        var location = await authoring.AttemptJsonAsync(new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:orders) WHERE n.status = 'ready' RETURN n.id AS id",
            ["sources"] = "orders-db",
            ["commit_as"] = "waiting-location",
            ["description"] = "A location mapping that still needs a value.",
            ["caller_text"] = """
                Tell me when an order is ready for pickup and the customer is waiting.
                vehicle location
                """,
        }, CancellationToken.None);
        Assert.Contains("Ask which stored value", location, StringComparison.Ordinal);
        Assert.Contains("location", location, StringComparison.Ordinal);

        var plate = await authoring.AttemptJsonAsync(new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:vehicles) WHERE n.plate = 'F1000' RETURN n.plate AS plate",
            ["sources"] = "fleet-db",
            ["commit_as"] = "plate-filter",
            ["description"] = "A guessed plate filter.",
            ["caller_text"] = """
                Tell me when an order is ready for pickup and the customer is waiting.
                vehicle location
                curbside
                the vehicle license plate
                """,
        }, CancellationToken.None);
        Assert.Contains("join key", plate, StringComparison.Ordinal);
        Assert.Contains("plate", plate, StringComparison.Ordinal);
        Assert.DoesNotContain("Ask which stored value", plate, StringComparison.Ordinal);
        Assert.DoesNotContain("A1234", plate, StringComparison.Ordinal);
        Assert.DoesNotContain("G2000", plate, StringComparison.Ordinal);

        var workbench = new QueryWorkbench(authoring, runtime, new InMemoryAuthoredQueryStore(), new LiveCatalog(QueryAuthoringEvalSupport.Catalog()), sources);
        var beforeKey = """
            Tell me when an order is ready for pickup and the customer is waiting.
            vehicle location
            curbside
            """;
        var repaired = await workbench.RepairJoinReplyAsync(
            beforeKey,
            "I can't continue because the sources expose no relationship path.",
            CancellationToken.None);
        Assert.Contains("plate", repaired, StringComparison.Ordinal);
        Assert.Contains("customer_name", repaired, StringComparison.Ordinal);
        var continuation = await workbench.ContinuationAsync(beforeKey + "\nthe vehicle license plate", CancellationToken.None);
        Assert.Contains("join key is plate", continuation);
        Assert.DoesNotContain("F1000", continuation);
    }

    private static RecordingRuntime PickupRuntime()
    {
        var runtime = new RecordingRuntime
        {
            Graph = new SourceGraph(
                [
                    new GraphNode("orders", [new GraphProperty("id", "string"), new GraphProperty("status", "string"), new GraphProperty("plate", "string"), new GraphProperty("customer_name", "string"), new GraphProperty("driver_name", "string")], ["orders-db"]),
                    new GraphNode("vehicles", [new GraphProperty("plate", "string"), new GraphProperty("location", "string"), new GraphProperty("customer_name", "string"), new GraphProperty("driver_name", "string")], ["fleet-db"]),
                ],
                [],
                []),
        };
        runtime.Schemas["orders-db"] = System.Text.Json.Nodes.JsonNode.Parse("""{"nodes":[{"label":"orders","properties":[{"name":"id"},{"name":"status"},{"name":"plate"},{"name":"customer_name"},{"name":"driver_name"}]}],"relations":[]}""");
        runtime.Schemas["fleet-db"] = System.Text.Json.Nodes.JsonNode.Parse("""{"nodes":[{"label":"vehicles","properties":[{"name":"plate"},{"name":"location"},{"name":"customer_name"},{"name":"driver_name"}]}],"relations":[]}""");
        runtime.LabelSamples["orders"] =
        [
            System.Text.Json.Nodes.JsonNode.Parse("""{"id":"1","status":"preparing","plate":"A1234","customer_name":"Ada","driver_name":"Lee"}""")!,
            System.Text.Json.Nodes.JsonNode.Parse("""{"id":"2","status":"ready","plate":"F1000","customer_name":"Noah","driver_name":"Kim"}""")!,
        ];
        runtime.LabelSamples["vehicles"] =
        [
            System.Text.Json.Nodes.JsonNode.Parse("""{"plate":"A1234","location":"Parking","customer_name":"Ada","driver_name":"Lee"}""")!,
            System.Text.Json.Nodes.JsonNode.Parse("""{"plate":"G2000","location":"Curbside","customer_name":"Maya","driver_name":"Oz"}""")!,
        ];
        return runtime;
    }

    private static AgentToolSession Session(RecordingRuntime runtime, InMemoryAuthoredQueryStore store, LiveCatalog catalog) =>
        AgentToolSession.Create(
            new ConversationSubscriptions(
                catalog.Catalog,
                new FakeNativeEngine(),
                new InMemorySentinelRepository(),
                new TaskEventNotifier(),
                TimeProvider.System),
            new Principal(new("tenant"), new("owner")),
            new("task", "message"),
            CancellationToken.None,
            QueryAuthoringEvalSupport.Workbench(runtime, store, catalog));

    private static Dictionary<string, string?> Args(string name, string value) => new() { [name] = value };
}
