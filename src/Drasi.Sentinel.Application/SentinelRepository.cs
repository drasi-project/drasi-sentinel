using System.Collections.Concurrent;
using Drasi.Sentinel.Domain;
using Npgsql;

namespace Drasi.Sentinel.Application;

public interface IConversationRepository
{
    Task<ConversationTask> GetOrCreateTaskAsync(Principal principal, ConversationTaskId taskId, CancellationToken cancellationToken);
    Task<ConversationSubscription> GetOrCreateSubscriptionAsync(ConversationSubscription subscription, CancellationToken cancellationToken);
    Task<ConversationSubscription?> GetSubscriptionAsync(OwnerId owner, ConversationTaskId taskId, SubscriptionId subscriptionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationSubscription>> ListSubscriptionsAsync(OwnerId owner, ConversationTaskId taskId, CancellationToken cancellationToken);
    Task<ConversationSubscription?> GetSubscriptionByNativeIdAsync(NativeSubscriptionId nativeId, CancellationToken cancellationToken);
    Task SaveSubscriptionAsync(ConversationSubscription subscription, CancellationToken cancellationToken);
    Task<ConversationEvent> AppendEventAsync(OwnerId owner, ConversationEvent eventToAppend, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationEvent>> ReadEventsAsync(OwnerId owner, ConversationTaskId taskId, TaskEventSequence after, int limit, CancellationToken cancellationToken);
    Task AppendAssistantArtifactAsync(Principal principal, ConversationTaskId taskId, string content, CancellationToken cancellationToken);
}

public interface ITaskEventNotifier
{
    void Notify(ConversationTaskId taskId);
    Task WaitAsync(ConversationTaskId taskId, CancellationToken cancellationToken);
}

public sealed class TaskEventNotifier : ITaskEventNotifier
{
    private readonly ConcurrentDictionary<ConversationTaskId, SemaphoreSlim> _signals = new();
    public void Notify(ConversationTaskId taskId) => _signals.GetOrAdd(taskId, static _ => new(0)).Release();
    public Task WaitAsync(ConversationTaskId taskId, CancellationToken cancellationToken) =>
        _signals.GetOrAdd(taskId, static _ => new(0)).WaitAsync(cancellationToken);
}

public sealed class InMemorySentinelRepository : IConversationRepository
{
    private readonly ConcurrentDictionary<(OwnerId Owner, ConversationTaskId Task), ConversationTask> _tasks = new();
    private readonly ConcurrentDictionary<SubscriptionId, ConversationSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<(OwnerId, ConversationTaskId, SubscriptionRequestKey), SubscriptionId> _requests = new();
    private readonly ConcurrentDictionary<(OwnerId Owner, ConversationTaskId Task), List<ConversationEvent>> _events = new();
    private readonly ConcurrentDictionary<ConversationTaskId, SemaphoreSlim> _gates = new();

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
            var key = (subscription.Owner, subscription.TaskId, subscription.Request.RequestKey);
            if (_requests.TryGetValue(key, out var existing)) return _subscriptions[existing];
            _subscriptions[subscription.Id] = subscription;
            _requests[key] = subscription.Id;
            return subscription;
        }
        finally { Gate(subscription.TaskId).Release(); }
    }

    public Task<ConversationSubscription?> GetSubscriptionAsync(OwnerId owner, ConversationTaskId taskId, SubscriptionId subscriptionId, CancellationToken cancellationToken) =>
        Task.FromResult(_subscriptions.TryGetValue(subscriptionId, out var subscription) && subscription.Owner == owner && subscription.TaskId == taskId ? subscription : null);

    public Task<IReadOnlyList<ConversationSubscription>> ListSubscriptionsAsync(OwnerId owner, ConversationTaskId taskId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ConversationSubscription>>(_subscriptions.Values.Where(x => x.Owner == owner && x.TaskId == taskId).OrderBy(x => x.CreatedAt).ToArray());

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

    public async Task AppendAssistantArtifactAsync(Principal principal, ConversationTaskId taskId, string content, CancellationToken cancellationToken)
    {
        await GetOrCreateTaskAsync(principal, taskId, cancellationToken);
        await AppendEventAsync(principal.Owner, new(taskId, default, ConversationEventKind.AssistantArtifact, null, content, DateTimeOffset.UtcNow), cancellationToken);
    }

    private SemaphoreSlim Gate(ConversationTaskId taskId) => _gates.GetOrAdd(taskId, static _ => new(1, 1));
}

public sealed class PostgresSentinelRepository : IConversationRepository, IAsyncDisposable
{
    private const string MigrationSql = """
        DROP TABLE IF EXISTS sentinel_outbound_intents;
        DROP TABLE IF EXISTS sentinel_delivery_events;
        DROP TABLE IF EXISTS sentinel_tool_calls;
        DROP TABLE IF EXISTS sentinel_task_turns;
        DROP TABLE IF EXISTS sentinel_monitors;
        CREATE TABLE IF NOT EXISTS sentinel_conversation_tasks (
            owner_id text NOT NULL, task_id text NOT NULL, tenant_id text NOT NULL, state text NOT NULL,
            created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL, PRIMARY KEY (owner_id, task_id));
        CREATE TABLE IF NOT EXISTS sentinel_subscriptions (
            subscription_id uuid PRIMARY KEY, owner_id text NOT NULL, task_id text NOT NULL, tenant_id text NOT NULL,
            query_id text NOT NULL, reaction_id text NOT NULL, request_key text NOT NULL,
            state text NOT NULL, native_subscription_id text NOT NULL UNIQUE, created_at timestamptz NOT NULL,
            UNIQUE (owner_id, task_id),
            UNIQUE (owner_id, task_id, request_key));
        CREATE TABLE IF NOT EXISTS sentinel_task_events (
            owner_id text NOT NULL, task_id text NOT NULL, sequence bigint NOT NULL, kind text NOT NULL,
            subscription_id uuid NULL, content text NOT NULL, occurred_at timestamptz NOT NULL,
            PRIMARY KEY (owner_id, task_id, sequence));
        ALTER TABLE sentinel_task_events DROP CONSTRAINT IF EXISTS sentinel_task_events_owner_id_task_id_deduplication_key_key;
        ALTER TABLE sentinel_task_events DROP COLUMN IF EXISTS deduplication_key;
        ALTER TABLE sentinel_subscriptions DROP COLUMN IF EXISTS filters;
        """;
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;
    public PostgresSentinelRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;
    private PostgresSentinelRepository(string connection) { _dataSource = NpgsqlDataSource.Create(connection); _ownsDataSource = true; }
    public static PostgresSentinelRepository Create(string connection) => new(connection);
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(MigrationSql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public async Task<ConversationTask> GetOrCreateTaskAsync(Principal principal, ConversationTaskId taskId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO sentinel_conversation_tasks (owner_id, task_id, tenant_id, state, created_at, updated_at)
            VALUES (@owner, @task, @tenant, 'Working', now(), now()) ON CONFLICT (owner_id, task_id) DO NOTHING;
            SELECT tenant_id, state, created_at, updated_at FROM sentinel_conversation_tasks WHERE owner_id=@owner AND task_id=@task;
            """, connection);
        command.Parameters.AddWithValue("owner", principal.Owner.Value); command.Parameters.AddWithValue("task", taskId.Value); command.Parameters.AddWithValue("tenant", principal.Tenant.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new(taskId, new(reader.GetString(0)), principal.Owner, Enum.Parse<ConversationTaskState>(reader.GetString(1)), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3));
    }
    public async Task<ConversationSubscription> GetOrCreateSubscriptionAsync(ConversationSubscription item, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("""
            INSERT INTO sentinel_subscriptions (subscription_id,owner_id,task_id,tenant_id,query_id,reaction_id,request_key,state,native_subscription_id,created_at)
            VALUES (@id,@owner,@task,@tenant,@query,@reaction,@request,@state,@native,@created)
            ON CONFLICT (owner_id, task_id, request_key) DO NOTHING;
            """, connection))
        { AddSubscription(command, item); await command.ExecuteNonQueryAsync(cancellationToken); }
        return await RequireSubscription(connection, item.Owner, item.TaskId, item.Request.RequestKey, cancellationToken);
    }
    public async Task<ConversationSubscription?> GetSubscriptionAsync(OwnerId owner, ConversationTaskId taskId, SubscriptionId id, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadSubscription(connection, "owner_id=@owner AND task_id=@task AND subscription_id=@id", owner, taskId, id, cancellationToken);
    }
    public async Task<IReadOnlyList<ConversationSubscription>> ListSubscriptionsAsync(OwnerId owner, ConversationTaskId taskId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = SubscriptionCommand(connection, "owner_id=@owner AND task_id=@task", owner, taskId);
        var result = new List<ConversationSubscription>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSubscription(reader));
        return result;
    }
    public async Task<ConversationSubscription?> GetSubscriptionByNativeIdAsync(NativeSubscriptionId nativeId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SubscriptionSelect + " WHERE native_subscription_id=@native", connection); command.Parameters.AddWithValue("native", nativeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? ReadSubscription(reader) : null;
    }
    public async Task SaveSubscriptionAsync(ConversationSubscription item, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("UPDATE sentinel_subscriptions SET state=@state WHERE subscription_id=@id AND owner_id=@owner AND task_id=@task", connection);
        command.Parameters.AddWithValue("state", item.State.ToString()); command.Parameters.AddWithValue("id", item.Id.Value); command.Parameters.AddWithValue("owner", item.Owner.Value); command.Parameters.AddWithValue("task", item.TaskId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new SentinelException("subscription_not_found", "The subscription does not exist.");
    }
    public async Task<ConversationEvent> AppendEventAsync(OwnerId owner, ConversationEvent input, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken); await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var lockTask = new NpgsqlCommand("SELECT 1 FROM sentinel_conversation_tasks WHERE owner_id=@owner AND task_id=@task FOR UPDATE", connection, transaction))
            { lockTask.Parameters.AddWithValue("owner", owner.Value); lockTask.Parameters.AddWithValue("task", input.TaskId.Value); await lockTask.ExecuteNonQueryAsync(cancellationToken); }
            await using var command = new NpgsqlCommand("""
                INSERT INTO sentinel_task_events (owner_id,task_id,sequence,kind,subscription_id,content,occurred_at)
                SELECT owner_id, task_id, COALESCE((SELECT MAX(sequence)+1 FROM sentinel_task_events WHERE owner_id=t.owner_id AND task_id=t.task_id),1),
                @kind,@subscription,@content,@occurred FROM sentinel_conversation_tasks t
                WHERE t.owner_id=@owner AND t.task_id=@task RETURNING sequence;
                """, connection, transaction);
            command.Parameters.AddWithValue("owner", owner.Value); command.Parameters.AddWithValue("task", input.TaskId.Value); command.Parameters.AddWithValue("kind", input.Kind.ToString()); command.Parameters.AddWithValue("subscription", (object?)input.SubscriptionId?.Value ?? DBNull.Value); command.Parameters.AddWithValue("content", input.Content); command.Parameters.AddWithValue("occurred", input.OccurredAt);
            var sequence = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException());
            await transaction.CommitAsync(cancellationToken);
            return input with { Sequence = new(sequence) };
        }
        catch { if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None); throw; }
    }
    public async Task<IReadOnlyList<ConversationEvent>> ReadEventsAsync(OwnerId owner, ConversationTaskId taskId, TaskEventSequence after, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT sequence,kind,subscription_id,content,occurred_at FROM sentinel_task_events
            WHERE owner_id=@owner AND task_id=@task AND sequence>@after ORDER BY sequence LIMIT @limit;
            """, connection);
        command.Parameters.AddWithValue("owner", owner.Value); command.Parameters.AddWithValue("task", taskId.Value); command.Parameters.AddWithValue("after", after.Value); command.Parameters.AddWithValue("limit", limit);
        var result = new List<ConversationEvent>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(taskId, new(reader.GetInt64(0)), Enum.Parse<ConversationEventKind>(reader.GetString(1)), reader.IsDBNull(2) ? null : new(reader.GetGuid(2)), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        return result;
    }
    public async Task AppendAssistantArtifactAsync(Principal principal, ConversationTaskId taskId, string content, CancellationToken cancellationToken)
    {
        await GetOrCreateTaskAsync(principal, taskId, cancellationToken);
        await AppendEventAsync(principal.Owner, new(taskId, default, ConversationEventKind.AssistantArtifact, null, content, DateTimeOffset.UtcNow), cancellationToken);
    }
    public async ValueTask DisposeAsync() { if (_ownsDataSource) await _dataSource.DisposeAsync(); }

    private const string SubscriptionSelect = "SELECT subscription_id,task_id,tenant_id,owner_id,query_id,reaction_id,request_key,state,native_subscription_id,created_at FROM sentinel_subscriptions";
    private static NpgsqlCommand SubscriptionCommand(NpgsqlConnection c, string where, OwnerId owner, ConversationTaskId task) { var x = new NpgsqlCommand(SubscriptionSelect + " WHERE " + where + " ORDER BY created_at", c); x.Parameters.AddWithValue("owner", owner.Value); x.Parameters.AddWithValue("task", task.Value); return x; }
    private static async Task<ConversationSubscription?> ReadSubscription(NpgsqlConnection c, string where, OwnerId owner, ConversationTaskId task, SubscriptionId id, CancellationToken ct) { await using var x = new NpgsqlCommand(SubscriptionSelect + " WHERE " + where, c); x.Parameters.AddWithValue("owner", owner.Value); x.Parameters.AddWithValue("task", task.Value); x.Parameters.AddWithValue("id", id.Value); await using var r = await x.ExecuteReaderAsync(ct); return await r.ReadAsync(ct) ? ReadSubscription(r) : null; }
    private static async Task<ConversationSubscription> RequireSubscription(NpgsqlConnection c, OwnerId owner, ConversationTaskId task, SubscriptionRequestKey key, CancellationToken ct) { await using var x = SubscriptionCommand(c, "owner_id=@owner AND task_id=@task AND request_key=@request", owner, task); x.Parameters.AddWithValue("request", key.Value); await using var r = await x.ExecuteReaderAsync(ct); if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Subscription was not persisted."); return ReadSubscription(r); }
    private static ConversationSubscription ReadSubscription(NpgsqlDataReader r) => new(new(r.GetGuid(0)), new(r.GetString(1)), new(r.GetString(2)), new(r.GetString(3)), new(new(r.GetString(4)), new(r.GetString(5)), new(r.GetString(6))), Enum.Parse<SubscriptionState>(r.GetString(7)), new(r.GetString(8)), r.GetFieldValue<DateTimeOffset>(9));
    private static void AddSubscription(NpgsqlCommand x, ConversationSubscription s) { x.Parameters.AddWithValue("id", s.Id.Value); x.Parameters.AddWithValue("owner", s.Owner.Value); x.Parameters.AddWithValue("task", s.TaskId.Value); x.Parameters.AddWithValue("tenant", s.Tenant.Value); x.Parameters.AddWithValue("query", s.Request.QueryId.Value); x.Parameters.AddWithValue("reaction", s.Request.ReactionId.Value); x.Parameters.AddWithValue("request", s.Request.RequestKey.Value); x.Parameters.AddWithValue("state", s.State.ToString()); x.Parameters.AddWithValue("native", s.NativeSubscriptionId.Value); x.Parameters.AddWithValue("created", s.CreatedAt); }
}
