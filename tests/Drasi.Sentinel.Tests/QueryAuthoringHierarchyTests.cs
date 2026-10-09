using System.Text.Json.Nodes;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Tests;

public sealed class QueryAuthoringHierarchyTests
{
    [Fact]
    public void With_aggregation_is_not_treated_as_sql()
    {
        var analysis = QueryText.Examine(
            "MATCH (r:orders)-[:PART_OF]->(f:orders) WITH f, avg(r.status) AS level WHERE level > 1 RETURN f.id AS id",
            QueryAuthoringEvalSupport.Graph(),
            ["PART_OF"]);
        Assert.DoesNotContain(analysis.Blocking, item => item.Contains("SQL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Commit_does_not_treat_a_label_word_as_a_stored_row()
    {
        var runtime = new RecordingRuntime
        {
            Graph = new SourceGraph(
                [new GraphNode("Room", [new GraphProperty("id", "string"), new GraphProperty("name", "string")], ["ledger"])],
                [],
                []),
            Row = JsonNode.Parse("""{"id":"room_01","name":"Room 01"}""")!,
        };
        var authoring = Authoring(runtime, ["ledger"]);
        var accepted = await authoring.AttemptJsonAsync(new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:Room) RETURN n.id AS id, n.name AS name",
            ["sources"] = "ledger",
            ["commit_as"] = "any-room",
            ["description"] = "Any room that changes.",
            ["caller_text"] = "Tell me when a room changes.",
        }, CancellationToken.None);
        using var committed = System.Text.Json.JsonDocument.Parse(accepted);
        Assert.Equal("any-room", committed.RootElement.GetProperty("Committed").GetProperty("Id").GetString());

        var named = await authoring.AttemptJsonAsync(new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:Room) RETURN n.id AS id, n.name AS name",
            ["sources"] = "ledger",
            ["commit_as"] = "named-room",
            ["description"] = "One named room.",
            ["caller_text"] = "Tell me when Room 01 changes.",
        }, CancellationToken.None);
        Assert.Contains("Room 01", named, StringComparison.Ordinal);
        Assert.Contains("missing from the query", named, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_requires_properties_only_for_variables_in_return()
    {
        var authoring = Authoring(new RecordingRuntime(), QueryAuthoringEvalSupport.SourceIds);
        var accepted = await authoring.AttemptJsonAsync(new Dictionary<string, string?>
        {
            ["query"] = "MATCH (o:orders)-[:LINK]->(p:payments) RETURN p.order_id AS order_id",
            ["sources"] = "orders-db,payments-db",
            ["joins"] = """[{"id":"LINK","keys":[{"label":"orders","property":"id"},{"label":"payments","property":"order_id"}]}]""",
            ["commit_as"] = "payment-side",
            ["description"] = "Payments reached through a join.",
        }, CancellationToken.None);
        using var committed = System.Text.Json.JsonDocument.Parse(accepted);
        Assert.Equal("payment-side", committed.RootElement.GetProperty("Committed").GetProperty("Id").GetString());
    }

    [Fact]
    public async Task Commit_allows_an_average_when_the_caller_asked_for_one()
    {
        var authoring = Authoring(new RecordingRuntime(), ["ledger"]);
        var accepted = await authoring.AttemptJsonAsync(new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:orders) WITH n, avg(n.status) AS level RETURN n.id AS id, n.status AS status, level",
            ["sources"] = "ledger",
            ["commit_as"] = "average-orders",
            ["description"] = "Average of order status grouped by the order.",
            ["caller_text"] = "Tell me the average status of an order.",
        }, CancellationToken.None);
        using var committed = System.Text.Json.JsonDocument.Parse(accepted);
        Assert.Equal("average-orders", committed.RootElement.GetProperty("Committed").GetProperty("Id").GetString());
    }

    private static QueryAuthoring Authoring(RecordingRuntime runtime, IEnumerable<string> sources) =>
        new(runtime, new InMemoryAuthoredQueryStore(), new LiveCatalog(QueryAuthoringEvalSupport.Catalog()), sources.ToHashSet(StringComparer.Ordinal), new ReactionId("notify"));
}
