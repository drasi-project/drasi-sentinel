using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public sealed class WatchExpiry(
    IConversationRepository repository,
    IAuthoredQueryStore store,
    INativeEngine engine,
    INativeQueryRuntime runtime,
    LiveCatalog catalog,
    ITaskEventNotifier notifier,
    TimeProvider time)
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        foreach (var subscription in await repository.ListRestorableSubscriptionsAsync(cancellationToken))
        {
            if (subscription.Expires.IsDue(now))
                await ExpireAsync(subscription, cancellationToken);
        }

        var live = await repository.ListRestorableSubscriptionsAsync(cancellationToken);
        foreach (var query in await store.ListAsync(cancellationToken))
        {
            if (!query.Expires.IsDue(now))
                continue;
            if (live.Any(item => item.Request.QueryId == query.Id))
                continue;
            await DropQueryAsync(query.Id, cancellationToken);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await SweepAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task ExpireAsync(ConversationSubscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            await engine.DetachAsync(subscription.NativeSubscriptionId, cancellationToken);
        }
        catch (Exception exception) when (DrasiNativeEngine.IsAbsentReaction(exception))
        {
        }
        catch (Exception)
        {
            return;
        }

        var expired = subscription with { State = SubscriptionState.Expired };
        await repository.SaveSubscriptionAsync(expired, cancellationToken);
        var changed = await repository.AppendEventAsync(
            expired.Owner,
            new(expired.TaskId, default, ConversationEventKind.SubscriptionChanged, expired.Id, $"Subscription {expired.Id.Value} expired.", time.GetUtcNow()),
            cancellationToken);
        await repository.AppendEventAsync(
            expired.Owner,
            new(expired.TaskId, default, ConversationEventKind.TaskCompleted, null, "The task subscription expired.", time.GetUtcNow()),
            cancellationToken);
        notifier.Notify(changed.TaskId);
    }

    private async Task DropQueryAsync(QueryId id, CancellationToken cancellationToken)
    {
        try
        {
            await runtime.RemoveAsync(id, cancellationToken);
        }
        catch (Exception exception) when (exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
        }
        catch (Exception)
        {
            return;
        }

        await store.DeleteAsync(id, cancellationToken);
        catalog.Remove(id);
    }
}
