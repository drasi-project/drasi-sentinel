using Drasi;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Tests;

public sealed class DrasiStoresTests
{
    [Fact]
    public async Task Open_uses_a_distinct_directory_per_manifest_and_rejects_a_second_opener()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "one");
        var second = Path.Combine(root, "two");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Engine? left = null;
        Engine? right = null;

        try
        {
            left = await DrasiStores.OpenAsync(first, null, null, "left", StartOnly, CancellationToken.None);
            right = await DrasiStores.OpenAsync(second, null, null, "right", StartOnly, CancellationToken.None);
            Assert.True(Directory.Exists(Path.Combine(first, DrasiStores.DirectoryName, "indexes")));
            Assert.True(Directory.Exists(Path.Combine(first, DrasiStores.DirectoryName, "wal")));
            Assert.True(File.Exists(Path.Combine(first, DrasiStores.DirectoryName, "state.redb")));
            Assert.True(Directory.Exists(Path.Combine(second, DrasiStores.DirectoryName, "indexes")));

            var blocked = await Assert.ThrowsAnyAsync<Exception>(() =>
                DrasiStores.OpenAsync(first, null, null, "left-again", StartOnly, CancellationToken.None));
            Assert.Contains("state store", blocked.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (left is not null)
                await DrasiStores.ReleaseAsync(left);
            if (right is not null)
                await DrasiStores.ReleaseAsync(right);
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Shutdown_still_holds_the_redb_lock_in_the_same_process()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var first = await DrasiStores.OpenAsync(directory, null, null, "reopen", StartOnly, CancellationToken.None);
            await DrasiStores.ReleaseAsync(first);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var blocked = await Assert.ThrowsAnyAsync<Exception>(() =>
                DrasiStores.OpenAsync(directory, null, null, "reopen", StartOnly, CancellationToken.None));
            Assert.Contains("already open", blocked.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Failed_start_reports_missing_plugins()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var engine = new DrasiNativeEngine(new DrasiTopology(
                "drasi-sentinel",
                directory,
                [],
                null,
                [],
                []));
            var error = await Assert.ThrowsAsync<SentinelException>(() => engine.StartAsync(CancellationToken.None));
            Assert.Equal("plugins_required", error.Code);
            Assert.True(File.Exists(Path.Combine(directory, DrasiStores.DirectoryName, "state.redb")));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Journal_reopens_from_the_data_directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = SentinelDataDirectory.JournalPath(directory, null);
        try
        {
            var principal = new Principal(new("tenant"), new("owner"));
            var repository = SqliteSentinelRepository.Create(path);
            await repository.InitializeAsync(CancellationToken.None);
            await repository.GetOrCreateTaskAsync(principal, new("task"), CancellationToken.None);
            await repository.AppendEventAsync(
                principal.Owner,
                new(new("task"), default, ConversationEventKind.Observation, null, "one", DateTimeOffset.UtcNow),
                CancellationToken.None);
            await repository.DisposeAsync();

            var reopened = SqliteSentinelRepository.Create(path);
            await reopened.InitializeAsync(CancellationToken.None);
            var events = await reopened.ReadEventsAsync(principal.Owner, new("task"), new(0), 10, CancellationToken.None);
            Assert.Equal("one", Assert.Single(events).Content);
            Assert.Equal(Path.Combine(directory, "data", SqliteSentinelRepository.FileName), path);
            var authored = Path.Combine(directory, "data", SqliteSentinelRepository.AuthoredQueriesDirectoryName);
            Assert.True(Directory.Exists(authored));
            Assert.Empty(Directory.EnumerateFiles(authored, "*.json"));
            Assert.False(File.Exists(Path.Combine(directory, "data", "authored-queries.json")));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Authored_queries_are_written_beside_the_journal()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var repository = SqliteSentinelRepository.Create(Path.Combine(directory, SqliteSentinelRepository.FileName));
        try
        {
            await repository.InitializeAsync(CancellationToken.None);
            await repository.SaveAsync(new AuthoredQuery(
                new("ready-orders"),
                "Ready pickup orders.",
                "MATCH (n:orders) WHERE n.status = 'ready' RETURN n.id AS id",
                ["ledger"],
                [new QueryJoinSpec("PLATE", [new QueryJoinKeySpec("orders", "plate"), new QueryJoinKeySpec("vehicles", "plate")])],
                new Unbounded()),
                CancellationToken.None);

            var directoryPath = Path.Combine(directory, SqliteSentinelRepository.AuthoredQueriesDirectoryName);
            var path = Path.Combine(directoryPath, "ready-orders.json");
            var written = await File.ReadAllTextAsync(path);
            Assert.Contains("\"id\": \"ready-orders\"", written, StringComparison.Ordinal);
            Assert.Contains("MATCH (n:orders)", written, StringComparison.Ordinal);
            Assert.Contains("\"id\": \"PLATE\"", written, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(directory, "authored-queries.json")));

            await repository.SaveAsync(new AuthoredQuery(
                new("parked-vehicles"),
                "Parked vehicles.",
                "MATCH (n:vehicles) WHERE n.location = 'Parking' RETURN n.plate AS plate",
                ["fleet"],
                [],
                new Unbounded()),
                CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(directoryPath, "parked-vehicles.json")));
            Assert.DoesNotContain("parked-vehicles", written, StringComparison.Ordinal);

            await repository.DeleteAsync(new("ready-orders"), CancellationToken.None);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(Path.Combine(directoryPath, "parked-vehicles.json")));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void Data_directory_must_not_be_the_manifest_directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var error = Assert.Throws<SentinelException>(() =>
            DrasiStores.ValidatePlacement(directory, directory, null));
        Assert.Equal("store_path_invalid", error.Code);
    }

    [Fact]
    public async Task Open_removes_a_probe_index_left_by_a_previous_process()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var indexes = Path.Combine(directory, DrasiStores.DirectoryName, DrasiStores.IndexDirectoryName);
        var probeId = DrasiStores.ProbeQueryPrefix + Guid.NewGuid().ToString("N");
        var committed = Path.Combine(indexes, "query-pickup-order-ready");
        Directory.CreateDirectory(Path.Combine(indexes, probeId));
        Directory.CreateDirectory(committed);
        File.WriteAllText(Path.Combine(indexes, probeId, "CURRENT"), "probe");
        File.WriteAllText(Path.Combine(committed, "CURRENT"), "keep");
        Engine? engine = null;
        try
        {
            engine = await DrasiStores.OpenAsync(directory, null, null, "sweep", StartOnly, CancellationToken.None);
            Assert.False(Directory.Exists(Path.Combine(indexes, probeId)));
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(committed, "CURRENT")));
        }
        finally
        {
            if (engine is not null)
                await DrasiStores.ReleaseAsync(engine);
            TryDelete(directory);
        }
    }

    [Fact]
    public void Leftover_probe_sweep_keeps_a_committed_query_index()
    {
        var indexes = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var probeId = DrasiStores.ProbeQueryPrefix + Guid.NewGuid().ToString("N");
        var committed = Path.Combine(indexes, "query-pickup-order-ready");
        var almost = Path.Combine(indexes, DrasiStores.ProbeQueryPrefix + "not-a-guid");
        Directory.CreateDirectory(Path.Combine(indexes, probeId));
        Directory.CreateDirectory(committed);
        Directory.CreateDirectory(almost);
        File.WriteAllText(Path.Combine(indexes, probeId, "CURRENT"), "probe");
        File.WriteAllText(Path.Combine(committed, "CURRENT"), "keep");
        try
        {
            DrasiStores.DeleteLeftoverProbeIndexes(indexes);
            Assert.False(Directory.Exists(Path.Combine(indexes, probeId)));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(committed, "CURRENT")));
            Assert.True(Directory.Exists(almost));
        }
        finally
        {
            TryDelete(indexes);
        }
    }

    [Fact]
    public void Probe_index_cleanup_refuses_a_committed_query_and_a_symlink()
    {
        var indexes = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var committed = Path.Combine(indexes, "query-pickup-order-ready");
        var probeId = DrasiStores.ProbeQueryPrefix + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(committed);
        File.WriteAllText(Path.Combine(committed, "CURRENT"), "keep");
        Directory.CreateSymbolicLink(Path.Combine(indexes, probeId), committed);
        var almost = Path.Combine(indexes, DrasiStores.ProbeQueryPrefix + "not-a-guid");
        Directory.CreateDirectory(almost);
        try
        {
            Assert.Throws<InvalidOperationException>(() => DrasiStores.DeleteProbeIndex(indexes, "query-pickup-order-ready"));
            Assert.Throws<InvalidOperationException>(() => DrasiStores.DeleteProbeIndex(indexes, probeId));
            Assert.Throws<InvalidOperationException>(() => DrasiStores.DeleteLeftoverProbeIndexes(indexes));

            Assert.Equal("keep", File.ReadAllText(Path.Combine(committed, "CURRENT")));
            Assert.True(Directory.Exists(almost));
            Assert.True(Directory.Exists(Path.Combine(indexes, probeId)));
        }
        finally
        {
            TryDelete(indexes);
        }
    }

    [Fact]
    public void Data_directory_must_not_contain_the_secret_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var data = Path.Combine(directory, "data");
        var secret = Path.Combine(data, "secrets.json");
        var error = Assert.Throws<SentinelException>(() =>
            DrasiStores.ValidatePlacement(directory, data, secret));
        Assert.Equal("store_path_invalid", error.Code);
    }

    private static Task StartOnly(Engine engine, CancellationToken cancellationToken) =>
        engine.StartAsync(cancellationToken);

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
