using System.Collections.Concurrent;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public interface ISentinelRepository : IConversationRepository, IAuthoredQueryStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

public interface IConversationRepository
{
    Task<ConversationTask> GetOrCreateTaskAsync(Principal principal, ConversationTaskId taskId, CancellationToken cancellationToken);
    Task<ConversationSubscription> GetOrCreateSubscriptionAsync(ConversationSubscription subscription, CancellationToken cancellationToken);
    Task<ConversationSubscription?> GetSubscriptionAsync(OwnerId owner, ConversationTaskId taskId, SubscriptionId subscriptionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationSubscription>> ListSubscriptionsAsync(OwnerId owner, ConversationTaskId taskId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationSubscription>> ListRestorableSubscriptionsAsync(CancellationToken cancellationToken);
    Task<ConversationEvent?> AppendDiffOnceAsync(OwnerId owner, ConversationEvent input, string? receipt, CancellationToken cancellationToken);
    Task<ConversationSubscription?> GetSubscriptionByNativeIdAsync(NativeSubscriptionId nativeId, CancellationToken cancellationToken);
    Task SaveSubscriptionAsync(ConversationSubscription subscription, CancellationToken cancellationToken);
    Task<ConversationEvent> AppendEventAsync(OwnerId owner, ConversationEvent eventToAppend, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationEvent>> ReadEventsAsync(OwnerId owner, ConversationTaskId taskId, TaskEventSequence after, int limit, CancellationToken cancellationToken);
    Task<TaskEventSequence> ReadLatestEventSequenceAsync(OwnerId owner, ConversationTaskId taskId, CancellationToken cancellationToken);
    Task AppendAssistantArtifactAsync(Principal principal, ConversationTaskId taskId, string content, CancellationToken cancellationToken);
    Task<bool> TaskExistsAsync(OwnerId owner, ConversationTaskId taskId, CancellationToken cancellationToken);
    Task SetPushConfigAsync(OwnerId owner, ConversationTaskId taskId, PushNotificationConfig config, CancellationToken cancellationToken);
    Task<PushNotificationConfig?> GetPushConfigAsync(OwnerId owner, ConversationTaskId taskId, CancellationToken cancellationToken);
    Task DeletePushConfigAsync(OwnerId owner, ConversationTaskId taskId, CancellationToken cancellationToken);
}

public interface ITaskEventNotifier
{
    long Observe(ConversationTaskId taskId);
    void Notify(ConversationTaskId taskId);
    Task WaitAsync(ConversationTaskId taskId, long observedGeneration, CancellationToken cancellationToken);
}

public sealed class TaskEventNotifier : ITaskEventNotifier
{
    private readonly ConcurrentDictionary<ConversationTaskId, Slot> _slots = new();

    public long Observe(ConversationTaskId taskId)
    {
        var slot = SlotFor(taskId);
        return Volatile.Read(ref slot.Generation);
    }

    public void Notify(ConversationTaskId taskId)
    {
        var slot = SlotFor(taskId);
        List<TaskCompletionSource> waiters;
        lock (slot)
        {
            slot.Generation++;
            waiters = slot.Waiters;
            slot.Waiters = [];
        }

        foreach (var waiter in waiters)
            waiter.TrySetResult();
    }

    public async Task WaitAsync(ConversationTaskId taskId, long observedGeneration, CancellationToken cancellationToken)
    {
        var slot = SlotFor(taskId);
        while (Volatile.Read(ref slot.Generation) == observedGeneration)
        {
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (slot)
            {
                if (slot.Generation != observedGeneration)
                    return;
                slot.Waiters.Add(waiter);
            }

            using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            try
            {
                await waiter.Task;
            }
            catch (OperationCanceledException)
            {
                lock (slot)
                    slot.Waiters.Remove(waiter);
                throw;
            }
        }
    }

    private Slot SlotFor(ConversationTaskId taskId) => _slots.GetOrAdd(taskId, static _ => new Slot());

    private sealed class Slot
    {
        public long Generation;
        public List<TaskCompletionSource> Waiters { get; set; } = [];
    }
}

public sealed class InMemorySentinelRepository : IConversationRepository
{
    private readonly ConcurrentDictionary<(OwnerId Owner, ConversationTaskId Task), ConversationTask> _tasks = new();
    private readonly ConcurrentDictionary<SubscriptionId, ConversationSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<(OwnerId, ConversationTaskId, SubscriptionRequestKey), SubscriptionId> _requests = new();
    private readonly ConcurrentDictionary<(OwnerId Owner, ConversationTaskId Task), List<ConversationEvent>> _events = new();
    private readonly ConcurrentDictionary<ConversationTaskId, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<(OwnerId Owner, ConversationTaskId Task), PushNotificationConfig> _pushConfigs = new();
    private readonly HashSet<(SubscriptionId Subscription, string Receipt)> _diffReceipts = [];

    private static bool IsRestorable(ConversationSubscription subscription) =>
        subscription.State is SubscriptionState.Arming or SubscriptionState.Active or SubscriptionState.Degraded;

    public Task<ConversationTask> GetOrCreateTaskAsync(Principal principal, ConversationTaskId taskId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(_tasks.GetOrAdd((principal.Owner, taskId), _ => new(taskId, principal.Tenant, principal.Owner, ConversationTaskState.Working, now, now)));
    }

    public async Task<ConversationSubscription> GetOrCreateSubscriptionAsync(ConversationSubscription subscription, CancellationToken cancellationToken)
    {
        await Gate(subscription.TaskId).WaitAsync(cancellationToken);
        try
        {
            var existing = _subscriptions.Values.FirstOrDefault(item =>
                item.Owner == subscription.Owner && item.TaskId == subscription.TaskId);
            if (existing is not null)
                return existing;
            _subscriptions[subscription.Id] = subscription;
            _requests[(subscription.Owner, subscription.TaskId, subscription.Request.RequestKey)] = subscription.Id;
            return subscription;
        }
        finally { Gate(subscription.TaskId).Release(); }
    }

    public Task<ConversationSubscription?> GetSubscriptionAsync(OwnerId owner, ConversationTaskId taskId, SubscriptionId subscriptionId, CancellationToken cancellationToken) =>
        Task.FromResult(_subscriptions.TryGetValue(subscriptionId, out var subscription) && subscription.Owner == owner && subscription.TaskId == taskId ? subscription : null);

    public Task<IReadOnlyList<ConversationSubscription>> ListSubscriptionsAsync(OwnerId owner, ConversationTaskId taskId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ConversationSubscription>>(_subscriptions.Values.Where(x => x.Owner == owner && x.TaskId == taskId).OrderBy(x => x.CreatedAt).ToArray());

    public Task<IReadOnlyList<ConversationSubscription>> ListRestorableSubscriptionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ConversationSubscription>>(_subscriptions.Values.Where(IsRestorable).OrderBy(x => x.CreatedAt).ToArray());

    public async Task<ConversationEvent?> AppendDiffOnceAsync(OwnerId owner, ConversationEvent input, string? receipt, CancellationToken cancellationToken)
    {
        if (input.SubscriptionId is not { } subscriptionId)
            throw new InvalidOperationException("A query diff requires a subscription.");
        await Gate(input.TaskId).WaitAsync(cancellationToken);
        try
        {
            if (receipt is not null && !_diffReceipts.Add((subscriptionId, receipt)))
                return null;
            var events = _events.GetOrAdd((owner, input.TaskId), static _ => []);
            lock (events)
            {
                var result = input with { Sequence = new(events.Count + 1) };
                events.Add(result);
                return result;
            }
        }
        finally { Gate(input.TaskId).Release(); }
    }

    public Task<ConversationSubscription?> GetSubscriptionByNativeIdAsync(NativeSubscriptionId nativeId, CancellationToken cancellationToken) =>
        Task.FromResult(_subscriptions.Values.SingleOrDefault(x => x.NativeSubscriptionId == nativeId));

    public Task SaveSubscriptionAsync(ConversationSubscription subscription, CancellationToken cancellationToken)
    {
        _subscriptions[subscription.Id] = subscription;
        return Task.CompletedTask;
    }

    public async Task<ConversationEvent> AppendEventAsync(OwnerId owner, ConversationEvent input, CancellationToken cancellationToken)
    {
        await Gate(input.TaskId).WaitAsync(cancellationToken);
        try
        {
            var events = _events.GetOrAdd((owner, input.TaskId), static _ => []);
            lock (events)
            {
                var result = input with { Sequence = new(events.Count + 1) };
                events.Add(result);
                return result;
            }
        }
        finally { Gate(input.TaskId).Release(); }
    }

    public Task<IReadOnlyList<ConversationEvent>> ReadEventsAsync(OwnerId owner, ConversationTaskId taskId, TaskEventSequence after, int limit, CancellationToken cancellationToken)
    {
        if (!_tasks.ContainsKey((owner, taskId))) throw new SentinelException("task_not_found", "The task does not exist.");
        var events = _events.TryGetValue((owner, taskId), out var journal) ? journal : [];
        lock (events)
            return Task.FromResult<IReadOnlyList<ConversationEvent>>(events.Where(x => x.Sequence.Value > after.Value).Take(limit).ToArray());
    }

    public Task<TaskEventSequence> ReadLatestEventSequenceAsync(OwnerId owner, ConversationTaskId taskId, CancellationToken cancellationToken)
    {
        if (!_tasks.ContainsKey((owner, taskId))) throw new SentinelException("task_not_found", "The task does not exist.");
        var events = _events.TryGetValue((owner, taskId), out var journal) ? journal : [];
        lock (events)
            return Task.FromResult(events.Count == 0 ? new TaskEventSequence(0) : events[^1].Sequence);
    }

    public async Task AppendAssistantArtifactAsync(Principal principal, ConversationTaskId taskId, string content, CancellationToken cancellationToken)
    {
        await GetOrCreateTaskAsync(principal, taskId, cancellationToken);
        await AppendEventAsync(principal.Owner, new(taskId, default, ConversationEventKind.AssistantArtifact, null, content, DateTimeOffset.UtcNow), cancellationToken);
    }

    public Task<bool> TaskExistsAsync(OwnerId owner, ConversationTaskId taskId, CancellationToken cancellationToken) =>
        Task.FromResult(_tasks.ContainsKey((owner, taskId)));

    public Task SetPushConfigAsync(OwnerId owner, ConversationTaskId taskId, PushNotificationConfig config, CancellationToken cancellationToken)
    {
        _pushConfigs[(owner, taskId)] = config;
        return Task.CompletedTask;
    }

    public Task<PushNotificationConfig?> GetPushConfigAsync(OwnerId owner, ConversationTaskId taskId, CancellationToken cancellationToken) =>
        Task.FromResult(_pushConfigs.TryGetValue((owner, taskId), out var config) ? config : null);

    public Task DeletePushConfigAsync(OwnerId owner, ConversationTaskId taskId, CancellationToken cancellationToken)
    {
        _pushConfigs.TryRemove((owner, taskId), out _);
        return Task.CompletedTask;
    }

    private SemaphoreSlim Gate(ConversationTaskId taskId) => _gates.GetOrAdd(taskId, static _ => new(1, 1));
}
