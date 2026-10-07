using System.Text.Json.Nodes;
using Drasi;

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
}
