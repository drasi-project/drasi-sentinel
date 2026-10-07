using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Drasi;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public abstract record GovernedToolCall;
public sealed record SubscribeToChanges(SubscriptionRequest Request) : GovernedToolCall;
public sealed record ListTaskSubscriptions : GovernedToolCall;
public sealed record CancelTaskSubscription(SubscriptionId SubscriptionId) : GovernedToolCall;
public sealed record GetCurrentTaskResults : GovernedToolCall;
public sealed record CurrentTaskResultsArtifact(
    string Type,
    string TaskId,
    string SubscriptionId,
    string QueryId,
    IReadOnlyList<JsonNode> Rows);
public sealed record ToolResult(
    string Kind,
    ConversationSubscription? Subscription = null,
    IReadOnlyList<ConversationSubscription>? Subscriptions = null,
    string? Message = null,
    CurrentTaskResultsArtifact? Data = null);

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
public sealed record DrasiTopology(
    string EngineId,
    string PluginLockDirectory,
    IReadOnlyList<DrasiPluginSourceDefinition> Sources,
    IReadOnlyList<DrasiQueryDefinition> Queries);

public sealed class DrasiNativeEngine(DrasiTopology topology, INativeResultIngestion? resultIngestion = null) : INativeEngine
{
    private Engine? _engine;
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_engine is not null) return;
        var engine = await Engine.CreateAsync(topology.EngineId, cancellationToken: cancellationToken);
        await engine.InstallFromLockfileAsync(topology.PluginLockDirectory, cancellationToken: cancellationToken);
        await engine.StartAsync(cancellationToken);
        foreach (var source in topology.Sources)
            await engine.AddSourceAsync(source.Kind, source.Id, source.Configuration, bootstrap: source.Bootstrap, cancellationToken: cancellationToken);
        foreach (var query in topology.Queries)
        {
            await engine.AddQueryAsync($"query-{query.Id.Value}", query.Cypher, query.SourceIds, new QueryOptions { EnableBootstrap = true }, cancellationToken);
            await engine.WaitForQueryAsync($"query-{query.Id.Value}", null, cancellationToken);
        }
        _engine = engine;
    }

    public Task AttachAsync(ConversationSubscription subscription, CancellationToken cancellationToken) =>
        RequireEngine().AddReactionAsync(
            subscription.NativeSubscriptionId.Value,
            [$"query-{subscription.Request.QueryId.Value}"],
            result => IngestResult(subscription.NativeSubscriptionId, result),
            true,
            cancellationToken);

    public Task DetachAsync(NativeSubscriptionId subscriptionId, CancellationToken cancellationToken) =>
        RequireEngine().RemoveReactionAsync(subscriptionId.Value, true, cancellationToken);

    public async Task<IReadOnlyList<JsonNode>> GetQueryResultsAsync(QueryId queryId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await RequireEngine().GetQueryResultsAsync($"query-{queryId.Value}");
        return rows.Select(row => row.DeepClone()).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null) await _engine.DisposeAsync();
        _engine = null;
    }

    private Engine RequireEngine() => _engine ?? throw new SentinelException("engine_unavailable", "The Drasi engine is not started.");
    private void IngestResult(NativeSubscriptionId id, QueryResultEvent result)
    {
        if (resultIngestion is null) return;
        foreach (var diff in result.Results)
            resultIngestion.IngestAsync(
                id,
                new NativeQueryDiff(
                    diff.Type.ToString(),
                    diff.Data,
                    diff.Before,
                    diff.After,
                    diff.GroupingKeys is null
                        ? null
                        : new JsonArray(diff.GroupingKeys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()),
                    result.Metadata),
                CancellationToken.None).GetAwaiter().GetResult();
    }
}

public sealed class ConversationSubscriptions(
    SafeCatalog catalog,
    INativeEngine engine,
    IConversationRepository repository,
    ITaskEventNotifier notifier,
    TimeProvider timeProvider) : IConversationSubscriptions
{
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ToolResult> InvokeAsync(Principal principal, TaskTurnId turn, GovernedToolCall call, CancellationToken cancellationToken) =>
        call switch
        {
            SubscribeToChanges subscribe => await SubscribeAsync(principal, turn, subscribe.Request, cancellationToken),
            ListTaskSubscriptions => new("subscriptions", Subscriptions: await repository.ListSubscriptionsAsync(principal.Owner, new(turn.TaskId), cancellationToken)),
            CancelTaskSubscription cancel => await CancelAsync(principal, turn, cancel.SubscriptionId, cancellationToken),
            GetCurrentTaskResults => await GetCurrentResultsAsync(principal, turn, cancellationToken),
            _ => throw new SentinelException("unsupported_tool", "The requested tool is not available."),
        };

    public Task<IReadOnlyList<ConversationEvent>> ReadEventsAsync(
        Principal principal, ConversationTaskId taskId, TaskEventSequence after, int limit, CancellationToken cancellationToken) =>
        repository.ReadEventsAsync(principal.Owner, taskId, after, limit, cancellationToken);

    private async Task<ToolResult> SubscribeAsync(Principal principal, TaskTurnId turn, SubscriptionRequest request, CancellationToken cancellationToken)
    {
        Validate(principal, request);
        var taskId = new ConversationTaskId(turn.TaskId);
        await repository.GetOrCreateTaskAsync(principal, taskId, cancellationToken);
        var existing = await repository.ListSubscriptionsAsync(principal.Owner, taskId, cancellationToken);
        if (existing.Count > 0)
        {
            var existingSubscription = existing.Single();
            if (existingSubscription.Request.RequestKey == request.RequestKey)
                return new("subscription_created", existingSubscription);
            throw new SentinelException(
                "task_already_has_subscription",
                "An A2A task can own only one active change subscription.");
        }
        var id = new SubscriptionId(Guid.NewGuid());
        var candidate = new ConversationSubscription(
            id, taskId, principal.Tenant, principal.Owner, request, SubscriptionState.Arming,
            new NativeSubscriptionId($"reaction-{id.Value:N}"), timeProvider.GetUtcNow());
        var subscription = await repository.GetOrCreateSubscriptionAsync(candidate, cancellationToken);
        if (subscription.State == SubscriptionState.Arming)
        {
            try
            {
                await engine.AttachAsync(subscription, cancellationToken);
                subscription = subscription with { State = SubscriptionState.Active };
                await repository.SaveSubscriptionAsync(subscription, cancellationToken);
                await AppendAsync(principal.Owner, taskId, ConversationEventKind.SubscriptionCreated, subscription.Id,
                    $"Subscription {subscription.Id.Value} is active.", cancellationToken);
            }
            catch
            {
                subscription = subscription with { State = SubscriptionState.Degraded };
                await repository.SaveSubscriptionAsync(subscription, CancellationToken.None);
                await AppendAsync(principal.Owner, taskId, ConversationEventKind.TaskFailed, subscription.Id,
                    $"Subscription {subscription.Id.Value} could not be activated.", CancellationToken.None);
                throw;
            }
        }
        return new("subscription_created", subscription);
    }

    private async Task<ToolResult> CancelAsync(Principal principal, TaskTurnId turn, SubscriptionId id, CancellationToken cancellationToken)
    {
        var taskId = new ConversationTaskId(turn.TaskId);
        var subscription = await repository.GetSubscriptionAsync(principal.Owner, taskId, id, cancellationToken)
            ?? throw new SentinelException("subscription_not_found", "The subscription does not exist.");
        if (subscription.State is not (SubscriptionState.Cancelled or SubscriptionState.Revoked or SubscriptionState.Expired))
        {
            await engine.DetachAsync(subscription.NativeSubscriptionId, cancellationToken);
            subscription = subscription with { State = SubscriptionState.Cancelled };
            await repository.SaveSubscriptionAsync(subscription, cancellationToken);
            await AppendAsync(principal.Owner, taskId, ConversationEventKind.SubscriptionChanged, subscription.Id,
                $"Subscription {subscription.Id.Value} was cancelled.", cancellationToken);
            await AppendAsync(principal.Owner, taskId, ConversationEventKind.TaskCompleted, null,
                "The task subscription was cancelled.", cancellationToken);
        }
        return new("subscription_cancelled", subscription);
    }

    private async Task<ToolResult> GetCurrentResultsAsync(Principal principal, TaskTurnId turn, CancellationToken cancellationToken)
    {
        var taskId = new ConversationTaskId(turn.TaskId);
        var subscription = (await repository.ListSubscriptionsAsync(principal.Owner, taskId, cancellationToken)).SingleOrDefault()
            ?? throw new SentinelException("subscription_not_found", "The task does not have a subscription.");
        var rows = await engine.GetQueryResultsAsync(subscription.Request.QueryId, cancellationToken);
        var artifact = new CurrentTaskResultsArtifact(
            "current_query_results",
            taskId.Value,
            subscription.Id.Value.ToString(),
            subscription.Request.QueryId.Value,
            rows);
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

    private void Validate(Principal principal, SubscriptionRequest request)
    {
        if (!catalog.Queries.TryGetValue(request.QueryId, out var query) ||
            !query.AllowedReactions.Contains(request.ReactionId) ||
            !catalog.Reactions.ContainsKey(request.ReactionId))
            throw new SentinelException("catalog_rejected", "The selected catalog assets cannot be combined.");
    }
}
