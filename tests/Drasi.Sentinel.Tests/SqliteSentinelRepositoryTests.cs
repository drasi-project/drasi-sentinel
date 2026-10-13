using Drasi.Sentinel.Application;
using Microsoft.Data.Sqlite;

namespace Drasi.Sentinel.Tests;

public sealed class SqliteSentinelRepositoryTests
{
    [Fact]
    public async Task Initialize_adds_the_restorable_subscription_index_to_a_new_and_an_existing_journal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sentinel-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SqliteSentinelRepository.FileName);
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE sentinel_subscriptions (
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
                        UNIQUE (owner_id, task_id));
                    """;
                await command.ExecuteNonQueryAsync();
            }

            ISentinelRepository repository = SqliteSentinelRepository.Create(path);
            await repository.InitializeAsync(CancellationToken.None);
            Assert.Contains("ix_sentinel_subscriptions_restorable", await IndexNamesAsync(path));

            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    EXPLAIN QUERY PLAN
                    SELECT subscription_id FROM sentinel_subscriptions
                    WHERE state IN ('Arming', 'Active', 'Degraded')
                    ORDER BY created_at;
                    """;
                await using var reader = await command.ExecuteReaderAsync();
                var plan = new List<string>();
                while (await reader.ReadAsync())
                    plan.Add(reader.GetString(3));
                Assert.Contains(plan, line => line.Contains("ix_sentinel_subscriptions_restorable", StringComparison.Ordinal));
            }
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static async Task<IReadOnlyList<string>> IndexNamesAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='index'";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
