using System.Text.Json.Nodes;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Tests;

public sealed class ConversationSubscriptionsTests
{
    [Fact]
    public async Task Task_owns_one_active_subscription_and_replays_ordered_observations()
    {
        var repository = new InMemorySentinelRepository();
        var notifier = new TaskEventNotifier();
        var engine = new FakeNativeEngine();
        await engine.StartAsync(CancellationToken.None);
        var service = new ConversationSubscriptions(Catalog(), engine, repository, notifier, TimeProvider.System);
        var principal = Principal();

        var first = await service.InvokeAsync(principal, new("task-1", "message-1"),
            new SubscribeToQuery(new("query"), new("first")), CancellationToken.None);
        Assert.Equal(SubscriptionState.Active, first.Subscription!.State);
        var repeat = await service.InvokeAsync(principal, new("task-1", "message-2"),
            new SubscribeToQuery(new("query"), new("first")), CancellationToken.None);
        Assert.Equal(first.Subscription.Id, repeat.Subscription!.Id);
        await Assert.ThrowsAsync<SentinelException>(() => service.InvokeAsync(
            principal,
            new("task-1", "message-3"),
            new SubscribeToQuery(new("query"), new("second")),
            CancellationToken.None));

        var ingestion = new NativeResultIngestion(repository, notifier, TimeProvider.System);
        await ingestion.IngestAsync(first.Subscription.NativeSubscriptionId, Diff("order-1"), CancellationToken.None);
        await ingestion.IngestAsync(first.Subscription.NativeSubscriptionId, Diff("order-1"), CancellationToken.None);

        var events = await service.ReadEventsAsync(principal, new("task-1"), new(0), 100, CancellationToken.None);
        Assert.Equal(3, events.Count);
        Assert.Equal([1L, 2L, 3L], events.Select(x => x.Sequence.Value));
        var replay = await service.ReadEventsAsync(principal, new("task-1"), new(1), 100, CancellationToken.None);
        Assert.Equal([2L, 3L], replay.Select(x => x.Sequence.Value));
    }

    [Fact]
    public async Task Cancellation_only_changes_the_owned_task_subscription()
    {
        var repository = new InMemorySentinelRepository();
        var notifier = new TaskEventNotifier();
        var engine = new FakeNativeEngine();
        await engine.StartAsync(CancellationToken.None);
        var service = new ConversationSubscriptions(Catalog(), engine, repository, notifier, TimeProvider.System);
        var principal = Principal();
        var created = await service.InvokeAsync(principal, new("task-1", "message-1"), new SubscribeToQuery(new("query"), new("one")), CancellationToken.None);
        var cancelled = await service.InvokeAsync(principal, new("task-1", "message-2"), new CancelTaskSubscription(created.Subscription!.Id), CancellationToken.None);
        Assert.Equal(SubscriptionState.Cancelled, cancelled.Subscription!.State);
        await Assert.ThrowsAsync<SentinelException>(() => service.InvokeAsync(principal, new("other-task", "message-3"), new CancelTaskSubscription(created.Subscription.Id), CancellationToken.None));
    }

    [Fact]
    public async Task Current_results_are_task_bound_data_artifacts()
    {
        var repository = new InMemorySentinelRepository();
        var notifier = new TaskEventNotifier();
        var engine = new FakeNativeEngine();
        await engine.StartAsync(CancellationToken.None);
        engine.SetQueryResults(new("query"), new JsonObject { ["id"] = "row-1" });
        var service = new ConversationSubscriptions(Catalog(), engine, repository, notifier, TimeProvider.System);
        var principal = Principal();
        var created = await service.InvokeAsync(principal, new("task-1", "message-1"),
            new SubscribeToQuery(new("query"), new("first")), CancellationToken.None);

        var result = await service.InvokeAsync(principal, new("task-1", "message-2"),
            new GetCurrentTaskResults(), CancellationToken.None);

        var artifact = Assert.IsType<CurrentTaskResultsArtifact>(result.Data);
        Assert.Equal("current_query_results", artifact.Type);
        Assert.Equal("task-1", artifact.TaskId);
        Assert.Equal(created.Subscription!.Id.Value.ToString(), artifact.SubscriptionId);
        Assert.Equal("query", artifact.QueryId);
        Assert.Equal("Reports configured changes.", artifact.QueryDescription);
        Assert.Equal("row-1", artifact.Rows.Single().AsObject()["id"]!.GetValue<string>());
        var events = await service.ReadEventsAsync(principal, new("task-1"), new(0), 100, CancellationToken.None);
        var current = JsonNode.Parse(events.Last().Content)!.AsObject();
        Assert.Equal("current_query_results", current["type"]!.GetValue<string>());
        Assert.Equal("Reports configured changes.", current["queryDescription"]!.GetValue<string>());
    }

    [Fact]
    public async Task Current_results_reject_a_task_owned_by_another_principal()
    {
        var repository = new InMemorySentinelRepository();
        var notifier = new TaskEventNotifier();
        var engine = new FakeNativeEngine();
        await engine.StartAsync(CancellationToken.None);
        var service = new ConversationSubscriptions(Catalog(), engine, repository, notifier, TimeProvider.System);
        var owner = Principal();
        await service.InvokeAsync(owner, new("task-1", "message-1"),
            new SubscribeToQuery(new("query"), new("first")), CancellationToken.None);
        var otherOwner = new Principal(new("tenant"), new("other-owner"));

        var exception = await Assert.ThrowsAsync<SentinelException>(() => service.InvokeAsync(
            otherOwner, new("task-1", "message-2"), new GetCurrentTaskResults(), CancellationToken.None));

        Assert.Equal("subscription_not_found", exception.Code);
    }

    private static Principal Principal() => new(new("tenant"), new("owner"));
    private static NativeQueryDiff Diff(string id) => new(
        "Add",
        new JsonObject { ["entity_id"] = "entity-200", ["event_id"] = id },
        null,
        null,
        null,
        null);
    private static SafeCatalog Catalog() => new(
        new Dictionary<QueryId, QueryCatalogItem> { [new("query")] = new(new("query"), "Reports configured changes.", new HashSet<ReactionId> { new("reaction") }, []) },
        new Dictionary<ReactionId, ReactionCatalogItem> { [new("reaction")] = new(new("reaction")) });
}
