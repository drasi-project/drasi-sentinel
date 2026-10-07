using System.Text.Json;
using System.Text.Json.Serialization;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public interface INativeResultIngestion
{
    Task IngestAsync(NativeSubscriptionId subscriptionId, NativeQueryDiff diff, CancellationToken cancellationToken);
}

public sealed class NativeResultIngestion(
    IConversationRepository repository,
    ITaskEventNotifier notifier,
    TimeProvider timeProvider) : INativeResultIngestion
{
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task IngestAsync(NativeSubscriptionId subscriptionId, NativeQueryDiff diff, CancellationToken cancellationToken)
    {
        var subscription = await repository.GetSubscriptionByNativeIdAsync(subscriptionId, cancellationToken);
        if (subscription is null || subscription.State != SubscriptionState.Active)
            return;

        var occurredAt = timeProvider.GetUtcNow();
        var persisted = await repository.AppendEventAsync(
            subscription.Owner,
            new ConversationEvent(
                subscription.TaskId,
                default,
                ConversationEventKind.Observation,
                subscription.Id,
                JsonSerializer.Serialize(new
                {
                    type = "native_query_diff",
                    subscriptionId = subscription.Id.Value,
                    queryId = subscription.Request.QueryId.Value,
                    reactionId = subscription.Request.ReactionId.Value,
                    occurredAt,
                    diffType = diff.Type,
                    data = diff.Data,
                    before = diff.Before,
                    after = diff.After,
                    groupingKeys = diff.GroupingKeys,
                    metadata = diff.Metadata,
                }, ArtifactJsonOptions),
                occurredAt),
            cancellationToken);
        notifier.Notify(persisted.TaskId);
    }
}
