using System.Text.Json.Nodes;
using Drasi;
using Drasi.Sentinel.Application;

namespace Drasi.Sentinel.Tests;

public sealed class DrasiNativeEngineTests
{
    [Fact]
    public async Task Drasi_binding_runs_a_curated_query()
    {
        await using var engine = await Engine.CreateAsync($"direct-test-{Guid.NewGuid():N}");
        await engine.StartAsync();
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync(
            "open",
            "MATCH (o:Order) WHERE o.status = 'open' RETURN o.id AS id",
            ["orders"]);
        await engine.WaitForQueryAsync("open");
        await engine.PushChangeAsync("orders", new SourceChange
        {
            Op = ChangeOp.Insert,
            Id = "order-1",
            Labels = ["Order"],
            Properties = new JsonObject { ["id"] = "order-1", ["status"] = "open" },
        });

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        IReadOnlyList<JsonNode> rows;
        do
        {
            rows = await engine.GetQueryResultsAsync("open");
            if (rows.Count == 1)
            {
                break;
            }
            await Task.Delay(25);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Single(rows);
    }

    [Fact]
    public async Task Mysql_plugin_that_ignores_autostart_can_start_feeds()
    {
        var plugins = FindMysqlPluginDirectory();
        if (plugins is null)
            return;

        var directory = Path.Combine(Path.GetTempPath(), "sentinel-mysql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.GetFiles(plugins, "libdrasi_source_mysql.*"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));

        await using var engine = new DrasiNativeEngine(new DrasiTopology(
            "drasi-sentinel",
            directory,
            ["source/mysql"],
            null,
            [
                new DrasiPluginSourceDefinition(
                    "physical-ops",
                    "mysql",
                    new JsonObject
                    {
                        ["host"] = "127.0.0.1",
                        ["port"] = 1,
                        ["database"] = "physical",
                        ["user"] = "sentinel",
                        ["password"] = "sentinel-demo",
                        ["tables"] = new JsonArray("vehicles"),
                        ["sslMode"] = "disabled",
                        ["serverId"] = 5403,
                    },
                    null),
            ],
            []));
        try
        {
            await engine.StartAsync(CancellationToken.None);
            await engine.StartFeedsAsync(CancellationToken.None);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task Probe_deletes_its_index_after_success_and_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "sentinel-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Engine? engine = null;
        try
        {
            engine = await DrasiStores.OpenAsync(root, null, null, "probe", static (started, token) => started.StartAsync(token), CancellationToken.None);
            await engine.AddSourceAsync("orders");
            await engine.AddQueryAsync(
                "paid",
                "MATCH (o:Order) WHERE o.paid = true RETURN o.id AS id",
                ["orders"],
                new QueryOptions { EnableBootstrap = true });
            await engine.WaitForQueryAsync("paid");
            await engine.PushChangeAsync("orders", new SourceChange
            {
                Op = ChangeOp.Insert,
                Id = "order-1",
                Labels = ["Order"],
                Properties = new JsonObject { ["id"] = "order-1", ["paid"] = true },
            });
            Assert.Equal("order-1", await WaitForId(engine, "paid"));

            var indexes = DrasiStores.IndexDirectory(root, null);
            var committed = Directory.EnumerateDirectories(indexes)
                .Single(path => !Path.GetFileName(path).StartsWith(DrasiStores.ProbeQueryPrefix, StringComparison.Ordinal));
            var probe = await DrasiNativeEngine.RunProbeAsync(
                engine,
                indexes,
                "MATCH (o:Order) WHERE o.paid = true RETURN o.id AS id",
                ["orders"],
                [],
                TimeSpan.FromSeconds(15),
                CancellationToken.None);
            Assert.True(probe.Ok, probe.Error);
            Assert.Equal("order-1", Assert.Single(probe.Rows)["id"]!.GetValue<string>());
            AssertNoProbeIndexes(root);

            var failed = await DrasiNativeEngine.RunProbeAsync(
                engine,
                indexes,
                "NOT CYPHER",
                ["missing"],
                [],
                TimeSpan.FromSeconds(15),
                CancellationToken.None);
            Assert.False(failed.Ok);
            AssertNoProbeIndexes(root);
            Assert.Equal("order-1", await WaitForId(engine, "paid"));
            Assert.True(Directory.Exists(committed));
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

    private static void AssertNoProbeIndexes(string root)
    {
        var leftovers = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Where(path => path.Contains(DrasiStores.ProbeQueryPrefix, StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(leftovers);
    }

    private static async Task<string> WaitForId(Engine engine, string queryId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var rows = await engine.GetQueryResultsAsync(queryId);
            if (rows.Count == 1)
                return rows[0]["id"]!.GetValue<string>();
            await Task.Delay(25);
        }

        throw new TimeoutException("Timed out waiting for " + queryId);
    }

    private static string? FindMysqlPluginDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "examples", "curbside-local-demo");
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "libdrasi_source_mysql.*").Length > 0)
                return candidate;
            directory = directory.Parent;
        }

        return null;
    }
}
