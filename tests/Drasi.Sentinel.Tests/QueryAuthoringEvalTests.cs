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
    public void A_reported_relationship_is_not_treated_as_a_missing_join()
    {
        var schema = new SourceGraph(
            [
                new GraphNode("orders", [new GraphProperty("id", "string")], ["ledger"]),
                new GraphNode("payments", [new GraphProperty("order_id", "string")], ["ledger"]),
            ],
            [new GraphRelation("PAID", "payments", [], ["ledger"])],
            []);
        var used = QueryText.Examine(
            "MATCH (o:orders)-[:PAID]->(p:payments) RETURN o.id AS id, p.order_id AS order_id",
            schema,
            []);
        Assert.DoesNotContain(used.Warnings, warning => warning.Contains("declares no joins", StringComparison.Ordinal));
        Assert.DoesNotContain(used.Warnings, warning => warning.Contains("no relationship", StringComparison.OrdinalIgnoreCase));

        var ignored = QueryText.Examine("MATCH (o:orders), (p:payments) RETURN o.id AS id", schema, []);
        Assert.Contains("reported relationship", Assert.Single(ignored.Warnings), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("declares no joins", ignored.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_watch_lifetime_is_not_true_for_and_a_condition_duration_still_is()
    {
        var authoring = Authoring();
        var kept = JsonDocument.Parse(await authoring.AttemptJsonAsync(Commit(
            "open-orders",
            "MATCH (n:orders) WHERE n.status = 'open' RETURN n.id AS id, n.status AS status",
            "Open orders.",
            "Tell me when an order is open. Keep the watch for 7 days."), CancellationToken.None));
        Assert.Equal("open-orders", kept.RootElement.GetProperty("Committed").GetProperty("Id").GetString());

        var waiting = await authoring.AttemptJsonAsync(Commit(
            "open-waiting",
            "MATCH (n:orders) WHERE n.status = 'open' RETURN n.id AS id, n.status AS status",
            "Orders open for ten seconds.",
            "Tell me when an order has been open for 10 seconds. Keep the watch for 7 days."), CancellationToken.None);
        Assert.Contains("trueFor", waiting, StringComparison.Ordinal);
        Assert.Contains("seconds: 10", waiting, StringComparison.Ordinal);
        Assert.DoesNotContain("days: 7", waiting, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Floor_and_case_are_rejected_unless_the_caller_asked()
    {
        var authoring = Authoring();
        var unasked = await authoring.AttemptJsonAsync(Commit(
            "rounded",
            "MATCH (n:orders) RETURN n.id AS id, floor(n.status) AS status",
            "Rounded orders.",
            "Tell me when an order changes."), CancellationToken.None);
        Assert.Contains("did not ask to round", unasked, StringComparison.Ordinal);

        var asked = JsonDocument.Parse(await authoring.AttemptJsonAsync(Commit(
            "rounded",
            "MATCH (n:orders) RETURN n.id AS id, floor(n.status) AS level",
            "Rounded orders.",
            "Round that level down to a whole number."), CancellationToken.None));
        Assert.Equal("rounded", asked.RootElement.GetProperty("Committed").GetProperty("Id").GetString());
    }

    [Fact]
    public async Task A_wide_label_commits_without_projecting_every_property()
    {
        var runtime = new RecordingRuntime
        {
            Graph = new SourceGraph(
                [new GraphNode("orders", [
                    new GraphProperty("id", "string"),
                    new GraphProperty("status", "string"),
                    new GraphProperty("notes", "string"),
                    new GraphProperty("extra", "string"),
                ], ["ledger"])],
                [],
                []),
        };
        var authoring = new QueryAuthoring(
            runtime,
            new InMemoryAuthoredQueryStore(),
            new LiveCatalog(QueryAuthoringEvalSupport.Catalog()),
            new HashSet<string>(["ledger"], StringComparer.Ordinal),
            new ReactionId("notify"));
        var committed = JsonDocument.Parse(await authoring.AttemptJsonAsync(new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:orders) WHERE n.status = 'open' RETURN n.id AS id",
            ["sources"] = "ledger",
            ["commit_as"] = "open-id",
            ["description"] = "Open order ids.",
            ["caller_text"] = "Tell me when an order is open.",
        }, CancellationToken.None));
        Assert.Equal("open-id", committed.RootElement.GetProperty("Committed").GetProperty("Id").GetString());
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
            "uncovered words",
            "was not deployed",
            "Do not ask the caller whether to continue",
            "not a substitute",
            "Once every stated condition is mapped",
            "Do not end with a question that asks whether to start",
        })
        {
            Assert.Contains(rule, enabled, StringComparison.Ordinal);
        }

        Assert.Contains("ledger", enabled, StringComparison.Ordinal);
        Assert.DoesNotContain("password", enabled, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("catalog JSON may be []", enabled, StringComparison.Ordinal);
        Assert.Contains("declares no joins", enabled, StringComparison.Ordinal);
        Assert.Contains("stored values", enabled, StringComparison.Ordinal);
        Assert.Contains("trueFor", enabled, StringComparison.Ordinal);
        Assert.DoesNotContain("Never invent subscription ids, query ids, or states", enabled, StringComparison.Ordinal);
        Assert.Contains("committed id", AgentTurn.Recovery("query_unknown", authoring: true), StringComparison.Ordinal);
        Assert.Contains("Do not invent another id", AgentTurn.Recovery("query_unknown", authoring: true), StringComparison.Ordinal);
        Assert.DoesNotContain("committed id", AgentTurn.Recovery("query_unknown"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subscribe_rejects_a_catalog_query_that_omits_a_named_label()
    {
        var store = new InMemoryAuthoredQueryStore();
        var catalog = new LiveCatalog(QueryAuthoringEvalSupport.Catalog());
        var sources = new HashSet<string>(["ledger", "physical"], StringComparer.Ordinal);
        await store.SaveAsync(new AuthoredQuery(
            new QueryId("pickup-order-ready"),
            "Ready orders.",
            "MATCH (n:orders) WHERE n.status = 'ready' RETURN n.status AS status",
            ["ledger"],
            [],
            new Unbounded()), CancellationToken.None);
        var runtime = new RecordingRuntime();
        runtime.Schemas["ledger"] = JsonNode.Parse("""{"nodes":[{"label":"orders","properties":[]}],"relations":[]}""");
        runtime.Schemas["physical"] = JsonNode.Parse("""{"nodes":[{"label":"vehicles","properties":[]}],"relations":[]}""");
        var workbench = new QueryWorkbench(
            new QueryAuthoring(runtime, store, catalog, sources, new ReactionId("notify")),
            runtime,
            store,
            catalog,
            sources);
        var session = AgentToolSession.Create(
            Unused(),
            Principal(),
            new("task", "message"),
            CancellationToken.None,
            workbench,
            "Tell me when an order is ready for pickup and the vehicle with the same plate is at the curb.");
        var rejected = await session.InvokeAsync(
            AgentTurn.SubscribeTool,
            new Dictionary<string, string?> { ["query"] = "pickup-order-ready" });
        Assert.Contains("vehicles", rejected, StringComparison.Ordinal);
        Assert.Contains("query_rejected", rejected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subscribe_rejects_an_unmapped_condition_and_names_available_fields()
    {
        var store = new InMemoryAuthoredQueryStore();
        var catalog = new LiveCatalog(QueryAuthoringEvalSupport.Catalog());
        var sources = new HashSet<string>(["ledger", "physical"], StringComparer.Ordinal);
        await store.SaveAsync(new AuthoredQuery(
            new QueryId("pickup-order-ready"),
            "Ready orders.",
            "MATCH (n:orders) WHERE n.status = 'ready' RETURN n.id AS id",
            ["ledger"],
            [],
            new Unbounded()), CancellationToken.None);
        var runtime = new RecordingRuntime();
        runtime.Schemas["ledger"] = JsonNode.Parse("""{"nodes":[{"label":"orders","properties":[{"name":"status"},{"name":"plate"}]}],"relations":[]}""");
        runtime.Schemas["physical"] = JsonNode.Parse("""{"nodes":[{"label":"vehicles","properties":[{"name":"location"},{"name":"plate"}]}],"relations":[]}""");
        var workbench = new QueryWorkbench(
            new QueryAuthoring(runtime, store, catalog, sources, new ReactionId("notify")),
            runtime,
            store,
            catalog,
            sources);
        Assert.Null(await workbench.CoverageGapAsync("pickup-order-ready", "Tell me when an order is ready.", CancellationToken.None));
        Assert.Null(await workbench.CoverageGapAsync("pickup-order-ready", "Tell me when a pickup order is ready for pickup.", CancellationToken.None));
        var session = AgentToolSession.Create(
            Unused(),
            Principal(),
            new("task", "message"),
            CancellationToken.None,
            workbench,
            "Tell me when an order is ready and the customer is waiting.");
        var rejected = await session.InvokeAsync(
            AgentTurn.SubscribeTool,
            new Dictionary<string, string?> { ["query"] = "pickup-order-ready" });
        Assert.Contains("mapping_required", rejected, StringComparison.Ordinal);
        Assert.Contains("customer", rejected, StringComparison.Ordinal);
        Assert.Contains("waiting", rejected, StringComparison.Ordinal);
        Assert.Contains("orders", rejected, StringComparison.Ordinal);
        Assert.Contains("status", rejected, StringComparison.Ordinal);
        Assert.Contains("vehicles", rejected, StringComparison.Ordinal);
        Assert.Contains("location", rejected, StringComparison.Ordinal);

        var guessed = await Authoring().AttemptJsonAsync(Commit(
            "customer-waiting",
            "MATCH (n:orders) WHERE n.status = 'ready' RETURN n.id AS id",
            "Ready orders.",
            "Tell me when an order is ready and the customer is waiting."), CancellationToken.None);
        Assert.Contains("uncovered words", guessed, StringComparison.Ordinal);
        Assert.Contains("waiting", guessed, StringComparison.Ordinal);
        Assert.DoesNotContain("Committed", guessed, StringComparison.Ordinal);

        var unseen = await Authoring().AttemptJsonAsync(Commit(
            "held-shipment",
            "MATCH (n:orders) WHERE n.status = 'open' RETURN n.id AS id",
            "The caller asked about a stranded parcel.",
            "Tell me when an order is open and the parcel is stranded."), CancellationToken.None);
        Assert.Contains("uncovered words", unseen, StringComparison.Ordinal);
        Assert.Contains("stranded", unseen, StringComparison.Ordinal);
        Assert.DoesNotContain("Committed", unseen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deploy_failure_is_returned_and_does_not_keep_the_query()
    {
        var runtime = new RecordingRuntime
        {
            AddFailure = new InvalidOperationException("query engine rejected the query"),
            Row = JsonNode.Parse("""{"id":"1","status":"ready"}""")!,
        };
        var store = new InMemoryAuthoredQueryStore();
        var catalog = new LiveCatalog(QueryAuthoringEvalSupport.Catalog());
        var sources = new HashSet<string>(["ledger"], StringComparer.Ordinal);
        runtime.Schemas["ledger"] = JsonNode.Parse("""{"nodes":[{"label":"orders","properties":[{"name":"id"},{"name":"status"}]}],"relations":[]}""");
        var workbench = new QueryWorkbench(
            new QueryAuthoring(runtime, store, catalog, sources, new ReactionId("notify")),
            runtime,
            store,
            catalog,
            sources);
        using var failed = JsonDocument.Parse(await workbench.InvokeAsync(QueryWorkbench.AddQueryTool, new Dictionary<string, string?>
        {
            ["id"] = "ready-orders",
            ["query"] = "MATCH (n:orders) WHERE n.status = 'ready' RETURN n.id AS id",
            ["sources"] = "ledger",
            ["description"] = "Ready orders.",
            ["ttl"] = "none",
        }, CancellationToken.None));
        Assert.Contains("was not deployed", failed.RootElement.GetProperty("CommitSkippedReason").GetString(), StringComparison.Ordinal);
        Assert.Contains("query engine rejected the query", failed.RootElement.GetProperty("CommitSkippedReason").GetString(), StringComparison.Ordinal);
        Assert.False(failed.RootElement.TryGetProperty("Committed", out _));
        Assert.Null(await store.FindAsync(new QueryId("ready-orders"), CancellationToken.None));
        Assert.Empty(runtime.Added);
    }

    [Fact]
    public async Task A_taken_query_id_keeps_the_first_definition()
    {
        var runtime = new RecordingRuntime();
        var store = new InMemoryAuthoredQueryStore();
        var catalog = new LiveCatalog(QueryAuthoringEvalSupport.Catalog());
        var sources = new HashSet<string>(["ledger"], StringComparer.Ordinal);
        var authoring = new QueryAuthoring(runtime, store, catalog, sources, new ReactionId("notify"));
        var ready = "MATCH (n:orders) WHERE n.status = 'ready' RETURN n.id AS id, n.status AS status";
        var preparing = "MATCH (n:orders) WHERE n.status = 'preparing' RETURN n.id AS id, n.status AS status";
        var first = JsonDocument.Parse(await authoring.AttemptJsonAsync(Commit("pickup-order-ready", ready, "Ready pickup orders."), CancellationToken.None));
        Assert.Equal("pickup-order-ready", first.RootElement.GetProperty("Committed").GetProperty("Id").GetString());

        var same = JsonDocument.Parse(await authoring.AttemptJsonAsync(Commit("pickup-order-ready", ready, "Ready pickup orders."), CancellationToken.None));
        Assert.Equal("pickup-order-ready", same.RootElement.GetProperty("Committed").GetProperty("Id").GetString());

        var second = JsonDocument.Parse(await authoring.AttemptJsonAsync(Commit("pickup-order-ready", preparing, "Orders still being prepared."), CancellationToken.None));
        Assert.Equal("pickup-order-ready-2", second.RootElement.GetProperty("Committed").GetProperty("Id").GetString());
        Assert.Equal(ready, (await store.FindAsync(new QueryId("pickup-order-ready"), CancellationToken.None))!.Cypher);
        Assert.Equal(preparing, (await store.FindAsync(new QueryId("pickup-order-ready-2"), CancellationToken.None))!.Cypher);

        var watched = new QueryAuthoring(
            runtime,
            store,
            catalog,
            sources,
            new ReactionId("notify"),
            (_, _) => Task.FromResult(true));
        var error = await Assert.ThrowsAsync<SentinelException>(() => watched.UpdateJsonAsync(new Dictionary<string, string?>
        {
            ["id"] = "pickup-order-ready",
            ["query"] = preparing,
            ["sources"] = "ledger",
        }, CancellationToken.None));
        Assert.Equal("query_rejected", error.Code);
        Assert.Equal(ready, (await store.FindAsync(new QueryId("pickup-order-ready"), CancellationToken.None))!.Cypher);
    }

    [Fact]
    public async Task Subscribe_rejects_a_different_prompt_and_a_different_committed_id()
    {
        var runtime = new RecordingRuntime();
        var store = new InMemoryAuthoredQueryStore();
        var catalog = new LiveCatalog(QueryAuthoringEvalSupport.Catalog());
        var sources = new HashSet<string>(["ledger"], StringComparer.Ordinal);
        runtime.Schemas["ledger"] = JsonNode.Parse("""{"nodes":[{"label":"orders","properties":[{"name":"status"}]}],"relations":[]}""");
        var workbench = new QueryWorkbench(
            new QueryAuthoring(runtime, store, catalog, sources, new ReactionId("notify")),
            runtime,
            store,
            catalog,
            sources);
        var session = AgentToolSession.Create(
            Unused(),
            Principal(),
            new("task", "message"),
            CancellationToken.None,
            workbench,
            "Tell me when a pickup order is still being prepared.");
        await store.SaveAsync(new AuthoredQuery(
            new QueryId("pickup-order-ready"),
            "Ready pickup orders.",
            "MATCH (n:orders) WHERE n.status = 'ready' RETURN n.id AS id, n.status AS status",
            ["ledger"],
            [],
            new Unbounded()), CancellationToken.None);
        catalog.Add(QueryAuthoring.ToCatalogItem(
            (await store.FindAsync(new QueryId("pickup-order-ready"), CancellationToken.None))!,
            new ReactionId("notify")));

        var reused = await session.InvokeAsync(AgentTurn.SubscribeTool, new Dictionary<string, string?> { ["query"] = "pickup-order-ready" });
        Assert.Contains("does not match", reused, StringComparison.Ordinal);
        Assert.Contains("query_rejected", reused, StringComparison.Ordinal);

        var committed = JsonDocument.Parse(await session.InvokeAsync(QueryWorkbench.AddQueryTool, new Dictionary<string, string?>
        {
            ["id"] = "pickup-order-ready",
            ["query"] = "MATCH (n:orders) WHERE n.status = 'preparing' RETURN n.id AS id, n.status AS status",
            ["sources"] = "ledger",
            ["description"] = "Orders still being prepared.",
        }));
        Assert.Equal("pickup-order-ready-2", committed.RootElement.GetProperty("Committed").GetProperty("Id").GetString());
        var wrong = await session.InvokeAsync(AgentTurn.SubscribeTool, new Dictionary<string, string?> { ["query"] = "pickup-order-ready" });
        Assert.Contains("pickup-order-ready-2", wrong, StringComparison.Ordinal);
        Assert.Contains("committed_id", wrong, StringComparison.Ordinal);
        Assert.Equal(
            "MATCH (n:orders) WHERE n.status = 'ready' RETURN n.id AS id, n.status AS status",
            (await store.FindAsync(new QueryId("pickup-order-ready"), CancellationToken.None))!.Cypher);
    }

    private static Dictionary<string, string?> Commit(string id, string query, string description, string? callerText = null) => new()
    {
        ["query"] = query,
        ["sources"] = "ledger",
        ["commit_as"] = id,
        ["description"] = description,
        ["caller_text"] = callerText,
    };

    private static QueryAuthoring Authoring() => new(
        new RecordingRuntime(),
        new InMemoryAuthoredQueryStore(),
        new LiveCatalog(QueryAuthoringEvalSupport.Catalog()),
        new HashSet<string>(["ledger"], StringComparer.Ordinal),
        new ReactionId("notify"));

    [Fact]
    public async Task Commit_rejects_a_phrase_when_the_stored_value_is_in_the_sample()
    {
        var runtime = new RecordingRuntime();
        var store = new InMemoryAuthoredQueryStore();
        var catalog = new LiveCatalog(QueryAuthoringEvalSupport.Catalog());
        var sources = new HashSet<string>(["ledger"], StringComparer.Ordinal);
        var authoring = new QueryAuthoring(runtime, store, catalog, sources, new ReactionId("notify"));
        var rejected = await authoring.AttemptJsonAsync(new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:orders) WHERE n.status = 'already open' RETURN n.id AS id",
            ["sources"] = "ledger",
            ["commit_as"] = "open-orders",
            ["description"] = "Orders described as already open.",
        }, CancellationToken.None);
        Assert.Contains("open", rejected, StringComparison.Ordinal);
        Assert.Contains("Stored values", rejected, StringComparison.Ordinal);
        Assert.Empty(await store.ListAsync(CancellationToken.None));

        var abbreviatedRuntime = new RecordingRuntime
        {
            Row = JsonNode.Parse("""{"id":"1","status":"Curbside"}""")!,
        };
        var abbreviated = await new QueryAuthoring(
            abbreviatedRuntime,
            store,
            catalog,
            sources,
            new ReactionId("notify")).AttemptJsonAsync(new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:orders) WHERE n.status = 'Curb' RETURN n.id AS id",
            ["sources"] = "ledger",
            ["commit_as"] = "curb-orders",
            ["description"] = "Orders at a shortened curb status.",
        }, CancellationToken.None);
        Assert.Contains("Curbside", abbreviated, StringComparison.Ordinal);
        Assert.Contains("Stored values", abbreviated, StringComparison.Ordinal);

        var split = new RecordingRuntime { FailCombinedProbes = true, Row = JsonNode.Parse("""{"id":"1","status":"ready"}""")! };
        var phrase = await new QueryAuthoring(split, store, catalog, new HashSet<string>(["ledger", "other"], StringComparer.Ordinal), new ReactionId("notify"))
            .AttemptJsonAsync(new Dictionary<string, string?>
            {
                ["query"] = "MATCH (n:orders) WHERE n.status = 'ready for pickup' RETURN n.id AS id",
                ["sources"] = "ledger,other",
                ["commit_as"] = "phrase-orders",
                ["description"] = "Orders described with a longer phrase.",
                ["caller_text"] = "Tell me when an order is ready for pickup.",
            }, CancellationToken.None);
        Assert.Contains("ready", phrase, StringComparison.Ordinal);
        Assert.Contains("Stored values", phrase, StringComparison.Ordinal);
        Assert.DoesNotContain("phrase-orders", (await store.ListAsync(CancellationToken.None)).Select(query => query.Id.Value));

        var unfiltered = await authoring.AttemptJsonAsync(new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:orders) RETURN n.id AS id, n.status AS status",
            ["sources"] = "ledger",
            ["commit_as"] = "all-orders",
            ["description"] = "All orders.",
            ["caller_text"] = "Tell me when an order is open.",
        }, CancellationToken.None);
        Assert.Contains("open", unfiltered, StringComparison.Ordinal);
        Assert.Contains("missing from the query", unfiltered, StringComparison.Ordinal);
        Assert.Empty(await store.ListAsync(CancellationToken.None));

        var counted = await authoring.AttemptJsonAsync(new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:orders) WHERE n.status = 'open' RETURN count(n)",
            ["sources"] = "ledger",
            ["commit_as"] = "open-count",
            ["description"] = "Count of open orders.",
            ["caller_text"] = "Tell me when an order is open.",
        }, CancellationToken.None);
        Assert.Contains("not a count", counted, StringComparison.Ordinal);
        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Commit_rejects_a_cross_label_equality_without_joins()
    {
        var runtime = new RecordingRuntime();
        var store = new InMemoryAuthoredQueryStore();
        var catalog = new LiveCatalog(QueryAuthoringEvalSupport.Catalog());
        var sources = QueryAuthoringEvalSupport.SourceIds.ToHashSet(StringComparer.Ordinal);
        var authoring = new QueryAuthoring(runtime, store, catalog, sources, new ReactionId("notify"));
        var rejected = await authoring.AttemptJsonAsync(new Dictionary<string, string?>
        {
            ["query"] = "MATCH (o:orders) MATCH (p:payments) WHERE o.id = p.order_id RETURN o.id AS id",
            ["sources"] = "orders-db,payments-db",
            ["commit_as"] = "paid-orders",
            ["description"] = "Orders with a matching payment.",
        }, CancellationToken.None);
        Assert.Contains("order_id", rejected, StringComparison.Ordinal);
        Assert.Contains("declares no joins", rejected, StringComparison.Ordinal);
        Assert.Contains("LINK", rejected, StringComparison.Ordinal);
        Assert.Empty(await store.ListAsync(CancellationToken.None));

        var accepted = await authoring.AttemptJsonAsync(new Dictionary<string, string?>
        {
            ["query"] = "MATCH (o:orders)-[:LINK]->(p:payments) WHERE o.status = 'open' RETURN o.id AS id, o.status AS status, p.order_id AS order_id",
            ["sources"] = "orders-db,payments-db",
            ["joins"] = """[{"id":"LINK","keys":[{"label":"orders","property":"id"},{"label":"payments","property":"order_id"}]}]""",
            ["commit_as"] = "paid-orders",
            ["description"] = "Orders with a matching payment.",
        }, CancellationToken.None);
        using var committed = JsonDocument.Parse(accepted);
        Assert.Equal("paid-orders", committed.RootElement.GetProperty("Committed").GetProperty("Id").GetString());
    }

    [Fact]
    public async Task Commit_requires_true_for_when_the_caller_names_a_duration()
    {
        var runtime = new RecordingRuntime();
        var store = new InMemoryAuthoredQueryStore();
        var catalog = new LiveCatalog(QueryAuthoringEvalSupport.Catalog());
        var sources = new HashSet<string>(["ledger"], StringComparer.Ordinal);
        var authoring = new QueryAuthoring(runtime, store, catalog, sources, new ReactionId("notify"));
        var rejected = await authoring.AttemptJsonAsync(new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:orders) WHERE n.status = 'open' RETURN n.id AS id",
            ["sources"] = "ledger",
            ["commit_as"] = "open-for-ten",
            ["description"] = "Orders open for ten seconds.",
            ["caller_text"] = "Tell me when an order has been open for 10 seconds.",
        }, CancellationToken.None);
        Assert.Contains("trueFor", rejected, StringComparison.Ordinal);
        Assert.Contains("seconds: 10", rejected, StringComparison.Ordinal);
        Assert.Empty(await store.ListAsync(CancellationToken.None));

        var accepted = await authoring.AttemptJsonAsync(new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:orders) WHERE drasi.trueFor(n.status = 'open', duration({ seconds: 10 })) RETURN n.id AS id, n.status AS status",
            ["sources"] = "ledger",
            ["commit_as"] = "open-for-ten",
            ["description"] = "Orders open for ten seconds.",
            ["caller_text"] = "Tell me when an order has been open for 10 seconds.",
        }, CancellationToken.None);
        using var committed = JsonDocument.Parse(accepted);
        Assert.Equal("open-for-ten", committed.RootElement.GetProperty("Committed").GetProperty("Id").GetString());
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

        var wrongCase = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.GenerateQueryTool, new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:orders) WHERE n.status = 'OPEN' RETURN n.id AS id",
            ["sources"] = "ledger",
            ["commit_as"] = "future-orders",
            ["description"] = "Reports open orders.",
        }));
        Assert.False(wrongCase.RootElement.TryGetProperty("Committed", out _));
        Assert.Contains("open", wrongCase.RootElement.GetProperty("CommitSkippedReason").GetString());
        Assert.DoesNotContain(runtime.Added, query => query.Id.Value == "future-orders");

        var future = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.GenerateQueryTool, new Dictionary<string, string?>
        {
            ["query"] = "MATCH (n:orders) WHERE n.status = 'shipped' RETURN n.id AS id, n.status AS status",
            ["sources"] = "ledger",
            ["commit_as"] = "future-orders",
            ["description"] = "Reports shipped orders.",
        }));
        Assert.Equal("future-orders", future.RootElement.GetProperty("Committed").GetProperty("Id").GetString());
        Assert.Contains("future value", future.RootElement.GetProperty("Hint").GetString());
        Assert.Contains(runtime.Added, query => query.Id.Value == "future-orders");

        var committed = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.AddQueryTool, new Dictionary<string, string?>
        {
            ["id"] = "open-orders",
            ["query"] = "MATCH (n:orders) WHERE n.status = 'open' RETURN n.id AS id, n.status AS status",
            ["sources"] = "ledger",
            ["description"] = "Reports open orders.",
        }));
        Assert.Equal("open-orders", committed.RootElement.GetProperty("Committed").GetProperty("Id").GetString());
        Assert.Contains(runtime.Added, query => query.Id.Value == "open-orders");

        var diagnosis = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.DiagnoseQueryTool, new Dictionary<string, string?>
        {
            ["id"] = "open-orders",
        }));
        Assert.Equal("Running", diagnosis.RootElement.GetProperty("Status").GetString());

        var replay = JsonDocument.Parse(await tools.InvokeAsync(QueryWorkbench.AddQueryTool, new Dictionary<string, string?>
        {
            ["id"] = "open-orders",
            ["query"] = "MATCH (n:orders) WHERE n.status = 'open' RETURN n.id AS id, n.status AS status",
            ["sources"] = "ledger",
            ["description"] = "Reports open orders.",
        }));
        Assert.Equal("open-orders", replay.RootElement.GetProperty("Committed").GetProperty("Id").GetString());
        Assert.Equal(1, runtime.Added.Count(query => query.Id.Value == "open-orders"));

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
        Assert.True(card.RootElement.GetProperty("capabilities").GetProperty("pushNotifications").GetBoolean());
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

    [Fact]
    public void Unlabeled_match_rows_expose_labels_from_element_metadata()
    {
        var row = JsonNode.Parse("""
            {"n":"Element(Node { metadata: ElementMetadata { labels: [\"widgets\"], effective_from: 1 }, properties: ElementPropertyMap { values: {\"code\": String(\"ready\")} } })"}
            """)!;
        Assert.Equal(["widgets"], QuerySamples.Labels([row]));
        Assert.Equal(["code"], QuerySamples.NodeProperties([row]));
    }

    [Fact]
    public void Node_properties_read_drasi_element_strings()
    {
        var row = JsonNode.Parse("""
            {"n":"Element(Node { properties: ElementPropertyMap { values: {\"location\": String(\"Parking\"), \"plate\": String(\"A1234\")} } })"}
            """)!;
        var names = QuerySamples.NodeProperties([row]);
        Assert.Equal(["location", "plate"], names);
        Assert.Equal("Parking", QuerySamples.Examples([row])["location"][0]!.GetValue<string>());
    }

    [Fact]
    public async Task Source_schema_does_not_invent_labels_when_the_plugin_omits_a_schema()
    {
        var runtime = new RecordingRuntime();
        var catalog = new LiveCatalog(QueryAuthoringEvalSupport.Catalog());
        var store = new InMemoryAuthoredQueryStore();
        var sources = new HashSet<string>(StringComparer.Ordinal) { "physical-ops" };
        var workbench = new QueryWorkbench(
            new QueryAuthoring(runtime, store, catalog, sources, new ReactionId("notify")),
            runtime,
            store,
            catalog,
            sources);

        using var described = JsonDocument.Parse(await workbench.InvokeAsync(
            QueryWorkbench.GetSourceSchemaTool,
            new Dictionary<string, string?> { ["id"] = "physical-ops" },
            CancellationToken.None));

        Assert.Equal("none", described.RootElement.GetProperty("Method").GetString());
        Assert.False(described.RootElement.GetProperty("Described").GetBoolean());
        Assert.Empty(runtime.Probes);
    }

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
            if (cypher.Contains("'OPEN'", StringComparison.Ordinal) || cypher.Contains("'shipped'", StringComparison.Ordinal))
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
