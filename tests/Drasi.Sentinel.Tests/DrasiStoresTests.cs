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
