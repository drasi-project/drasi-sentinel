using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;
using Drasi.Sentinel.Host;

namespace Drasi.Sentinel.Tests;

public sealed class TtlTests
{
    [Fact]
    public void Admit_rejects_a_missing_token_an_over_max_token_and_unbounded_when_capped()
    {
        var now = DateTimeOffset.Parse("2026-10-12T17:00:00Z");
        var capped = new TtlPolicy(TimeSpan.FromDays(7));
        Assert.Equal("ttl_required", Assert.Throws<SentinelException>(() => TtlToken.Admit(" ", capped, now)).Code);
        Assert.Equal("ttl_invalid", Assert.Throws<SentinelException>(() => TtlToken.Admit("7w", capped, now)).Code);
        Assert.Equal("ttl_exceeds_max", Assert.Throws<SentinelException>(() => TtlToken.Admit("8d", capped, now)).Code);
        Assert.Equal("ttl_unbounded", Assert.Throws<SentinelException>(() => TtlToken.Admit("none", capped, now)).Code);
        Assert.IsType<Unbounded>(TtlToken.Admit("none", TtlPolicy.Uncapped, now));
        Assert.Equal(now.AddDays(7), Assert.IsType<Until>(TtlToken.Admit("7d", capped, now)).Instant);
    }

    [Fact]
    public void Manifest_max_ttl_defaults_to_uncapped_and_parses_a_cap()
    {
        if (OperatingSystem.IsWindows())
            return;
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "sentinel.yaml");
        try
        {
            File.WriteAllText(manifest, """
                service:
                  profile: demo
                  a2a:
                    authentication:
                      mode: anonymous
                sources: []
                queries: []
                reactions:
                  - id: notify
                """);
            Assert.Null(SentinelManifestLoader.Load(manifest).TtlPolicy.Maximum);

            File.WriteAllText(manifest, """
                service:
                  profile: demo
                  a2a:
                    authentication:
                      mode: anonymous
                  max_ttl: 7d
                sources: []
                queries: []
                reactions:
                  - id: notify
                """);
            Assert.Equal(TimeSpan.FromDays(7), SentinelManifestLoader.Load(manifest).TtlPolicy.Maximum);

            File.WriteAllText(manifest, """
                service:
                  profile: demo
                  a2a:
                    authentication:
                      mode: anonymous
                  max_ttl: 7w
                sources: []
                queries: []
                reactions:
                  - id: notify
                """);
            Assert.Contains("max_ttl", Assert.Throws<ManifestLoadException>(() => SentinelManifestLoader.Load(manifest)).Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Expired_watch_is_detached_and_a_due_query_is_kept_while_another_watch_uses_it()
    {
        var now = DateTimeOffset.Parse("2026-10-12T17:00:00Z");
        var time = new FixedTime(now);
        var repository = new InMemorySentinelRepository();
        var store = new InMemoryAuthoredQueryStore();
        var catalog = new LiveCatalog(new SafeCatalog(
            new Dictionary<QueryId, QueryCatalogItem>(),
            new Dictionary<ReactionId, ReactionCatalogItem> { [new("notify")] = new(new("notify")) }));
        var engine = new RecordingEngine();
        var principal = new Principal(new("tenant"), new("owner"));
        await repository.GetOrCreateTaskAsync(principal, new("task"), CancellationToken.None);
        var due = new ConversationSubscription(
            new(Guid.NewGuid()), new("task"), principal.Tenant, principal.Owner,
            new(new("ready"), new("notify"), new("one")),
            SubscriptionState.Active, new("reaction-due"), now.AddHours(-2), new Until(now.AddMinutes(-1)));
        await repository.GetOrCreateSubscriptionAsync(due, CancellationToken.None);
        await store.SaveAsync(new AuthoredQuery(new("ready"), "Ready.", "MATCH (n) RETURN n", ["ledger"], [], new Until(now.AddMinutes(-1))), CancellationToken.None);
        await store.SaveAsync(new AuthoredQuery(new("shared"), "Shared.", "MATCH (n) RETURN n", ["ledger"], [], new Until(now.AddMinutes(-1))), CancellationToken.None);
        await repository.GetOrCreateTaskAsync(principal, new("other"), CancellationToken.None);
        await repository.GetOrCreateSubscriptionAsync(new ConversationSubscription(
            new(Guid.NewGuid()), new("other"), principal.Tenant, principal.Owner,
            new(new("shared"), new("notify"), new("two")),
            SubscriptionState.Active, new("reaction-live"), now, new Until(now.AddHours(1))), CancellationToken.None);

        var expiry = new WatchExpiry(repository, store, engine, engine, catalog, new TaskEventNotifier(), time);
        await expiry.SweepAsync(CancellationToken.None);

        Assert.Equal(SubscriptionState.Expired, (await repository.GetSubscriptionAsync(principal.Owner, new("task"), due.Id, CancellationToken.None))!.State);
        Assert.Contains(due.NativeSubscriptionId, engine.Detached);
        Assert.Null(await store.FindAsync(new("ready"), CancellationToken.None));
        Assert.NotNull(await store.FindAsync(new("shared"), CancellationToken.None));

        await expiry.SweepAsync(CancellationToken.None);
        var events = await repository.ReadEventsAsync(principal.Owner, new("task"), new(0), 10, CancellationToken.None);
        Assert.Equal(2, events.Count(item => item.Content.Contains("expired", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task A_turn_copies_one_lifetime_onto_the_query_and_the_watch()
    {
        var granted = DateTimeOffset.Parse("2026-10-12T17:00:00Z");
        var runtime = new RecordingRuntime();
        var store = new InMemoryAuthoredQueryStore();
        var catalog = new LiveCatalog(QueryAuthoringEvalSupport.Catalog());
        var sources = new HashSet<string>(["ledger"], StringComparer.Ordinal);
        var authoring = new QueryAuthoring(
            runtime, store, catalog, sources, new ReactionId("notify"),
            null, new TtlPolicy(TimeSpan.FromDays(30)), new FixedTime(granted.AddHours(1)));
        var workbench = new QueryWorkbench(authoring, runtime, store, catalog, sources);
        var engine = new FakeNativeEngine();
        await engine.StartAsync(CancellationToken.None);
        var repository = new InMemorySentinelRepository();
        var commands = new ConversationSubscriptions(
            catalog.Catalog, engine, repository, new TaskEventNotifier(), new FixedTime(granted));
        var session = AgentToolSession.Create(
            commands,
            new Principal(new("tenant"), new("owner")),
            new("task", "message"),
            CancellationToken.None,
            workbench,
            "Tell me when an order is ready.",
            new TtlPolicy(TimeSpan.FromDays(30)),
            new FixedTime(granted));

        var committed = await session.InvokeAsync(QueryWorkbench.AddQueryTool, new Dictionary<string, string?>
        {
            ["id"] = "ready-orders",
            ["query"] = "MATCH (n:orders) WHERE n.status = 'ready' RETURN n.id AS id, n.status AS status",
            ["sources"] = "ledger",
            ["description"] = "Ready orders.",
            ["ttl"] = "7d",
        });
        var subscribed = await session.InvokeAsync(AgentTurn.SubscribeTool, new Dictionary<string, string?>
        {
            ["query"] = "ready-orders",
            ["ttl"] = "7d",
        });

        Assert.Contains("\"Id\":\"ready-orders\"", committed, StringComparison.Ordinal);
        Assert.Contains("subscription_created", subscribed, StringComparison.Ordinal);
        var query = await store.FindAsync(new("ready-orders"), CancellationToken.None);
        var subscription = Assert.Single(await repository.ListSubscriptionsAsync(new("owner"), new("task"), CancellationToken.None));
        Assert.Equal(granted.AddDays(7), query!.Expires.Instant);
        Assert.Equal(query.Expires.Instant, subscription.Expires.Instant);
    }

    [Fact]
    public async Task A_due_watch_is_not_reported_as_created()
    {
        var now = DateTimeOffset.Parse("2026-10-12T17:00:00Z");
        var catalog = new LiveCatalog(new SafeCatalog(
            new Dictionary<QueryId, QueryCatalogItem> { [new("ready")] = new(new("ready"), "Ready.", new HashSet<ReactionId> { new("notify") }, ["Ready."]) },
            new Dictionary<ReactionId, ReactionCatalogItem> { [new("notify")] = new(new("notify")) }));
        var repository = new InMemorySentinelRepository();
        var principal = new Principal(new("tenant"), new("owner"));
        await repository.GetOrCreateTaskAsync(principal, new("task"), CancellationToken.None);
        await repository.GetOrCreateSubscriptionAsync(new ConversationSubscription(
            new(Guid.NewGuid()), new("task"), principal.Tenant, principal.Owner,
            new(new("ready"), new("notify"), new("one")),
            SubscriptionState.Active, new("reaction-due"), now.AddHours(-2), new Until(now.AddMinutes(-1))), CancellationToken.None);
        var service = new ConversationSubscriptions(
            catalog.Catalog, new FakeNativeEngine(), repository, new TaskEventNotifier(), new FixedTime(now));

        var ended = await service.InvokeAsync(
            principal, new("task", "message"), new SubscribeToQuery(new("ready"), new("again"), new Until(now.AddHours(1))), CancellationToken.None);

        Assert.Equal("watch_ended", ended.Kind);
        Assert.Equal(SubscriptionState.Active, ended.Subscription!.State);
    }

    [Fact]
    public async Task A_watch_cannot_outlive_its_authored_query()
    {
        var catalog = new LiveCatalog(new SafeCatalog(
            new Dictionary<QueryId, QueryCatalogItem> { [new("ready")] = new(new("ready"), "Ready.", new HashSet<ReactionId> { new("notify") }, ["Ready."]) },
            new Dictionary<ReactionId, ReactionCatalogItem> { [new("notify")] = new(new("notify")) }));
        var store = new InMemoryAuthoredQueryStore();
        await store.SaveAsync(new AuthoredQuery(new("ready"), "Ready.", "MATCH (n) RETURN n", ["ledger"], [], new Until(DateTimeOffset.Parse("2026-10-13T00:00:00Z"))), CancellationToken.None);
        var service = new ConversationSubscriptions(catalog.Catalog, new FakeNativeEngine(), new InMemorySentinelRepository(), new TaskEventNotifier(), TimeProvider.System, store);
        var error = await Assert.ThrowsAsync<SentinelException>(() => service.InvokeAsync(
            new Principal(new("tenant"), new("owner")),
            new("task", "message"),
            new SubscribeToQuery(new("ready"), new("one"), new Unbounded()),
            CancellationToken.None));
        Assert.Equal("ttl_exceeds_query", error.Code);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingEngine : INativeEngine, INativeQueryRuntime
    {
        public List<NativeSubscriptionId> Detached { get; } = [];
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AttachAsync(ConversationSubscription subscription, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DetachAsync(NativeSubscriptionId subscriptionId, CancellationToken cancellationToken)
        {
            Detached.Add(subscriptionId);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<System.Text.Json.Nodes.JsonNode>> GetQueryResultsAsync(QueryId queryId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<System.Text.Json.Nodes.JsonNode>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<SourceGraph> DescribeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<QueryProbe> ProbeAsync(string cypher, IReadOnlyList<string> sourceIds, IReadOnlyList<QueryJoinSpec> joins, TimeSpan readyTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddAsync(AuthoredQuery query, TimeSpan? readyTimeout, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateAsync(AuthoredQuery query, TimeSpan? readyTimeout, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAsync(QueryId id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<System.Text.Json.Nodes.JsonNode?> SourceSchemaAsync(string sourceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<System.Text.Json.Nodes.JsonNode> GraphSchemaAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ComponentSnapshot>> ListQueriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ComponentSnapshot>> ListSourcesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<System.Text.Json.Nodes.JsonNode>> ResultsAsync(QueryId id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<System.Text.Json.Nodes.JsonObject> QueryMetricsAsync(QueryId id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RuntimeLogLine>> TailLogsAsync(string target, string id, TimeSpan window, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CapturedDiff>> WatchAsync(QueryId id, TimeSpan window, int max, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
