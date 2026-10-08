using System.Text.Json;
using System.Text.Json.Nodes;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Tests;

public sealed class SubscriptionInvariantTests
{
    [Fact]
    public async Task Request_replay_returns_the_original_subscription()
    {
        var (service, _, principal) = await CreateAsync();
        var first = await Subscribe(service, principal, "task", "message-1", "request");
        var replay = await Subscribe(service, principal, "task", "message-2", "request");
        Assert.Equal(first.Id, replay.Id);
    }

    [Fact]
    public async Task Request_keys_are_scoped_to_the_task_and_owner()
    {
        var (service, _, principal) = await CreateAsync();
        var first = await Subscribe(service, principal, "task-1", "message-1", "request");
        var otherTask = await Subscribe(service, principal, "task-2", "message-2", "request");
        var otherOwner = await Subscribe(service, Principal("other"), "task-1", "message-3", "request");
        Assert.NotEqual(first.Id, otherTask.Id);
        Assert.NotEqual(first.Id, otherOwner.Id);
    }

    [Fact]
    public async Task Listing_is_scoped_to_the_current_task()
    {
        var (service, _, principal) = await CreateAsync();
        await Subscribe(service, principal, "task-1", "message-1", "one");
        await Subscribe(service, principal, "task-2", "message-2", "two");
        var listed = await service.InvokeAsync(principal, new("task-1", "message-3"), new ListTaskSubscriptions(), CancellationToken.None);
        Assert.Single(listed.Subscriptions!);
    }

    [Fact]
    public async Task Cancel_is_idempotent_and_appends_one_change_event()
    {
        var (service, repository, principal) = await CreateAsync();
        var subscription = await Subscribe(service, principal, "task", "message-1", "request");
        await service.InvokeAsync(principal, new("task", "message-2"), new CancelTaskSubscription(subscription.Id), CancellationToken.None);
        await service.InvokeAsync(principal, new("task", "message-3"), new CancelTaskSubscription(subscription.Id), CancellationToken.None);
        var events = await repository.ReadEventsAsync(principal.Owner, new("task"), new(0), 100, CancellationToken.None);
        Assert.Single(events, x => x.Kind == ConversationEventKind.SubscriptionChanged);
        Assert.Single(events, x => x.Kind == ConversationEventKind.TaskCompleted);
    }

    [Fact]
    public async Task Subscription_rejects_an_unknown_query()
    {
        var (service, _, principal) = await CreateAsync();
        var exception = await Assert.ThrowsAsync<SentinelException>(() =>
            service.InvokeAsync(principal, new("task", "message"), new SubscribeToQuery(new("unknown"), new("request")), CancellationToken.None));
        Assert.Equal("query_unknown", exception.Code);
    }

    [Fact]
    public async Task Subscription_rejects_a_query_with_many_reactions()
    {
        var engine = new FakeNativeEngine();
        await engine.StartAsync(CancellationToken.None);
        var catalog = new SafeCatalog(
            new Dictionary<QueryId, QueryCatalogItem>
            {
                [new("query")] = new(new("query"), "Reports configured changes.", new HashSet<ReactionId> { new("one"), new("two") }, []),
            },
            new Dictionary<ReactionId, ReactionCatalogItem>
            {
                [new("one")] = new(new("one")),
                [new("two")] = new(new("two")),
            });
        var service = new ConversationSubscriptions(catalog, engine, new InMemorySentinelRepository(), new TaskEventNotifier(), TimeProvider.System);
        var exception = await Assert.ThrowsAsync<SentinelException>(() =>
            service.InvokeAsync(Principal(), new("task", "message"), new SubscribeToQuery(new("query"), new("request")), CancellationToken.None));
        Assert.Equal("query_ambiguous", exception.Code);
    }

    [Fact]
    public async Task Failed_attachment_is_persisted_as_degraded()
    {
        var repository = new InMemorySentinelRepository();
        var service = new ConversationSubscriptions(Catalog(), new FailingEngine(), repository, new TaskEventNotifier(), TimeProvider.System);
        var principal = Principal();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Subscribe(service, principal, "task", "message", "request"));
        var subscription = Assert.Single(await repository.ListSubscriptionsAsync(principal.Owner, new("task"), CancellationToken.None));
        Assert.Equal(SubscriptionState.Degraded, subscription.State);
    }

    [Fact]
    public async Task Native_results_reject_inactive_subscriptions()
    {
        var (service, repository, principal) = await CreateAsync();
        var subscription = await Subscribe(service, principal, "task", "message", "request");
        var ingestion = new NativeResultIngestion(repository, new TaskEventNotifier(), TimeProvider.System);
        await ingestion.IngestAsync(subscription.NativeSubscriptionId, Diff("Add", "entity-200", "one"), CancellationToken.None);
        await service.InvokeAsync(principal, new("task", "cancel"), new CancelTaskSubscription(subscription.Id), CancellationToken.None);
        await ingestion.IngestAsync(subscription.NativeSubscriptionId, Diff("Update", "entity-200", "two"), CancellationToken.None);
        var events = await repository.ReadEventsAsync(principal.Owner, new("task"), new(0), 100, CancellationToken.None);
        Assert.Single(events, x => x.Kind == ConversationEventKind.Observation);
    }

    [Fact]
    public async Task Native_diffs_append_complete_structured_data_without_deduplication()
    {
        var (service, repository, principal) = await CreateAsync();
        var subscription = await Subscribe(service, principal, "task", "message", "request");
        var ingestion = new NativeResultIngestion(repository, new TaskEventNotifier(), TimeProvider.System);
        foreach (var diff in new[]
        {
            Diff("Add", "entity-200", "one"),
            Diff("Update", "entity-200", "two"),
            Diff("Delete", "entity-200", "three"),
            Diff("Aggregation", "entity-200", "four"),
            Diff("Noop", "entity-200", "five"),
            Diff("Add", "entity-200", "one"),
        })
            await ingestion.IngestAsync(subscription.NativeSubscriptionId, diff, CancellationToken.None);

        var events = await repository.ReadEventsAsync(principal.Owner, new("task"), new(0), 100, CancellationToken.None);
        var observations = events.Where(x => x.Kind == ConversationEventKind.Observation).ToArray();
        Assert.Equal(6, observations.Length);
        Assert.Equal([2L, 3L, 4L, 5L, 6L, 7L], observations.Select(x => x.Sequence.Value));
        Assert.Equal(["Add", "Update", "Delete", "Aggregation", "Noop", "Add"], observations.Select(DiffType));

        using var artifact = JsonDocument.Parse(observations[1].Content);
        Assert.Equal("native_query_diff", artifact.RootElement.GetProperty("type").GetString());
        Assert.Equal(subscription.Id.Value, artifact.RootElement.GetProperty("subscriptionId").GetGuid());
        Assert.Equal("query", artifact.RootElement.GetProperty("queryId").GetString());
        Assert.Equal("reaction", artifact.RootElement.GetProperty("reactionId").GetString());
        Assert.Equal(JsonValueKind.String, artifact.RootElement.GetProperty("occurredAt").ValueKind);
        Assert.Equal("Update", artifact.RootElement.GetProperty("diffType").GetString());
        Assert.Equal("entity-200", artifact.RootElement.GetProperty("data").GetProperty("entity_id").GetString());
        Assert.Equal("one", artifact.RootElement.GetProperty("before").GetProperty("event_id").GetString());
        Assert.Equal("two", artifact.RootElement.GetProperty("after").GetProperty("event_id").GetString());
        Assert.Equal("group-1", artifact.RootElement.GetProperty("groupingKeys")[0].GetString());
        Assert.Equal("source-1", artifact.RootElement.GetProperty("metadata").GetProperty("source").GetString());
    }

    [Fact]
    public async Task Task_rejects_a_second_subscription()
    {
        var (service, repository, principal) = await CreateAsync();
        var first = await Subscribe(service, principal, "task", "message-1", "first");
        await Assert.ThrowsAsync<SentinelException>(() =>
            Subscribe(service, principal, "task", "message-2", "second"));
        var ingestion = new NativeResultIngestion(repository, new TaskEventNotifier(), TimeProvider.System);
        await ingestion.IngestAsync(first.NativeSubscriptionId, Diff("Add", "entity-200", "event"), CancellationToken.None);
        var events = await repository.ReadEventsAsync(principal.Owner, new("task"), new(0), 100, CancellationToken.None);
        Assert.Single(events, x => x.Kind == ConversationEventKind.Observation);
    }

    [Fact]
    public async Task Cancelling_the_task_subscription_completes_the_task()
    {
        var (service, repository, principal) = await CreateAsync();
        var first = await Subscribe(service, principal, "task", "message-1", "first");
        await service.InvokeAsync(principal, new("task", "message-2"), new CancelTaskSubscription(first.Id), CancellationToken.None);
        var events = await repository.ReadEventsAsync(principal.Owner, new("task"), new(0), 100, CancellationToken.None);
        Assert.Contains(events, x => x.Kind == ConversationEventKind.TaskCompleted);
    }

    [Fact]
    public async Task Unknown_native_subscription_has_no_task_effect()
    {
        var (service, repository, principal) = await CreateAsync();
        await Subscribe(service, principal, "task", "message", "request");
        var ingestion = new NativeResultIngestion(repository, new TaskEventNotifier(), TimeProvider.System);
        await ingestion.IngestAsync(new("unknown"), Diff("Add", "entity-200", "event"), CancellationToken.None);
        var events = await repository.ReadEventsAsync(principal.Owner, new("task"), new(0), 100, CancellationToken.None);
        Assert.DoesNotContain(events, x => x.Kind == ConversationEventKind.Observation);
    }

    private static async Task<(ConversationSubscriptions Service, InMemorySentinelRepository Repository, Principal Principal)> CreateAsync()
    {
        var engine = new FakeNativeEngine();
        await engine.StartAsync(CancellationToken.None);
        var repository = new InMemorySentinelRepository();
        return (new(Catalog(), engine, repository, new TaskEventNotifier(), TimeProvider.System), repository, Principal());
    }

    private static async Task<ConversationSubscription> Subscribe(IConversationSubscriptions service, Principal principal, string task, string message, string key) =>
        (await service.InvokeAsync(principal, new(task, message), new SubscribeToQuery(new("query"), new(key)), CancellationToken.None)).Subscription!;
    private static Principal Principal(string owner = "owner") => new(new("tenant"), new(owner));
    private static string DiffType(ConversationEvent observation)
    {
        using var artifact = JsonDocument.Parse(observation.Content);
        return artifact.RootElement.GetProperty("diffType").GetString()!;
    }
    private static NativeQueryDiff Diff(string type, string entity, string eventId) => new(
        type,
        new JsonObject
        {
            ["entity_id"] = entity,
            ["event_id"] = eventId,
            ["attributes"] = new JsonArray("nested"),
        },
        new JsonObject { ["entity_id"] = entity, ["event_id"] = "one" },
        new JsonObject { ["entity_id"] = entity, ["event_id"] = eventId },
        new JsonArray("group-1"),
        new JsonObject { ["source"] = "source-1" });
    private static SafeCatalog Catalog() => new(
        new Dictionary<QueryId, QueryCatalogItem> { [new("query")] = new(new("query"), "Reports configured changes.", new HashSet<ReactionId> { new("reaction") }, []) },
        new Dictionary<ReactionId, ReactionCatalogItem> { [new("reaction")] = new(new("reaction")) });

    private sealed class FailingEngine : INativeEngine
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AttachAsync(ConversationSubscription subscription, CancellationToken cancellationToken) => Task.FromException(new InvalidOperationException("native unavailable"));
        public Task DetachAsync(NativeSubscriptionId subscriptionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<JsonNode>> GetQueryResultsAsync(QueryId queryId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<JsonNode>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
