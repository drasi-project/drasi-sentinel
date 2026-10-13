using System.Text.Json.Nodes;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;

if (args.Length != 3 || args[0] is not ("arm" or "resume"))
{
    Console.Error.WriteLine("usage: WatchRestart arm|resume <directory> <port>");
    return 2;
}

var phase = args[0];
var directory = Path.GetFullPath(args[1]);
var port = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
Directory.CreateDirectory(directory);

try
{
    await RunAsync(phase, directory, port);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static async Task RunAsync(string phase, string directory, int port)
{
    var repository = SqliteSentinelRepository.Create(SentinelDataDirectory.JournalPath(directory, null));
    await repository.InitializeAsync(CancellationToken.None);
    var notifier = new TaskEventNotifier();
    var ingestion = new NativeResultIngestion(repository, notifier, TimeProvider.System);
    using var stopping = new CancellationTokenSource();
    var ingestionTask = ingestion.RunAsync(stopping.Token);
    await using var engine = new DrasiNativeEngine(Topology(directory, port), ingestion);
    var subscriptions = new ConversationSubscriptions(Catalog(), engine, repository, notifier, TimeProvider.System);
    var principal = new Principal(new("tenant"), new("owner"));
    try
    {
        await engine.StartAsync(stopping.Token);
        if (phase == "arm")
        {
            var created = await subscriptions.InvokeAsync(
                principal,
                new("watch-task", "arm"),
                new SubscribeToQuery(new("paid-items"), new("paid"), new Unbounded()),
                stopping.Token);
            if (created.Subscription is null || created.Subscription.State != SubscriptionState.Active)
                throw new InvalidOperationException($"Watch did not arm: {created.Kind} {created.Message}");
        }
        else
        {
            await subscriptions.RestoreWatchesAsync(stopping.Token);
            var restored = (await repository.ListSubscriptionsAsync(principal.Owner, new("watch-task"), CancellationToken.None)).Single();
            if (restored.State != SubscriptionState.Active)
                throw new InvalidOperationException($"Restored watch is {restored.State}.");
        }

        await engine.StartFeedsAsync(stopping.Token);
        if (phase == "arm")
            await File.WriteAllTextAsync(Path.Combine(directory, "ready"), "ready");

        var needle = phase == "arm" ? "item-1" : "item-2";
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var events = await repository.ReadEventsAsync(principal.Owner, new("watch-task"), new(0), 50, stopping.Token);
            if (events.Any(item => item.Content.Contains(needle, StringComparison.Ordinal)))
                return;
            await Task.Delay(200, stopping.Token);
        }

        var missed = await repository.ReadEventsAsync(principal.Owner, new("watch-task"), new(0), 50, CancellationToken.None);
        throw new TimeoutException("Timed out waiting for " + needle + ". Events: " + string.Join(" | ", missed.Select(item => item.Content)));
    }
    finally
    {
        stopping.Cancel();
        try
        {
            await ingestionTask;
        }
        catch (OperationCanceledException)
        {
        }
    }
}

static DrasiTopology Topology(string directory, int port)
{
    var source = SourceConfig(port);
    var bootstrap = source.DeepClone().AsObject();
    bootstrap["kind"] = "postgres";
    return new DrasiTopology(
        "drasi-sentinel",
        directory,
        ["source/postgres", "bootstrap/postgres"],
        null,
        [
            new DrasiPluginSourceDefinition("ledger", "postgres", source, bootstrap),
        ],
        [
            new DrasiQueryDefinition(
                new("paid-items"),
                "MATCH (item:items) WHERE item.paid = true RETURN item.item_id AS item_id",
                ["ledger"]),
        ],
        null);
}

static JsonObject SourceConfig(int port) => new()
{
    ["host"] = "127.0.0.1",
    ["port"] = port,
    ["database"] = "sentinel",
    ["user"] = "sentinel",
    ["password"] = "sentinel-demo",
    ["tables"] = new JsonArray("items"),
    ["slotName"] = "drasi_reattach_slot",
    ["publicationName"] = "drasi_reattach_publication",
    ["sslMode"] = "disable",
    ["tableKeys"] = new JsonArray(new JsonObject
    {
        ["table"] = "items",
        ["keyColumns"] = new JsonArray("item_id"),
    }),
};

static SafeCatalog Catalog() => new(
    new Dictionary<QueryId, QueryCatalogItem>
    {
        [new("paid-items")] = new(
            new("paid-items"),
            "Reports paid items.",
            new HashSet<ReactionId> { new("notify") },
            ["Tell me when an item is paid."]),
    },
    new Dictionary<ReactionId, ReactionCatalogItem>
    {
        [new("notify")] = new(new("notify")),
    });
