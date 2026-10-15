using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Drasi;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public abstract record GovernedToolCall;
public sealed record SubscribeToQuery(QueryId Query, SubscriptionRequestKey RequestKey, Lifetime Expires) : GovernedToolCall;
public sealed record ListTaskSubscriptions : GovernedToolCall;
public sealed record CancelTaskSubscription(SubscriptionId SubscriptionId) : GovernedToolCall;
public sealed record GetCurrentTaskResults : GovernedToolCall;
public sealed record ListOwnerTasks : GovernedToolCall;
public sealed record GetOwnerTaskResults(ConversationTaskId TaskId) : GovernedToolCall;
public sealed record CurrentTaskResultsArtifact(
    string Type,
    string TaskId,
    string SubscriptionId,
    string QueryId,
    IReadOnlyList<JsonNode> Rows,
    string? QueryDescription = null);
public sealed record ToolResult(
    string Kind,
    ConversationSubscription? Subscription = null,
    IReadOnlyList<ConversationSubscription>? Subscriptions = null,
    string? Message = null,
    CurrentTaskResultsArtifact? Data = null,
    IReadOnlyList<OwnerTaskSummary>? Tasks = null);

public interface IConversationSubscriptions
{
    Task<ToolResult> InvokeAsync(
        Principal principal,
        TaskTurnId turn,
        GovernedToolCall call,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ConversationEvent>> ReadEventsAsync(
        Principal principal,
        ConversationTaskId taskId,
        TaskEventSequence after,
        int limit,
        CancellationToken cancellationToken);

    Task RestoreWatchesAsync(CancellationToken cancellationToken);
}

public interface INativeEngine : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task AttachAsync(ConversationSubscription subscription, CancellationToken cancellationToken);
    Task DetachAsync(NativeSubscriptionId subscriptionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<JsonNode>> GetQueryResultsAsync(QueryId queryId, CancellationToken cancellationToken);
}

public sealed class FakeNativeEngine : INativeEngine
{
    private readonly ConcurrentDictionary<NativeSubscriptionId, ConversationSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<QueryId, IReadOnlyList<JsonNode>> _results = new();
    private bool _started;
    public Task StartAsync(CancellationToken cancellationToken) { _started = true; return Task.CompletedTask; }
    public Task AttachAsync(ConversationSubscription subscription, CancellationToken cancellationToken)
    {
        if (!_started) throw new SentinelException("engine_unavailable", "The native engine is not started.");
        _subscriptions[subscription.NativeSubscriptionId] = subscription;
        return Task.CompletedTask;
    }
    public Task DetachAsync(NativeSubscriptionId subscriptionId, CancellationToken cancellationToken)
    {
        _subscriptions.TryRemove(subscriptionId, out _);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<JsonNode>> GetQueryResultsAsync(QueryId queryId, CancellationToken cancellationToken)
    {
        IReadOnlyList<JsonNode> rows = _results.TryGetValue(queryId, out var result)
            ? result.Select(row => row.DeepClone()).ToArray()
            : [];
        return Task.FromResult(rows);
    }
    public void SetQueryResults(QueryId queryId, params JsonNode[] rows) =>
        _results[queryId] = rows.Select(row => row.DeepClone()).ToArray();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed record DrasiQueryDefinition(QueryId Id, string Cypher, IReadOnlyList<string> SourceIds);
public sealed record DrasiPluginSourceDefinition(string Id, string Kind, JsonObject Configuration, JsonObject? Bootstrap);
public sealed record DrasiSecretStore(string Kind, JsonObject Configuration);
public sealed record DrasiTopology(
    string EngineId,
    string PluginDirectory,
    IReadOnlyList<string> PluginReferences,
    DrasiSecretStore? SecretStore,
    IReadOnlyList<DrasiPluginSourceDefinition> Sources,
    IReadOnlyList<DrasiQueryDefinition> Queries,
    string? DataDirectory = null);

public sealed partial class DrasiNativeEngine(DrasiTopology topology, INativeResultIngestion? resultIngestion = null) : INativeEngine
{
    private readonly List<string> _feedQueryIds = [];
    private Engine? _engine;
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_engine is not null) return;
        _engine = await DrasiStores.OpenAsync(
            topology.PluginDirectory,
            topology.DataDirectory,
            SecretFilePath(topology),
            topology.EngineId,
            BindTopologyAsync,
            cancellationToken);
    }

    private async Task BindTopologyAsync(Engine engine, CancellationToken cancellationToken)
    {
        await InstallPluginsAsync(engine, topology.PluginDirectory, topology.PluginReferences, cancellationToken);
        if (topology.SecretStore is { } secretStore)
            await engine.UseSecretStoreAsync(secretStore.Kind, secretStore.Configuration, cancellationToken);
        await engine.StartAsync(cancellationToken);
        foreach (var source in topology.Sources)
            await engine.AddSourceAsync(source.Kind, source.Id, source.Configuration, autoStart: false, bootstrap: source.Bootstrap, cancellationToken: cancellationToken);
        // drasi-source-mysql 0.2.10 ignores autoStart and starts during add.
        // Stop it so feeds stay quiet until watches are restored.
        foreach (var source in topology.Sources)
            await StopIfRunningAsync(engine, source.Id, cancellationToken);
        foreach (var query in topology.Queries)
        {
            var nativeId = NativeQueryId(query.Id);
            await engine.AddQueryAsync(nativeId, query.Cypher, query.SourceIds, new QueryOptions { EnableBootstrap = true }, cancellationToken);
            _feedQueryIds.Add(nativeId);
        }
    }

    public async Task StartFeedsAsync(CancellationToken cancellationToken)
    {
        var engine = RequireEngine();
        foreach (var source in topology.Sources)
            await StartIfNeededAsync(engine, source.Id, cancellationToken);
        foreach (var queryId in _feedQueryIds)
            await engine.WaitForQueryAsync(queryId, null, cancellationToken);
    }

    private static async Task StopIfRunningAsync(Engine engine, string id, CancellationToken cancellationToken)
    {
        var status = await engine.GetSourceStatusAsync(id, cancellationToken);
        if (status is ComponentStatus.Running or ComponentStatus.Starting)
            await engine.StopSourceAsync(id, cancellationToken);
    }

    private static async Task StartIfNeededAsync(Engine engine, string id, CancellationToken cancellationToken)
    {
        var status = await engine.GetSourceStatusAsync(id, cancellationToken);
        if (status is ComponentStatus.Running or ComponentStatus.Starting)
            return;
        try
        {
            await engine.StartSourceAsync(id, cancellationToken);
        }
        catch (DrasiException exception) when (exception.Message.Contains("already running", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    public Task AttachAsync(ConversationSubscription subscription, CancellationToken cancellationToken) =>
        RequireEngine().AddDurableReactionAsync(
            subscription.NativeSubscriptionId.Value,
            [NativeQueryId(subscription.Request.QueryId)],
            result => IngestDurableAsync(subscription.NativeSubscriptionId, result),
            RecoveryPolicy.Strict,
            cancellationToken);

    public async Task DetachAsync(NativeSubscriptionId subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            await RequireEngine().RemoveReactionAsync(subscriptionId.Value, true, cancellationToken);
        }
        catch (DrasiException exception) when (IsAbsentReaction(exception))
        {
        }
    }

    internal static bool IsAbsentReaction(Exception exception) =>
        exception.Message.Contains("reaction not found", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<JsonNode>> GetQueryResultsAsync(QueryId queryId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await RequireEngine().GetQueryResultsAsync(NativeQueryId(queryId));
        return rows.Select(row => row.DeepClone()).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        var engine = _engine;
        if (engine is null) return;
        _engine = null;
        await DrasiStores.ReleaseAsync(engine);
    }

    private static string? SecretFilePath(DrasiTopology topology)
    {
        if (topology.SecretStore?.Configuration["path"] is JsonValue value &&
            value.GetValueKind() == JsonValueKind.String)
            return value.GetValue<string>();
        return null;
    }

    private static async Task InstallPluginsAsync(
        Engine engine,
        string directory,
        IReadOnlyList<string> references,
        CancellationToken cancellationToken)
    {
        if (references.Count == 0)
            throw new SentinelException("plugins_required", "The manifest does not declare any plugins.");
        foreach (var reference in references)
            await engine.InstallPluginAsync(reference, directory, cancellationToken: cancellationToken);
    }

    private Engine RequireEngine() => _engine ?? throw new SentinelException("engine_unavailable", "The Drasi engine is not started.");

    private Task IngestDurableAsync(NativeSubscriptionId id, QueryResultEvent result)
    {
        if (resultIngestion is null)
            throw new InvalidOperationException("A durable reaction requires result ingestion.");
        return resultIngestion.IngestResultAsync(id, result, CancellationToken.None);
    }
}

public sealed class ConversationSubscriptions(
    SafeCatalog catalog,
    INativeEngine engine,
    IConversationRepository repository,
    ITaskEventNotifier notifier,
    TimeProvider timeProvider,
    IAuthoredQueryStore? authored = null) : IConversationSubscriptions
{
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _activation = new();

    public async Task<ToolResult> InvokeAsync(Principal principal, TaskTurnId turn, GovernedToolCall call, CancellationToken cancellationToken) =>
        call switch
        {
            SubscribeToQuery subscribe => await SubscribeQueryAsync(principal, turn, subscribe, cancellationToken),
            ListTaskSubscriptions => new("subscriptions", Subscriptions: await repository.ListSubscriptionsAsync(principal.Owner, new(turn.TaskId), cancellationToken)),
            ListOwnerTasks => new("tasks", Tasks: await repository.ListOwnerTasksAsync(principal.Owner, cancellationToken)),
            CancelTaskSubscription cancel => await CancelAsync(principal, turn, cancel.SubscriptionId, cancellationToken),
            GetCurrentTaskResults => await GetCurrentResultsAsync(principal, new(turn.TaskId), cancellationToken),
            GetOwnerTaskResults results => await GetCurrentResultsAsync(principal, results.TaskId, cancellationToken),
            _ => throw new SentinelException("unsupported_tool", "The requested tool is not available."),
        };

    public Task<IReadOnlyList<ConversationEvent>> ReadEventsAsync(
        Principal principal, ConversationTaskId taskId, TaskEventSequence after, int limit, CancellationToken cancellationToken) =>
        repository.ReadEventsAsync(principal.Owner, taskId, after, limit, cancellationToken);

    private Task<ToolResult> SubscribeQueryAsync(Principal principal, TaskTurnId turn, SubscribeToQuery subscribe, CancellationToken cancellationToken)
    {
        var bound = CatalogBinding.Require(catalog, subscribe.Query);
        return SubscribeAsync(principal, turn, new(bound.Query, bound.Reaction, subscribe.RequestKey), subscribe.Expires, cancellationToken);
    }

    private async Task<ToolResult> SubscribeAsync(Principal principal, TaskTurnId turn, SubscriptionRequest request, Lifetime expires, CancellationToken cancellationToken)
    {
        var gate = _activation.GetOrAdd($"{principal.Owner.Value}\n{turn.TaskId}", static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await SubscribeClaimedAsync(principal, turn, request, expires, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ToolResult> SubscribeClaimedAsync(Principal principal, TaskTurnId turn, SubscriptionRequest request, Lifetime expires, CancellationToken cancellationToken)
    {
        if (authored is not null && await authored.FindAsync(request.QueryId, cancellationToken) is { } query)
            TtlToken.EnsureSubscriptionFits(query.Expires, expires);
        var taskId = new ConversationTaskId(turn.TaskId);
        await repository.GetOrCreateTaskAsync(principal, taskId, cancellationToken);
        var id = new SubscriptionId(Guid.NewGuid());
        var candidate = new ConversationSubscription(
            id, taskId, principal.Tenant, principal.Owner, request, SubscriptionState.Arming,
            new NativeSubscriptionId($"reaction-{id.Value:N}"), timeProvider.GetUtcNow(), expires);
        var subscription = await repository.GetOrCreateSubscriptionAsync(candidate, cancellationToken);
        subscription = await repository.GetSubscriptionAsync(principal.Owner, taskId, subscription.Id, cancellationToken) ?? subscription;
        if (subscription.State is SubscriptionState.Cancelled or SubscriptionState.Revoked or SubscriptionState.Expired
            || subscription.Expires.IsDue(timeProvider.GetUtcNow()))
            return new("watch_ended", subscription, Message: "The watch on this task has ended.");
        if (subscription.State is not (SubscriptionState.Arming or SubscriptionState.Degraded))
        {
            if (subscription.Request.RequestKey != request.RequestKey)
                throw new SentinelException(
                    "task_already_has_subscription",
                    "An A2A task can own only one active change subscription.");
            return new("subscription_created", subscription);
        }
        var retrying = subscription.State == SubscriptionState.Degraded;
        try
        {
            if (subscription.State != SubscriptionState.Active)
            {
                subscription = subscription with { State = SubscriptionState.Active };
                await repository.SaveSubscriptionAsync(subscription, cancellationToken);
            }
            await engine.AttachAsync(subscription, cancellationToken);
            await AppendAsync(
                principal.Owner,
                taskId,
                retrying ? ConversationEventKind.SubscriptionChanged : ConversationEventKind.SubscriptionCreated,
                subscription.Id,
                retrying
                    ? $"Subscription {subscription.Id.Value} is active again."
                    : $"Subscription {subscription.Id.Value} is active.",
                cancellationToken);
        }
        catch
        {
            subscription = subscription with { State = SubscriptionState.Degraded };
            await repository.SaveSubscriptionAsync(subscription, CancellationToken.None);
            throw;
        }
        return new("subscription_created", subscription);
    }

    private async Task<ToolResult> CancelAsync(Principal principal, TaskTurnId turn, SubscriptionId id, CancellationToken cancellationToken)
    {
        var subscription = await repository.GetSubscriptionAsync(principal.Owner, new(turn.TaskId), id, cancellationToken)
            ?? await repository.FindSubscriptionAsync(principal.Owner, id, cancellationToken)
            ?? throw new SentinelException("subscription_not_found", "The subscription does not exist.");
        var taskId = subscription.TaskId;
        if (subscription.State is not (SubscriptionState.Cancelled or SubscriptionState.Revoked or SubscriptionState.Expired))
        {
            try
            {
                await engine.DetachAsync(subscription.NativeSubscriptionId, cancellationToken);
            }
            catch (Exception exception) when (DrasiNativeEngine.IsAbsentReaction(exception))
            {
            }
            subscription = subscription with { State = SubscriptionState.Cancelled };
            await repository.SaveSubscriptionAsync(subscription, cancellationToken);
            await AppendAsync(principal.Owner, taskId, ConversationEventKind.SubscriptionChanged, subscription.Id,
                $"Subscription {subscription.Id.Value} was cancelled.", cancellationToken);
            await AppendAsync(principal.Owner, taskId, ConversationEventKind.TaskCompleted, null,
                "The task subscription was cancelled.", cancellationToken);
        }
        return new("subscription_cancelled", subscription);
    }

    public async Task RestoreWatchesAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in await repository.ListRestorableSubscriptionsAsync(cancellationToken))
        {
            var ready = subscription.State == SubscriptionState.Active
                ? subscription
                : subscription with { State = SubscriptionState.Active };
            if (ready != subscription)
                await repository.SaveSubscriptionAsync(ready, cancellationToken);
            try
            {
                await engine.AttachAsync(ready, cancellationToken);
            }
            catch (Exception exception)
            {
                var failed = ready with { State = SubscriptionState.Degraded };
                await repository.SaveSubscriptionAsync(failed, CancellationToken.None);
                try
                {
                    await AppendAsync(
                        failed.Owner,
                        failed.TaskId,
                        ConversationEventKind.SubscriptionChanged,
                        failed.Id,
                        $"Subscription {failed.Id.Value} could not be reattached: {exception.Message}",
                        CancellationToken.None);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private async Task<ToolResult> GetCurrentResultsAsync(Principal principal, ConversationTaskId taskId, CancellationToken cancellationToken)
    {
        var subscription = (await repository.ListSubscriptionsAsync(principal.Owner, taskId, cancellationToken)).SingleOrDefault()
            ?? throw new SentinelException("subscription_not_found", "The task does not have a subscription.");
        if (subscription.State is SubscriptionState.Cancelled or SubscriptionState.Revoked or SubscriptionState.Expired
            || subscription.Expires.IsDue(timeProvider.GetUtcNow()))
            return new("watch_ended", subscription, Message: "The watch on this task has ended.");
        var rows = await engine.GetQueryResultsAsync(subscription.Request.QueryId, cancellationToken);
        catalog.Queries.TryGetValue(subscription.Request.QueryId, out var query);
        var artifact = new CurrentTaskResultsArtifact(
            "current_query_results",
            taskId.Value,
            subscription.Id.Value.ToString(),
            subscription.Request.QueryId.Value,
            rows,
            query?.Description);
        await AppendAsync(
            principal.Owner,
            taskId,
            ConversationEventKind.Observation,
            subscription.Id,
            JsonSerializer.Serialize(artifact, ArtifactJsonOptions),
            cancellationToken);
        return new("current_results", subscription, Data: artifact);
    }

    private async Task AppendAsync(OwnerId owner, ConversationTaskId taskId, ConversationEventKind kind, SubscriptionId? subscriptionId, string content, CancellationToken cancellationToken)
    {
        var persisted = await repository.AppendEventAsync(owner, new(taskId, default, kind, subscriptionId, content, timeProvider.GetUtcNow()), cancellationToken);
        notifier.Notify(persisted.TaskId);
    }

}
