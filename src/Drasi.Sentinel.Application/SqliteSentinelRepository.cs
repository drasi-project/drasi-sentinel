using System.Globalization;
using System.Text.Json;
using Drasi.Sentinel.Domain;
using Microsoft.Data.Sqlite;

namespace Drasi.Sentinel.Application;

public static class SentinelDataDirectory
{
    public static string Resolve(string manifestDirectory, string? configuredDataDirectory) =>
        DrasiStores.ResolveRoot(manifestDirectory, configuredDataDirectory);

    public static string JournalPath(string manifestDirectory, string? configuredDataDirectory) =>
        Path.Combine(Resolve(manifestDirectory, configuredDataDirectory), SqliteSentinelRepository.FileName);
}

public sealed class SqliteSentinelRepository : IConversationRepository, IAuthoredQueryStore, IAsyncDisposable
{
    public const string FileName = "journal.sqlite";
    public const string AuthoredQueriesFileName = "authored-queries.json";

    private const string MigrationSql = """
        CREATE TABLE IF NOT EXISTS sentinel_conversation_tasks (
            owner_id TEXT NOT NULL,
            task_id TEXT NOT NULL,
            tenant_id TEXT NOT NULL,
            state TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY (owner_id, task_id));
        CREATE TABLE IF NOT EXISTS sentinel_subscriptions (
            subscription_id TEXT PRIMARY KEY,
            owner_id TEXT NOT NULL,
            task_id TEXT NOT NULL,
            tenant_id TEXT NOT NULL,
            query_id TEXT NOT NULL,
            reaction_id TEXT NOT NULL,
            request_key TEXT NOT NULL,
            state TEXT NOT NULL,
            native_subscription_id TEXT NOT NULL UNIQUE,
            created_at TEXT NOT NULL,
            expires_at TEXT NULL,
            UNIQUE (owner_id, task_id),
            UNIQUE (owner_id, task_id, request_key));
        CREATE TABLE IF NOT EXISTS sentinel_task_events (
            owner_id TEXT NOT NULL,
            task_id TEXT NOT NULL,
            sequence INTEGER NOT NULL,
            kind TEXT NOT NULL,
            subscription_id TEXT NULL,
            content TEXT NOT NULL,
            occurred_at TEXT NOT NULL,
            PRIMARY KEY (owner_id, task_id, sequence));
        CREATE TABLE IF NOT EXISTS sentinel_authored_queries (
            query_id TEXT PRIMARY KEY,
            description TEXT NOT NULL,
            cypher TEXT NOT NULL,
            source_ids TEXT NOT NULL,
            joins TEXT NOT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT NULL);
        CREATE TABLE IF NOT EXISTS sentinel_diff_receipts (
            subscription_id TEXT NOT NULL,
            receipt TEXT NOT NULL,
            PRIMARY KEY (subscription_id, receipt));
        """;

    private readonly string _path;

    private SqliteSentinelRepository(string path) => _path = path;

    public static SqliteSentinelRepository Create(string path) => new(path);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = MigrationSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "sentinel_subscriptions", "expires_at", cancellationToken);
        await EnsureColumnAsync(connection, "sentinel_authored_queries", "expires_at", cancellationToken);
        await PublishAuthoredQueriesAsync(cancellationToken);
    }

    public async Task<ConversationTask> GetOrCreateTaskAsync(Principal principal, ConversationTaskId taskId, CancellationToken cancellationToken)
    {
        var now = Format(DateTimeOffset.UtcNow);
        await using var connection = await OpenAsync(cancellationToken);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO sentinel_conversation_tasks (owner_id, task_id, tenant_id, state, created_at, updated_at)
                VALUES (@owner, @task, @tenant, 'Working', @now, @now)
                ON CONFLICT (owner_id, task_id) DO NOTHING;
                """;
            Add(insert, "@owner", principal.Owner.Value);
            Add(insert, "@task", taskId.Value);
            Add(insert, "@tenant", principal.Tenant.Value);
            Add(insert, "@now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT tenant_id, state, created_at, updated_at
            FROM sentinel_conversation_tasks WHERE owner_id=@owner AND task_id=@task;
            """;
        Add(select, "@owner", principal.Owner.Value);
        Add(select, "@task", taskId.Value);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new(taskId, new(reader.GetString(0)), principal.Owner, Enum.Parse<ConversationTaskState>(reader.GetString(1)), ParseTime(reader.GetString(2)), ParseTime(reader.GetString(3)));
    }

    public async Task<ConversationSubscription> GetOrCreateSubscriptionAsync(ConversationSubscription item, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO sentinel_subscriptions (
                    subscription_id, owner_id, task_id, tenant_id, query_id, reaction_id, request_key, state, native_subscription_id, created_at, expires_at)
                VALUES (@id, @owner, @task, @tenant, @query, @reaction, @request, @state, @native, @created, @expires)
                ON CONFLICT (owner_id, task_id) DO NOTHING;
                """;
            AddSubscription(insert, item);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        return await RequireTaskSubscription(connection, item.Owner, item.TaskId, cancellationToken);
    }

    public async Task<ConversationSubscription?> GetSubscriptionAsync(OwnerId owner, ConversationTaskId taskId, SubscriptionId id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadSubscription(connection, "owner_id=@owner AND task_id=@task AND subscription_id=@id", owner, taskId, id, cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationSubscription>> ListSubscriptionsAsync(OwnerId owner, ConversationTaskId taskId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = SubscriptionCommand(connection, "owner_id=@owner AND task_id=@task", owner, taskId);
        var result = new List<ConversationSubscription>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadSubscription(reader));
        return result;
    }

    public async Task<ConversationSubscription?> GetSubscriptionByNativeIdAsync(NativeSubscriptionId nativeId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SubscriptionSelect + " WHERE native_subscription_id=@native";
        Add(command, "@native", nativeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSubscription(reader) : null;
    }

    public async Task<IReadOnlyList<ConversationSubscription>> ListRestorableSubscriptionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SubscriptionSelect + """
             WHERE state IN ('Arming', 'Active', 'Degraded') ORDER BY created_at;
            """;
        var result = new List<ConversationSubscription>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadSubscription(reader));
        return result;
    }

    public async Task<ConversationEvent?> AppendDiffOnceAsync(OwnerId owner, ConversationEvent input, string? receipt, CancellationToken cancellationToken)
    {
        if (input.SubscriptionId is not { } subscriptionId)
            throw new InvalidOperationException("A query diff requires a subscription.");
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "BEGIN IMMEDIATE;", cancellationToken);
        try
        {
            if (receipt is not null)
            {
                await using var claim = connection.CreateCommand();
                claim.CommandText = """
                    INSERT INTO sentinel_diff_receipts (subscription_id, receipt) VALUES (@id, @receipt)
                    ON CONFLICT DO NOTHING;
                    """;
                Add(claim, "@id", subscriptionId.Value.ToString("D"));
                Add(claim, "@receipt", receipt);
                if (await claim.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await ExecuteAsync(connection, "COMMIT;", cancellationToken);
                    return null;
                }
            }

            var stored = await InsertEventAsync(connection, owner, input, cancellationToken);
            await ExecuteAsync(connection, "COMMIT;", cancellationToken);
            return stored;
        }
        catch
        {
            await RollbackAsync(connection, cancellationToken);
            throw;
        }
    }

    public async Task SaveSubscriptionAsync(ConversationSubscription item, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sentinel_subscriptions SET state=@state, native_subscription_id=@native
            WHERE subscription_id=@id AND owner_id=@owner AND task_id=@task;
            """;
        Add(command, "@state", item.State.ToString());
        Add(command, "@native", item.NativeSubscriptionId.Value);
        Add(command, "@id", item.Id.Value.ToString("D"));
        Add(command, "@owner", item.Owner.Value);
        Add(command, "@task", item.TaskId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new SentinelException("subscription_not_found", "The subscription does not exist.");
    }

    public async Task<ConversationEvent> AppendEventAsync(OwnerId owner, ConversationEvent input, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "BEGIN IMMEDIATE;", cancellationToken);
        try
        {
            var stored = await InsertEventAsync(connection, owner, input, cancellationToken);
            await ExecuteAsync(connection, "COMMIT;", cancellationToken);
            return stored;
        }
        catch
        {
            await RollbackAsync(connection, cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<ConversationEvent>> ReadEventsAsync(OwnerId owner, ConversationTaskId taskId, TaskEventSequence after, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, kind, subscription_id, content, occurred_at FROM sentinel_task_events
            WHERE owner_id=@owner AND task_id=@task AND sequence>@after ORDER BY sequence LIMIT @limit;
            """;
        Add(command, "@owner", owner.Value);
        Add(command, "@task", taskId.Value);
        Add(command, "@after", after.Value);
        Add(command, "@limit", limit);
        var result = new List<ConversationEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                taskId,
                new(reader.GetInt64(0)),
                Enum.Parse<ConversationEventKind>(reader.GetString(1)),
                reader.IsDBNull(2) ? null : new SubscriptionId(Guid.Parse(reader.GetString(2))),
                reader.GetString(3),
                ParseTime(reader.GetString(4))));
        }

        return result;
    }

    public async Task<TaskEventSequence> ReadLatestEventSequenceAsync(OwnerId owner, ConversationTaskId taskId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(MAX(sequence), 0) FROM sentinel_task_events WHERE owner_id=@owner AND task_id=@task;
            """;
        Add(command, "@owner", owner.Value);
        Add(command, "@task", taskId.Value);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return new(scalar is null or DBNull ? 0 : Convert.ToInt64(scalar, CultureInfo.InvariantCulture));
    }

    public async Task AppendAssistantArtifactAsync(Principal principal, ConversationTaskId taskId, string content, CancellationToken cancellationToken)
    {
        await GetOrCreateTaskAsync(principal, taskId, cancellationToken);
        await AppendEventAsync(principal.Owner, new(taskId, default, ConversationEventKind.AssistantArtifact, null, content, DateTimeOffset.UtcNow), cancellationToken);
    }

    public async Task<IReadOnlyList<AuthoredQuery>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT query_id, description, cypher, source_ids, joins, expires_at FROM sentinel_authored_queries ORDER BY query_id";
        var result = new List<AuthoredQuery>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadAuthored(reader));
        return result;
    }

    public async Task<AuthoredQuery?> FindAsync(QueryId id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT query_id, description, cypher, source_ids, joins, expires_at FROM sentinel_authored_queries WHERE query_id=@id";
        Add(command, "@id", id.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAuthored(reader) : null;
    }

    public async Task SaveAsync(AuthoredQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sentinel_authored_queries (query_id, description, cypher, source_ids, joins, created_at, expires_at)
            VALUES (@id, @description, @cypher, @sources, @joins, @created, @expires)
            ON CONFLICT (query_id) DO NOTHING;
            """;
        AddAuthored(command, query);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new SentinelException("query_exists", $"Query '{query.Id.Value}' already exists.");
        await PublishAuthoredQueriesAsync(cancellationToken);
    }

    public async Task ReplaceAsync(AuthoredQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sentinel_authored_queries
            SET description=@description, cypher=@cypher, source_ids=@sources, joins=@joins
            WHERE query_id=@id;
            """;
        AddAuthored(command, query);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new SentinelException("query_unknown", $"Query '{query.Id.Value}' does not exist.");
        await PublishAuthoredQueriesAsync(cancellationToken);
    }

    public async Task DeleteAsync(QueryId id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sentinel_authored_queries WHERE query_id=@id";
        Add(command, "@id", id.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await PublishAuthoredQueriesAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task PublishAuthoredQueriesAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory))
            return;
        var queries = await ListAsync(cancellationToken);
        var json = JsonSerializer.Serialize(
            queries.Select(query => new
            {
                id = query.Id.Value,
                description = query.Description,
                cypher = query.Cypher,
                sources = query.SourceIds,
                joins = query.Joins.Select(join => new
                {
                    id = join.Id,
                    keys = join.Keys.Select(key => new { label = key.Label, property = key.Property }),
                }),
                expiresAt = query.Expires.Instant,
            }),
            new JsonSerializerOptions { WriteIndented = true });
        var destination = Path.Combine(directory, AuthoredQueriesFileName);
        var temporary = destination + ".tmp";
        await File.WriteAllTextAsync(temporary, json + Environment.NewLine, cancellationToken);
        File.Move(temporary, destination, overwrite: true);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;", cancellationToken);
        return connection;
    }

    private static async Task<ConversationEvent> InsertEventAsync(
        SqliteConnection connection,
        OwnerId owner,
        ConversationEvent input,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sentinel_task_events (owner_id, task_id, sequence, kind, subscription_id, content, occurred_at)
            SELECT @owner, @task,
                   COALESCE((SELECT MAX(sequence) + 1 FROM sentinel_task_events WHERE owner_id=@owner AND task_id=@task), 1),
                   @kind, @subscription, @content, @occurred
            WHERE EXISTS (
                SELECT 1 FROM sentinel_conversation_tasks WHERE owner_id=@owner AND task_id=@task)
            RETURNING sequence;
            """;
        Add(command, "@owner", owner.Value);
        Add(command, "@task", input.TaskId.Value);
        Add(command, "@kind", input.Kind.ToString());
        Add(command, "@subscription", input.SubscriptionId is null ? DBNull.Value : input.SubscriptionId.Value.Value.ToString("D"));
        Add(command, "@content", input.Content);
        Add(command, "@occurred", Format(input.OccurredAt));
        var sequence = await command.ExecuteScalarAsync(cancellationToken);
        if (sequence is null or DBNull)
            throw new InvalidOperationException();
        return input with { Sequence = new(Convert.ToInt64(sequence, CultureInfo.InvariantCulture)) };
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RollbackAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(connection, "ROLLBACK;", cancellationToken);
        }
        catch (SqliteException)
        {
        }
    }

    private const string SubscriptionSelect = """
        SELECT subscription_id, task_id, tenant_id, owner_id, query_id, reaction_id, request_key, state, native_subscription_id, created_at, expires_at
        FROM sentinel_subscriptions
        """;

    private static SqliteCommand SubscriptionCommand(SqliteConnection connection, string where, OwnerId owner, ConversationTaskId task)
    {
        var command = connection.CreateCommand();
        command.CommandText = SubscriptionSelect + " WHERE " + where + " ORDER BY created_at";
        Add(command, "@owner", owner.Value);
        Add(command, "@task", task.Value);
        return command;
    }

    private static async Task<ConversationSubscription?> ReadSubscription(SqliteConnection connection, string where, OwnerId owner, ConversationTaskId task, SubscriptionId id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SubscriptionSelect + " WHERE " + where;
        Add(command, "@owner", owner.Value);
        Add(command, "@task", task.Value);
        Add(command, "@id", id.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSubscription(reader) : null;
    }

    private static async Task<ConversationSubscription> RequireTaskSubscription(SqliteConnection connection, OwnerId owner, ConversationTaskId task, CancellationToken cancellationToken)
    {
        await using var command = SubscriptionCommand(connection, "owner_id=@owner AND task_id=@task", owner, task);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Subscription was not persisted.");
        return ReadSubscription(reader);
    }

    private static ConversationSubscription ReadSubscription(SqliteDataReader reader) => new(
        new(Guid.Parse(reader.GetString(0))),
        new(reader.GetString(1)),
        new(reader.GetString(2)),
        new(reader.GetString(3)),
        new(new(reader.GetString(4)), new(reader.GetString(5)), new(reader.GetString(6))),
        Enum.Parse<SubscriptionState>(reader.GetString(7)),
        new(reader.GetString(8)),
        ParseTime(reader.GetString(9)),
        ReadLifetime(reader, 10));

    private static void AddSubscription(SqliteCommand command, ConversationSubscription subscription)
    {
        Add(command, "@id", subscription.Id.Value.ToString("D"));
        Add(command, "@owner", subscription.Owner.Value);
        Add(command, "@task", subscription.TaskId.Value);
        Add(command, "@tenant", subscription.Tenant.Value);
        Add(command, "@query", subscription.Request.QueryId.Value);
        Add(command, "@reaction", subscription.Request.ReactionId.Value);
        Add(command, "@request", subscription.Request.RequestKey.Value);
        Add(command, "@state", subscription.State.ToString());
        Add(command, "@native", subscription.NativeSubscriptionId.Value);
        Add(command, "@created", Format(subscription.CreatedAt));
        Add(command, "@expires", FormatLifetime(subscription.Expires));
    }

    private static AuthoredQuery ReadAuthored(SqliteDataReader reader)
    {
        var joins = System.Text.Json.JsonSerializer.Deserialize<StoredJoin[]>(reader.GetString(4)) ?? [];
        return new(
            new(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            System.Text.Json.JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [],
            joins.Select(join => new QueryJoinSpec(
                join.Id,
                join.Keys.Select(key => new QueryJoinKeySpec(key.Label, key.Property)).ToArray())).ToArray(),
            ReadLifetime(reader, 5));
    }

    private static void AddAuthored(SqliteCommand command, AuthoredQuery query)
    {
        Add(command, "@id", query.Id.Value);
        Add(command, "@description", query.Description);
        Add(command, "@cypher", query.Cypher);
        Add(command, "@sources", System.Text.Json.JsonSerializer.Serialize(query.SourceIds));
        Add(command, "@joins", System.Text.Json.JsonSerializer.Serialize(query.Joins));
        Add(command, "@created", Format(DateTimeOffset.UtcNow));
        Add(command, "@expires", FormatLifetime(query.Expires));
    }

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, CancellationToken cancellationToken)
    {
        await using var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await info.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetString(1).Equals(column, StringComparison.Ordinal))
                return;
        }

        await ExecuteAsync(connection, $"ALTER TABLE {table} ADD COLUMN {column} TEXT NULL;", cancellationToken);
    }

    private static Lifetime ReadLifetime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? new Unbounded() : new Until(ParseTime(reader.GetString(ordinal)));

    private static object FormatLifetime(Lifetime lifetime) =>
        lifetime.Instant is { } instant ? Format(instant) : DBNull.Value;

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class StoredJoin
    {
        public string Id { get; set; } = "";
        public List<StoredJoinKey> Keys { get; set; } = [];
    }

    private sealed class StoredJoinKey
    {
        public string Label { get; set; } = "";
        public string Property { get; set; } = "";
    }
}
