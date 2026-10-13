using System.Text.Json.Nodes;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Tests;

public sealed class WatchRestoreTests
{
    [Fact]
    public async Task Restore_reattaches_open_watches_and_leaves_ended_ones()
    {
        var repository = new InMemorySentinelRepository();
        var engine = new RecordingEngine();
        var service = Service(repository, engine);
        var principal = new Principal(new("tenant"), new("owner"));
        var active = await service.InvokeAsync(principal, new("live", "m1"), new SubscribeToQuery(new("query"), new("one"), new Unbounded()), CancellationToken.None);
        var degraded = await Arm(repository, principal, "stuck", SubscriptionState.Degraded);
        var ended = await service.InvokeAsync(principal, new("done", "m1"), new SubscribeToQuery(new("query"), new("one"), new Unbounded()), CancellationToken.None);
        await service.InvokeAsync(principal, new("done", "m2"), new CancelTaskSubscription(ended.Subscription!.Id), CancellationToken.None);
        engine.Attached.Clear();

        await service.RestoreWatchesAsync(CancellationToken.None);

        Assert.Equal(2, engine.Attached.Count);
        Assert.Contains(engine.Attached, item => item.TaskId.Value == "live" && item.State == SubscriptionState.Active);
        Assert.Contains(engine.Attached, item => item.TaskId.Value == "stuck" && item.State == SubscriptionState.Active);
        Assert.DoesNotContain(engine.Attached, item => item.TaskId.Value == "done");
        var restored = engine.Attached.Single(item => item.TaskId.Value == "live");
        Assert.Equal(active.Subscription!.NativeSubscriptionId, restored.NativeSubscriptionId);
        var events = await repository.ReadEventsAsync(principal.Owner, new("live"), new(0), 20, CancellationToken.None);
        Assert.DoesNotContain(events, item => item.Kind == ConversationEventKind.SubscriptionCreated && item.Sequence.Value > 1);
        Assert.Equal(SubscriptionState.Active, (await repository.GetSubscriptionAsync(principal.Owner, degraded.TaskId, degraded.Id, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Restore_marks_only_the_failed_watch_degraded()
    {
        var repository = new InMemorySentinelRepository();
        var engine = new RecordingEngine { FailTask = "bad" };
        var service = Service(repository, engine);
        var principal = new Principal(new("tenant"), new("owner"));
        await service.InvokeAsync(principal, new("good", "m1"), new SubscribeToQuery(new("query"), new("one"), new Unbounded()), CancellationToken.None);
        await service.InvokeAsync(principal, new("bad", "m1"), new SubscribeToQuery(new("query"), new("one"), new Unbounded()), CancellationToken.None);
        engine.FailRestores = true;

        await service.RestoreWatchesAsync(CancellationToken.None);

        var good = (await repository.ListSubscriptionsAsync(principal.Owner, new("good"), CancellationToken.None)).Single();
        var bad = (await repository.ListSubscriptionsAsync(principal.Owner, new("bad"), CancellationToken.None)).Single();
        Assert.Equal(SubscriptionState.Active, good.State);
        Assert.Equal(SubscriptionState.Degraded, bad.State);
        Assert.Contains(engine.Attached, item => item.TaskId.Value == "good");
        var failure = (await repository.ReadEventsAsync(principal.Owner, new("bad"), new(0), 20, CancellationToken.None))
            .Single(item => item.Kind == ConversationEventKind.SubscriptionChanged);
        Assert.Contains("could not be reattached", failure.Content, StringComparison.Ordinal);
        Assert.Contains("native unavailable", failure.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_of_a_degraded_watch_is_active_before_attach()
    {
        var repository = new InMemorySentinelRepository();
        var engine = new RecordingEngine();
        var service = Service(repository, engine);
        var principal = new Principal(new("tenant"), new("owner"));
        var degraded = await Arm(repository, principal, "stuck", SubscriptionState.Degraded);

        var retried = await service.InvokeAsync(principal, new("stuck", "m2"), new SubscribeToQuery(new("query"), new("one"), new Unbounded()), CancellationToken.None);

        var attached = Assert.Single(engine.Attached);
        Assert.Equal(degraded.NativeSubscriptionId, attached.NativeSubscriptionId);
        Assert.Equal(SubscriptionState.Active, attached.State);
        Assert.Equal(SubscriptionState.Active, retried.Subscription!.State);
        var events = await repository.ReadEventsAsync(principal.Owner, new("stuck"), new(0), 20, CancellationToken.None);
        Assert.Contains(events, item => item.Kind == ConversationEventKind.SubscriptionChanged && item.Content.Contains("active again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Degraded_watch_keeps_a_diff_instead_of_dropping_it()
    {
        var repository = new InMemorySentinelRepository();
        var principal = new Principal(new("tenant"), new("owner"));
        var subscription = await Arm(repository, principal, "task", SubscriptionState.Degraded);
        var ingestion = new NativeResultIngestion(repository, new TaskEventNotifier(), TimeProvider.System);

        await ingestion.IngestAsync(subscription.NativeSubscriptionId, Diff("item-1", 4), CancellationToken.None);

        var events = await repository.ReadEventsAsync(principal.Owner, new("task"), new(0), 10, CancellationToken.None);
        Assert.Contains(events, item => item.Kind == ConversationEventKind.Observation && item.Content.Contains("item-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_succeeds_when_the_reaction_is_already_gone()
    {
        var repository = new InMemorySentinelRepository();
        var engine = new RecordingEngine { DetachMissing = true };
        var service = Service(repository, engine);
        var principal = new Principal(new("tenant"), new("owner"));
        var created = await service.InvokeAsync(principal, new("task", "m1"), new SubscribeToQuery(new("query"), new("one"), new Unbounded()), CancellationToken.None);

        var cancelled = await service.InvokeAsync(principal, new("task", "m2"), new CancelTaskSubscription(created.Subscription!.Id), CancellationToken.None);

        Assert.Equal(SubscriptionState.Cancelled, cancelled.Subscription!.State);
    }

    [Fact]
    public async Task Identical_diff_is_journaled_once_and_a_different_diff_is_kept()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var repository = SqliteSentinelRepository.Create(Path.Combine(directory, "journal.sqlite"));
        await repository.InitializeAsync(CancellationToken.None);
        var principal = new Principal(new("tenant"), new("owner"));
        await repository.GetOrCreateTaskAsync(principal, new("task"), CancellationToken.None);
        var subscription = new ConversationSubscription(
            new(Guid.NewGuid()),
            new("task"),
            principal.Tenant,
            principal.Owner,
            new(new("query"), new("notify"), new("one")),
            SubscriptionState.Active,
            new("reaction-1"),
            DateTimeOffset.UnixEpoch,
            new Unbounded());
        await repository.GetOrCreateSubscriptionAsync(subscription, CancellationToken.None);
        var ingestion = new NativeResultIngestion(repository, new TaskEventNotifier(), TimeProvider.System);

        await ingestion.IngestAsync(subscription.NativeSubscriptionId, Diff("item-1", 7), CancellationToken.None);
        await ingestion.IngestAsync(subscription.NativeSubscriptionId, Diff("item-1", 7), CancellationToken.None);
        await ingestion.IngestAsync(subscription.NativeSubscriptionId, Diff("item-2", 8), CancellationToken.None);

        var events = await repository.ReadEventsAsync(principal.Owner, new("task"), new(0), 10, CancellationToken.None);
        Assert.Equal(2, events.Count);
        Assert.Contains("item-1", events[0].Content, StringComparison.Ordinal);
        Assert.Contains("item-2", events[1].Content, StringComparison.Ordinal);
        Assert.Contains("\"querySequence\":7", events[0].Content, StringComparison.Ordinal);
    }

    private static ConversationSubscriptions Service(IConversationRepository repository, INativeEngine engine) =>
        new(Catalog(), engine, repository, new TaskEventNotifier(), TimeProvider.System);

    private static async Task<ConversationSubscription> Arm(InMemorySentinelRepository repository, Principal principal, string taskId, SubscriptionState state)
    {
        await repository.GetOrCreateTaskAsync(principal, new(taskId), CancellationToken.None);
        var id = Guid.NewGuid();
        var subscription = new ConversationSubscription(
            new(id),
            new(taskId),
            principal.Tenant,
            principal.Owner,
            new(new("query"), new("notify"), new("one")),
            state,
            new($"reaction-{id:N}"),
            DateTimeOffset.UnixEpoch,
            new Unbounded());
        return await repository.GetOrCreateSubscriptionAsync(subscription, CancellationToken.None);
    }

    private static NativeQueryDiff Diff(string id, ulong sequence) => new(
        "Insert",
        new JsonObject { ["id"] = id },
        null,
        null,
        null,
        null,
        sequence,
        0);

    private static SafeCatalog Catalog() => new(
        new Dictionary<QueryId, QueryCatalogItem>
        {
            [new("query")] = new(new("query"), "Reports configured changes.", new HashSet<ReactionId> { new("notify") }, ["Show it."]),
        },
        new Dictionary<ReactionId, ReactionCatalogItem>
        {
            [new("notify")] = new(new("notify")),
        });

    private sealed class RecordingEngine : INativeEngine
    {
        public string? FailTask { get; init; }
        public bool FailRestores { get; set; }
        public bool DetachMissing { get; init; }
        public List<ConversationSubscription> Attached { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AttachAsync(ConversationSubscription subscription, CancellationToken cancellationToken)
        {
            if (FailRestores && FailTask == subscription.TaskId.Value)
                throw new InvalidOperationException("native unavailable");
            Attached.Add(subscription);
            return Task.CompletedTask;
        }

        public Task DetachAsync(NativeSubscriptionId subscriptionId, CancellationToken cancellationToken) =>
            DetachMissing
                ? Task.FromException(new InvalidOperationException("RemoveReaction failed: reaction not found: " + subscriptionId.Value))
                : Task.CompletedTask;

        public Task<IReadOnlyList<JsonNode>> GetQueryResultsAsync(QueryId queryId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<JsonNode>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
