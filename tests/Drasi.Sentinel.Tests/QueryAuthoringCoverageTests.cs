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
