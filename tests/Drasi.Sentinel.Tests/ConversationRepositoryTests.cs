using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Tests;

public sealed class ConversationRepositoryTests
{
    [Fact]
    public async Task Tasks_are_isolated_by_owner()
    {
        var repository = new InMemorySentinelRepository();
        await repository.GetOrCreateTaskAsync(Principal("one"), new("shared"), CancellationToken.None);
        await repository.GetOrCreateTaskAsync(Principal("two"), new("shared"), CancellationToken.None);
        await repository.AppendEventAsync(new("one"), Event("shared", "one"), CancellationToken.None);
        Assert.Empty(await repository.ReadEventsAsync(new("two"), new("shared"), new(0), 10, CancellationToken.None));
    }

    [Fact]
    public async Task Event_sequences_are_monotonic_per_task()
    {
        var repository = await RepositoryWithTaskAsync();
        var first = await repository.AppendEventAsync(new("owner"), Event("task", "one"), CancellationToken.None);
        var second = await repository.AppendEventAsync(new("owner"), Event("task", "two"), CancellationToken.None);
        Assert.Equal(1, first.Sequence.Value);
        Assert.Equal(2, second.Sequence.Value);
    }

    [Fact]
    public async Task Repeated_events_append_distinct_sequences()
    {
        var repository = await RepositoryWithTaskAsync();
        var first = await repository.AppendEventAsync(new("owner"), Event("task", "same"), CancellationToken.None);
        var replay = await repository.AppendEventAsync(new("owner"), Event("task", "same"), CancellationToken.None);
        Assert.Equal(1, first.Sequence.Value);
        Assert.Equal(2, replay.Sequence.Value);
    }

    [Fact]
    public async Task Read_events_honors_task_local_resume_cursor_and_limit()
    {
        var repository = await RepositoryWithTaskAsync();
        for (var index = 0; index != 3; index++)
            await repository.AppendEventAsync(new("owner"), Event("task", index.ToString()), CancellationToken.None);
        var result = await repository.ReadEventsAsync(new("owner"), new("task"), new(1), 1, CancellationToken.None);
        Assert.Single(result);
        Assert.Equal(2, result[0].Sequence.Value);
    }

    [Fact]
    public async Task Assistant_artifacts_append_for_each_request()
    {
        var repository = await RepositoryWithTaskAsync();
        var principal = Principal("owner");
        await repository.AppendAssistantArtifactAsync(principal, new("task"), "answer", CancellationToken.None);
        await repository.AppendAssistantArtifactAsync(principal, new("task"), "answer changed", CancellationToken.None);
        var events = await repository.ReadEventsAsync(principal.Owner, new("task"), new(0), 10, CancellationToken.None);
        Assert.Equal(2, events.Count);
        Assert.Equal("answer", events[0].Content);
        Assert.Equal("answer changed", events[1].Content);
    }

    [Fact]
    public async Task Subscription_request_idempotency_is_scoped_to_owner_task()
    {
        var repository = await RepositoryWithTaskAsync();
        var first = await repository.GetOrCreateSubscriptionAsync(Subscription("owner", "task", "request"), CancellationToken.None);
        var replay = await repository.GetOrCreateSubscriptionAsync(Subscription("owner", "task", "request"), CancellationToken.None);
        var otherTask = await repository.GetOrCreateSubscriptionAsync(Subscription("owner", "other", "request"), CancellationToken.None);
        Assert.Equal(first.Id, replay.Id);
        Assert.NotEqual(first.Id, otherTask.Id);
        var otherKey = await repository.GetOrCreateSubscriptionAsync(Subscription("owner", "task", "other-key"), CancellationToken.None);
        Assert.Equal(first.Id, otherKey.Id);
    }

    [Fact]
    public async Task Notifier_wakes_every_waiter()
    {
        var notifier = new TaskEventNotifier();
        var taskId = new ConversationTaskId("task");
        var observed = notifier.Observe(taskId);
        var first = notifier.WaitAsync(taskId, observed, CancellationToken.None);
        var second = notifier.WaitAsync(taskId, observed, CancellationToken.None);

        notifier.Notify(taskId);
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task Subscription_native_lookup_does_not_require_caller_controlled_ids()
    {
        var repository = await RepositoryWithTaskAsync();
        var subscription = await repository.GetOrCreateSubscriptionAsync(Subscription("owner", "task", "request"), CancellationToken.None);
        Assert.Equal(subscription, await repository.GetSubscriptionByNativeIdAsync(subscription.NativeSubscriptionId, CancellationToken.None));
    }

    [Fact]
    public async Task Subscription_reads_enforce_owner_and_task()
    {
        var repository = await RepositoryWithTaskAsync();
        var subscription = await repository.GetOrCreateSubscriptionAsync(Subscription("owner", "task", "request"), CancellationToken.None);
        Assert.Null(await repository.GetSubscriptionAsync(new("other"), new("task"), subscription.Id, CancellationToken.None));
        Assert.Null(await repository.GetSubscriptionAsync(new("owner"), new("other"), subscription.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_owner_task_cannot_read_a_journal()
    {
        var repository = await RepositoryWithTaskAsync();
        await Assert.ThrowsAsync<SentinelException>(() => repository.ReadEventsAsync(new("other"), new("task"), new(0), 10, CancellationToken.None));
    }

    [Fact]
    public async Task Saving_a_subscription_persists_its_lifecycle_state()
    {
        var repository = await RepositoryWithTaskAsync();
        var subscription = await repository.GetOrCreateSubscriptionAsync(Subscription("owner", "task", "request"), CancellationToken.None);
        await repository.SaveSubscriptionAsync(subscription with { State = SubscriptionState.Cancelled }, CancellationToken.None);
        var persisted = await repository.GetSubscriptionAsync(new("owner"), new("task"), subscription.Id, CancellationToken.None);
        Assert.Equal(SubscriptionState.Cancelled, persisted!.State);
    }

    private static async Task<InMemorySentinelRepository> RepositoryWithTaskAsync()
    {
        var repository = new InMemorySentinelRepository();
        await repository.GetOrCreateTaskAsync(Principal("owner"), new("task"), CancellationToken.None);
        return repository;
    }
    private static Principal Principal(string owner) => new(new("tenant"), new(owner));
    private static ConversationEvent Event(string task, string content) => new(new(task), default, ConversationEventKind.AssistantArtifact, null, content, DateTimeOffset.UtcNow);
    private static ConversationSubscription Subscription(string owner, string task, string key)
    {
        var id = new SubscriptionId(Guid.NewGuid());
        return new(id, new(task), new("tenant"), new(owner), new(new("query"), new("reaction"), new(key)), SubscriptionState.Active, new($"native-{id.Value:N}"), DateTimeOffset.UtcNow);
    }
}
