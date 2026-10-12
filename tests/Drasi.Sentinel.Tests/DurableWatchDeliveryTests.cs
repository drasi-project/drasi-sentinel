using System.Text.Json.Nodes;
using Drasi;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Tests;

public sealed class DurableWatchDeliveryTests
{
    [Fact]
    public async Task Degraded_watch_journals_a_live_diff_from_the_engine()
    {
        var root = Path.Combine(Path.GetTempPath(), "sentinel-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var repository = SqliteSentinelRepository.Create(Path.Combine(root, "journal.sqlite"));
        await repository.InitializeAsync(CancellationToken.None);
        var principal = new Principal(new("tenant"), new("owner"));
        var nativeId = new NativeSubscriptionId($"reaction-{Guid.NewGuid():N}");
        var subscription = new ConversationSubscription(
            new(Guid.NewGuid()),
            new("watch-task"),
            principal.Tenant,
            principal.Owner,
            new(new("paid-orders"), new("notify"), new("paid")),
            SubscriptionState.Degraded,
            nativeId,
            DateTimeOffset.UnixEpoch);
        await repository.GetOrCreateTaskAsync(principal, subscription.TaskId, CancellationToken.None);
        await repository.GetOrCreateSubscriptionAsync(subscription, CancellationToken.None);
        var ingestion = new NativeResultIngestion(repository, new TaskEventNotifier(), TimeProvider.System);
        Engine? engine = null;
        try
        {
            engine = await DrasiStores.OpenAsync(root, null, null, "delivery", StartOnly, CancellationToken.None);
            await engine.AddSourceAsync("orders");
            await engine.AddQueryAsync(
                "paid",
                "MATCH (o:Order) WHERE o.paid = true RETURN o.id AS id",
                ["orders"],
                new QueryOptions { EnableBootstrap = true });
            await engine.WaitForQueryAsync("paid");
            await engine.AddDurableReactionAsync(
                nativeId.Value,
                ["paid"],
                result => ingestion.IngestResultAsync(nativeId, result, CancellationToken.None),
                RecoveryPolicy.Strict);

            await engine.PushChangeAsync("orders", new SourceChange
            {
                Op = ChangeOp.Insert,
                Id = "order-1",
                Labels = ["Order"],
                Properties = new JsonObject { ["id"] = "order-1", ["paid"] = true },
            });

            var observed = await WaitForObservation(repository, principal.Owner, "order-1");
            Assert.Single(observed);
            Assert.Contains("\"querySequence\":", observed[0].Content, StringComparison.Ordinal);
        }
        finally
        {
            if (engine is not null)
                await DrasiStores.ReleaseAsync(engine);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task Reattached_reaction_journals_a_diff_the_first_delivery_did_not_acknowledge()
    {
        var root = Path.Combine(Path.GetTempPath(), "sentinel-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var repository = SqliteSentinelRepository.Create(Path.Combine(root, "journal.sqlite"));
        await repository.InitializeAsync(CancellationToken.None);
        var principal = new Principal(new("tenant"), new("owner"));
        var nativeId = new NativeSubscriptionId($"reaction-{Guid.NewGuid():N}");
        var subscription = new ConversationSubscription(
            new(Guid.NewGuid()),
            new("watch-task"),
            principal.Tenant,
            principal.Owner,
            new(new("paid-orders"), new("notify"), new("paid")),
            SubscriptionState.Active,
            nativeId,
            DateTimeOffset.UnixEpoch);
        await repository.GetOrCreateTaskAsync(principal, subscription.TaskId, CancellationToken.None);
        await repository.GetOrCreateSubscriptionAsync(subscription, CancellationToken.None);
        var ingestion = new NativeResultIngestion(repository, new TaskEventNotifier(), TimeProvider.System);
        var failFirst = true;
        Engine? engine = null;
        try
        {
            engine = await DrasiStores.OpenAsync(root, null, null, "replay", StartOnly, CancellationToken.None);
            await engine.AddSourceAsync("orders");
            await engine.AddQueryAsync(
                "paid",
                "MATCH (o:Order) WHERE o.paid = true RETURN o.id AS id",
                ["orders"],
                new QueryOptions { EnableBootstrap = true });
            await engine.WaitForQueryAsync("paid");
            await engine.AddDurableReactionAsync(
                nativeId.Value,
                ["paid"],
                result =>
                {
                    if (failFirst)
                    {
                        failFirst = false;
                        throw new InvalidOperationException("journal unavailable");
                    }
                    return ingestion.IngestResultAsync(nativeId, result, CancellationToken.None);
                },
                RecoveryPolicy.Strict);
            try
            {
                await engine.PushChangeAsync("orders", new SourceChange
                {
                    Op = ChangeOp.Insert,
                    Id = "order-9",
                    Labels = ["Order"],
                    Properties = new JsonObject { ["id"] = "order-9", ["paid"] = true },
                });
            }
            catch (Exception) when (!failFirst)
            {
            }

            await repository.SaveSubscriptionAsync(subscription with { State = SubscriptionState.Degraded }, CancellationToken.None);
            await engine.RemoveReactionAsync(nativeId.Value, cleanup: false);
            await engine.AddDurableReactionAsync(
                nativeId.Value,
                ["paid"],
                result => ingestion.IngestResultAsync(nativeId, result, CancellationToken.None),
                RecoveryPolicy.Strict);

            var observed = await WaitForObservation(repository, principal.Owner, "order-9");
            Assert.Single(observed);
        }
        finally
        {
            if (engine is not null)
                await DrasiStores.ReleaseAsync(engine);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static Task StartOnly(Engine engine, CancellationToken cancellationToken) =>
        engine.StartAsync(cancellationToken);

    private static async Task<IReadOnlyList<ConversationEvent>> WaitForObservation(
        SqliteSentinelRepository repository,
        OwnerId owner,
        string needle)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var events = await repository.ReadEventsAsync(owner, new("watch-task"), new(0), 20, CancellationToken.None);
            var observed = events.Where(item => item.Kind == ConversationEventKind.Observation && item.Content.Contains(needle, StringComparison.Ordinal)).ToArray();
            if (observed.Length > 0)
                return observed;
            await Task.Delay(25);
        }

        var missed = await repository.ReadEventsAsync(owner, new("watch-task"), new(0), 20, CancellationToken.None);
        throw new TimeoutException("Timed out waiting for " + needle + ". Events: " + string.Join(" | ", missed.Select(item => item.Content)));
    }
}
