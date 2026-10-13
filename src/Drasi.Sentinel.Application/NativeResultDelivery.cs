using System.Threading.Channels;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Drasi;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public interface IArtifactWebhook
{
    Task DeliverAsync(string artifactJson, CancellationToken cancellationToken);
}

public interface INativeResultIngestion
{
    void Enqueue(NativeSubscriptionId subscriptionId, NativeQueryDiff diff);
    Task IngestAsync(NativeSubscriptionId subscriptionId, NativeQueryDiff diff, CancellationToken cancellationToken);
    Task IngestResultAsync(NativeSubscriptionId subscriptionId, QueryResultEvent result, CancellationToken cancellationToken);
    Task RunAsync(CancellationToken cancellationToken);
}

public sealed class NativeResultIngestion(
    IConversationRepository repository,
    ITaskEventNotifier notifier,
    TimeProvider timeProvider,
    IArtifactWebhook? webhook = null,
    SafeCatalog? catalog = null) : INativeResultIngestion
{
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly Channel<NativeDelivery> _deliveries = Channel.CreateUnbounded<NativeDelivery>(
        new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(NativeSubscriptionId subscriptionId, NativeQueryDiff diff) =>
        _deliveries.Writer.TryWrite(new NativeDelivery(subscriptionId, diff));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var delivery in _deliveries.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await IngestAsync(delivery.SubscriptionId, delivery.Diff, CancellationToken.None);
                }
                catch (Exception)
                {
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task IngestResultAsync(NativeSubscriptionId subscriptionId, QueryResultEvent result, CancellationToken cancellationToken)
    {
        var ordinal = 0;
        foreach (var diff in result.Results)
        {
            await IngestAsync(subscriptionId, ToDiff(diff, result, ordinal), cancellationToken);
            ordinal++;
        }
    }

    public async Task IngestAsync(NativeSubscriptionId subscriptionId, NativeQueryDiff diff, CancellationToken cancellationToken)
    {
        var subscription = await repository.GetSubscriptionByNativeIdAsync(subscriptionId, cancellationToken);
        if (subscription is null)
            throw new InvalidOperationException($"No watch is registered for reaction {subscriptionId.Value}.");
        if (subscription.State is SubscriptionState.Cancelled or SubscriptionState.Revoked or SubscriptionState.Expired
            || subscription.Expires.IsDue(timeProvider.GetUtcNow()))
            return;
        if (subscription.State is not (SubscriptionState.Arming or SubscriptionState.Active or SubscriptionState.Degraded))
            throw new InvalidOperationException($"Watch {subscription.Id.Value} cannot accept a diff while {subscription.State}.");

        var occurredAt = timeProvider.GetUtcNow();
        string? queryDescription = null;
        if (catalog?.Queries.TryGetValue(subscription.Request.QueryId, out var query) == true)
            queryDescription = query.Description;
        var persisted = await repository.AppendDiffOnceAsync(
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
                    queryDescription,
                    reactionId = subscription.Request.ReactionId.Value,
                    occurredAt,
                    querySequence = diff.QuerySequence,
                    diffOrdinal = diff.DiffOrdinal,
                    diffType = diff.Type,
                    data = diff.Data,
                    before = diff.Before,
                    after = diff.After,
                    groupingKeys = diff.GroupingKeys,
                    metadata = diff.Metadata,
                }, ArtifactJsonOptions),
                occurredAt),
            SequenceReceipt(diff),
            cancellationToken);
        if (persisted is null)
            return;
        notifier.Notify(persisted.TaskId);
        if (webhook is null)
            return;
        try
        {
            await webhook.DeliverAsync(persisted.Content, CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    private static string? SequenceReceipt(NativeQueryDiff diff) =>
        diff.QuerySequence is { } sequence ? $"{sequence}:{diff.DiffOrdinal}" : null;

    private static NativeQueryDiff ToDiff(QueryDiff diff, QueryResultEvent result, int ordinal) => new(
        diff.Type.ToString(),
        diff.Data,
        diff.Before,
        diff.After,
        diff.GroupingKeys is null
            ? null
            : new JsonArray(diff.GroupingKeys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()),
        result.Metadata,
        result.Sequence,
        ordinal);

    private sealed record NativeDelivery(NativeSubscriptionId SubscriptionId, NativeQueryDiff Diff);
}
