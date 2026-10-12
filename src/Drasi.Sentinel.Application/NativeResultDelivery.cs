using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    public async Task IngestAsync(NativeSubscriptionId subscriptionId, NativeQueryDiff diff, CancellationToken cancellationToken)
    {
        var subscription = await repository.GetSubscriptionByNativeIdAsync(subscriptionId, cancellationToken);
        if (subscription is null || subscription.State is not (SubscriptionState.Arming or SubscriptionState.Active))
            return;

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
            DiffReceipt(diff),
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

    private static string DiffReceipt(NativeQueryDiff diff)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            diff.Type,
            diff.Data,
            diff.Before,
            diff.After,
            diff.GroupingKeys,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record NativeDelivery(NativeSubscriptionId SubscriptionId, NativeQueryDiff Diff);
}
