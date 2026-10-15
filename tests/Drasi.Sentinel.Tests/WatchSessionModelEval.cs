using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Drasi.Sentinel.Agent.Maf;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;
using Drasi.Sentinel.Host;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Drasi.Sentinel.Tests;

public sealed class WatchSessionModelEval(ITestOutputHelper output)
{
    [Fact]
    public async Task Configured_model_handles_varied_watch_sessions()
    {
        if (Environment.GetEnvironmentVariable("SENTINEL_QUERY_EVAL") != "1")
        {
            output.WriteLine("Skipped. Set SENTINEL_QUERY_EVAL=1 to call AZURE_OPENAI_MODEL.");
            return;
        }

        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var model = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(endpoint));
        Assert.False(string.IsNullOrWhiteSpace(model));
        var only = Environment.GetEnvironmentVariable("SENTINEL_QUERY_EVAL_ONLY");
        var limit = int.TryParse(Environment.GetEnvironmentVariable("SENTINEL_QUERY_EVAL_LIMIT"), out var parsed) ? parsed : int.MaxValue;
        var scenarios = Scenarios()
            .Where(scenario => string.IsNullOrWhiteSpace(only) || only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(scenario.Name))
            .Take(limit)
            .ToArray();
        output.WriteLine($"Calling model {model} for {scenarios.Length} watch sessions.");
        var failures = new List<string>();
        foreach (var scenario in scenarios)
        {
            try
            {
                await RunAsync(scenario, endpoint!, model!);
                output.WriteLine($"PASS {scenario.Name}");
            }
            catch (Exception exception)
            {
                failures.Add($"{scenario.Name}: {exception.Message}");
                output.WriteLine($"FAIL {scenario.Name}: {exception.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private async Task RunAsync(WatchScenario scenario, string endpoint, string model)
    {
        var (responder, repository, store, subscriptions) = Harness(endpoint, model);
        var principal = A2aPrincipalFactory.Create(new ClaimsPrincipal(), new A2aAnonymousManifest());
        var sessions = new Dictionary<string, string>(StringComparer.Ordinal);
        var knownTasks = new HashSet<string>(StringComparer.Ordinal);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        for (var index = 0; index < scenario.Turns.Length; index++)
        {
            var turn = scenario.Turns[index];
            var messageId = $"{scenario.Name}-{index + 1}";
            sessions.TryGetValue(turn.Session, out var contextId);
            var before = await ArtifactCountAsync(repository, principal, contextId, timeout.Token);
            var messageBefore = await ArtifactCountAsync(repository, principal, messageId, timeout.Token);
            await PostAsync(repository, subscriptions, responder, InspectorMessage(messageId, turn.Text, contextId));
            var (taskId, reply) = await WaitAsync(repository, principal, contextId, messageId, before, messageBefore, timeout.Token);
            output.WriteLine($"{scenario.Name} {turn.Session} {turn.Expect}: {turn.Text}");
            output.WriteLine(reply);
            knownTasks.Add(taskId);
            if (turn.Expect is WatchExpect.Stay or WatchExpect.Manage)
                Assert.Equal(contextId, taskId);
            if (turn.Expect is WatchExpect.SplitSubscribe)
            {
                Assert.NotEqual(contextId, taskId);
                Assert.Equal(messageId, taskId);
            }
            var priorSubscriptions = contextId is null
                ? []
                : await repository.ListSubscriptionsAsync(principal.Owner, new(contextId), timeout.Token);
            var priorQuery = priorSubscriptions.SingleOrDefault()?.Request.QueryId.Value;
            var active = await repository.ListSubscriptionsAsync(principal.Owner, new(taskId), timeout.Token);
            if (turn.Expect is WatchExpect.Stay or WatchExpect.Manage)
                Assert.Equal(priorSubscriptions.Count, active.Count);
            if (turn.Expect == WatchExpect.Ask)
            {
                Assert.Empty(active);
                if (turn.Text.Contains(" is ", StringComparison.OrdinalIgnoreCase))
                    Assert.True(
                        reply.Contains("vehicle", StringComparison.OrdinalIgnoreCase),
                        reply);
            }
            if (turn.Expect == WatchExpect.Manage && turn.Text.StartsWith("Cancel", StringComparison.Ordinal))
            {
                var listed = await repository.ListOwnerTasksAsync(principal.Owner, timeout.Token);
                Assert.Contains(listed, item => item.SubscriptionState == SubscriptionState.Cancelled);
                Assert.Contains(listed, item => item.SubscriptionState == SubscriptionState.Active);
            }
            if (turn.Expect is WatchExpect.Subscribe or WatchExpect.SplitSubscribe)
            {
                var subscription = Assert.Single(active);
                var committed = await store.FindAsync(subscription.Request.QueryId, timeout.Token);
                Assert.NotNull(committed);
                Assert.Contains(turn.Value!, committed!.Cypher, StringComparison.Ordinal);
                Assert.DoesNotContain("IS NOT NULL", committed.Cypher, StringComparison.OrdinalIgnoreCase);
                if (turn.Expect == WatchExpect.SplitSubscribe && priorQuery is not null)
                    Assert.NotEqual(priorQuery, subscription.Request.QueryId.Value);
            }
            if (turn.Expect == WatchExpect.Manage && turn.Value == "list")
            {
                var listed = await repository.ListOwnerTasksAsync(principal.Owner, timeout.Token);
                Assert.True(listed.Count >= 1, reply);
                Assert.False(string.IsNullOrWhiteSpace(reply));
            }
            sessions[turn.Session] = taskId;
        }

        if (scenario.ConsumerCancels is { } cancelSession)
        {
            var cancelId = sessions[cancelSession];
            await PostAsync(repository, subscriptions, responder, TaskAdmin("CancelTask", cancelId));
            var listed = await repository.ListOwnerTasksAsync(principal.Owner, timeout.Token);
            Assert.Equal(SubscriptionState.Cancelled, listed.Single(task => task.TaskId.Value == cancelId).SubscriptionState);
            Assert.Contains(listed, task => task.TaskId.Value != cancelId && task.SubscriptionState == SubscriptionState.Active);
        }
    }

    private static async Task<int> ArtifactCountAsync(IConversationRepository repository, Principal principal, string? taskId, CancellationToken cancellationToken)
    {
        if (taskId is null || !await repository.TaskExistsAsync(principal.Owner, new(taskId), cancellationToken))
            return 0;
        var events = await repository.ReadEventsAsync(principal.Owner, new(taskId), new(0), 200, cancellationToken);
        return events.Count(item => item.Kind == ConversationEventKind.AssistantArtifact);
    }

    private static async Task<(string TaskId, string Reply)> WaitAsync(
        IConversationRepository repository,
        Principal principal,
        string? contextId,
        string messageId,
        int contextBefore,
        int messageBefore,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var (id, before) in new[] { (messageId, messageBefore), (contextId, contextBefore) })
            {
                if (id is null || !await repository.TaskExistsAsync(principal.Owner, new(id), cancellationToken))
                    continue;
                var events = await repository.ReadEventsAsync(principal.Owner, new(id), new(0), 200, cancellationToken);
                var artifacts = events.Where(item => item.Kind == ConversationEventKind.AssistantArtifact).ToArray();
                if (artifacts.Length > before)
                    return (id, artifacts[^1].Content);
            }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException($"No reply for {messageId}.");
    }

    private static async Task PostAsync(
        IConversationRepository repository,
        IConversationSubscriptions subscriptions,
        IAgentResponder responder,
        string body)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        await A2aEndpoint.SendAsync(
            context,
            repository,
            subscriptions,
            new TaskEventNotifier(),
            responder,
            new A2aAnonymousManifest());
    }

    private static string InspectorMessage(string messageId, string text, string? contextId)
    {
        var context = contextId is null ? "" : ",\"contextId\":" + JsonSerializer.Serialize(contextId);
        return "{\"jsonrpc\":\"2.0\",\"id\":" + JsonSerializer.Serialize(messageId)
            + ",\"method\":\"SendStreamingMessage\",\"params\":{\"message\":{\"role\":\"ROLE_USER\",\"messageId\":"
            + JsonSerializer.Serialize(messageId)
            + context
            + ",\"parts\":[{\"text\":" + JsonSerializer.Serialize(text) + "}]}}}";
    }

    private static string TaskAdmin(string method, string taskId) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + JsonSerializer.Serialize(method + taskId)
        + ",\"method\":" + JsonSerializer.Serialize(method)
        + ",\"params\":{\"id\":" + JsonSerializer.Serialize(taskId) + "}}";

    private static (CatalogAgentResponder Responder, InMemorySentinelRepository Repository, InMemoryAuthoredQueryStore Store, IConversationSubscriptions Subscriptions) Harness(string endpoint, string model)
    {
        var sourceIds = new[] { "orders-db", "fleet-db" };
        var runtime = new RecordingRuntime
        {
            Graph = new SourceGraph(
                [
                    new GraphNode("orders", [new GraphProperty("id", "string"), new GraphProperty("status", "string"), new GraphProperty("plate", "string")], ["orders-db"]),
                    new GraphNode("vehicles", [new GraphProperty("plate", "string"), new GraphProperty("location", "string")], ["fleet-db"]),
                ],
                [],
                []),
            GraphSchema = JsonNode.Parse("""{"nodes":{"orders":{"properties":["id","status","plate"]},"vehicles":{"properties":["plate","location"]}},"relations":{},"sourcesWithoutSchema":[]}"""),
            ListedSourceIds = sourceIds,
        };
        runtime.Schemas["orders-db"] = JsonNode.Parse("""{"nodes":[{"label":"orders","properties":[{"name":"id"},{"name":"status"},{"name":"plate"}]}],"relations":[]}""");
        runtime.Schemas["fleet-db"] = JsonNode.Parse("""{"nodes":[{"label":"vehicles","properties":[{"name":"plate"},{"name":"location"}]}],"relations":[]}""");
        runtime.LabelSamples["orders"] =
        [
            JsonNode.Parse("""{"id":"1","status":"preparing","plate":"A1234"}""")!,
            JsonNode.Parse("""{"id":"2","status":"ready","plate":"F1000"}""")!,
        ];
        runtime.Row = JsonNode.Parse("""{"id":"1"}""")!;
        runtime.LabelSamples["vehicles"] =
        [
            JsonNode.Parse("""{"plate":"A1234","location":"Parking"}""")!,
            JsonNode.Parse("""{"plate":"G2000","location":"Curbside"}""")!,
        ];
        var store = new InMemoryAuthoredQueryStore();
        var catalog = new LiveCatalog(QueryAuthoringEvalSupport.Catalog());
        var repository = new InMemorySentinelRepository();
        var engine = new FakeNativeEngine();
        engine.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var sources = sourceIds.ToHashSet(StringComparer.Ordinal);
        var workbench = new QueryWorkbench(
            new QueryAuthoring(runtime, store, catalog, sources, new ReactionId("notify")),
            runtime,
            store,
            catalog,
            sources);
        var subscriptions = new ConversationSubscriptions(catalog.Catalog, engine, repository, new TaskEventNotifier(), TimeProvider.System, store);
        var responder = new CatalogAgentResponder(
            catalog.Catalog,
            subscriptions,
            MafLanguageModel.Create(new Uri(endpoint), model, ModelCredentialKind.AzureCli),
            workbench,
            new QueryAuthoringPrompt(sourceIds));
        return (responder, repository, store, subscriptions);
    }

    private static IReadOnlyList<WatchScenario> Scenarios() =>
    [
        One("ready-tell", "Tell me when an order status is ready. Keep the watch for 1h.", "ready"),
        One("parking-let", "Let me know when a vehicle location is Parking. Keep the watch for 2h.", "Parking"),
        One("preparing-notify", "Notify me when an order is preparing. Keep the watch for 1 day.", "preparing"),
        One("curb-alert", "Alert me when a vehicle is at Curbside. Keep the watch for 30m.", "Curbside"),
        One("ready-ping", "Ping me if an order status becomes ready. Keep the watch for 1h.", "ready"),
        One("parking-buzz", "Buzz me when a vehicle sits in Parking. Keep the watch for 4h.", "Parking"),
        One("ready-want", "I want a watch for orders whose status is ready. Keep it for 1h.", "ready"),
        One("curb-warn", "Warn me once a vehicle location is Curbside. Keep the watch for 1h.", "Curbside"),
        One("ready-eye", "Keep an eye out for ready orders for 1h.", "ready"),
        One("parking-shout", "Give me a shout when vehicle location is Parking. Keep the watch for 1h.", "Parking"),
        One("preparing-moment", "The moment an order is preparing, I need to know. Keep the watch for 1h.", "preparing"),
        One("curb-heads", "Heads up if a vehicle location is Curbside. Keep the watch for 1h.", "Curbside"),
        Vague("waiting-location", "Tell me when an order is ready and the customer is waiting. Keep the watch for 1h.", "vehicle location", "Curbside"),
        Vague("stuck-status", "Notify me when pickup is stuck. Keep the watch for 1h.", "still being prepared", "preparing"),
        Vague("lot-place", "Watch the lot. Keep the watch for 1h.", "vehicle location", "Parking"),
        Vague("curb-word", "Something is off with pickups. Let me know when the customer is waiting. Keep the watch for 1h.", "the customer is at the curb", "Curbside"),
        Two("ready-then-ping", "Tell me when an order status is ready. Keep the watch for 1h.", "ready", "Ping me if a vehicle is in Parking. Keep the watch for 1h.", "Parking"),
        Two("preparing-then-buzz", "Notify me when an order is preparing. Keep the watch for 1h.", "preparing", "Buzz me when a vehicle is at Curbside. Keep the watch for 2h.", "Curbside"),
        Two("parking-then-ready", "Let me know when a vehicle location is Parking. Keep the watch for 1h.", "Parking", "I need another watch: orders whose status is ready. Keep it for 1h.", "ready"),
        Two("curb-then-preparing", "Alert me when a vehicle is at Curbside. Keep the watch for 1h.", "Curbside", "Also warn me when an order is preparing. Keep the watch for 1 day.", "preparing"),
        Two("ready-then-holler", "Tell me when an order status is ready. Keep the watch for 1h.", "ready", "Holler if a vehicle location is Parking. Keep the watch for 1h.", "Parking"),
        Two("preparing-then-message", "Notify me when an order is preparing. Keep the watch for 1h.", "preparing", "Message me when a vehicle is Curbside. Keep the watch for 1h.", "Curbside"),
        SplitSessions("sessions-ready-parking", "Tell me when an order status is ready. Keep the watch for 1h.", "ready", "Let me know when a vehicle location is Parking. Keep the watch for 1h.", "Parking"),
        SplitSessions("sessions-preparing-curb", "Notify me when an order is preparing. Keep the watch for 1h.", "preparing", "Alert me when a vehicle is at Curbside. Keep the watch for 1h.", "Curbside", cancelFirst: true),
        ManageAfter("rows-stay", "Tell me when an order status is ready. Keep the watch for 1h.", "ready", "Show the current rows."),
        ManageAfter("list-stay", "Notify me when an order is preparing. Keep the watch for 1h.", "preparing", "What watches do I have?"),
        TwoThenManage("two-then-list", "Tell me when an order status is ready. Keep the watch for 1h.", "ready", "Ping me if a vehicle is in Parking. Keep the watch for 1h.", "Parking"),
        TwoThenCancel("two-then-cancel", "Let me know when a vehicle location is Parking. Keep the watch for 1h.", "Parking", "Tell me when an order status is ready. Keep the watch for 1h.", "ready"),
        VagueThenSecond("vague-then-parking", "Tell me when the customer is waiting. Keep the watch for 1h.", "vehicle location", "Curbside", "Buzz me when an order status is ready. Keep the watch for 1h.", "ready"),
        SplitVague("sessions-vague-specific", "Watch the lot. Keep the watch for 1h.", "vehicle location", "Parking", "Notify me when an order is preparing. Keep the watch for 1h.", "preparing"),
    ];

    private static WatchScenario One(string name, string text, string value) => new(
        name,
        [new("a", text, WatchExpect.Subscribe, value)]);

    private static WatchScenario Vague(string name, string first, string follow, string value) => new(
        name,
        [
            new("a", first, WatchExpect.Ask, null),
            new("a", follow, WatchExpect.Stay, null),
            new("a", value, WatchExpect.Subscribe, value),
        ]);

    private static WatchScenario Two(string name, string first, string firstValue, string second, string secondValue) => new(
        name,
        [
            new("a", first, WatchExpect.Subscribe, firstValue),
            new("a", second, WatchExpect.SplitSubscribe, secondValue),
        ]);

    private static WatchScenario SplitSessions(string name, string first, string firstValue, string second, string secondValue, bool cancelFirst = false) => new(
        name,
        [
            new("a", first, WatchExpect.Subscribe, firstValue),
            new("b", second, WatchExpect.Subscribe, secondValue),
        ],
        cancelFirst ? "a" : null);

    private static WatchScenario ManageAfter(string name, string first, string value, string management) => new(
        name,
        [
            new("a", first, WatchExpect.Subscribe, value),
            new("a", management, management.Contains("watches", StringComparison.OrdinalIgnoreCase) ? WatchExpect.Manage : WatchExpect.Stay, management.Contains("watches", StringComparison.OrdinalIgnoreCase) ? "list" : null),
        ]);

    private static WatchScenario TwoThenManage(string name, string first, string firstValue, string second, string secondValue) => new(
        name,
        [
            new("a", first, WatchExpect.Subscribe, firstValue),
            new("a", second, WatchExpect.SplitSubscribe, secondValue),
            new("a", "What watches do I have?", WatchExpect.Manage, "list"),
        ]);

    private static WatchScenario TwoThenCancel(string name, string first, string firstValue, string second, string secondValue) => new(
        name,
        [
            new("a", first, WatchExpect.Subscribe, firstValue),
            new("a", second, WatchExpect.SplitSubscribe, secondValue),
            new("a", "Cancel the parking watch.", WatchExpect.Manage, null),
        ]);

    private static WatchScenario VagueThenSecond(string name, string first, string follow, string mapped, string second, string secondValue) => new(
        name,
        [
            new("a", first, WatchExpect.Ask, null),
            new("a", follow, WatchExpect.Stay, null),
            new("a", mapped, WatchExpect.Subscribe, mapped),
            new("a", second, WatchExpect.SplitSubscribe, secondValue),
        ]);

    private static WatchScenario SplitVague(string name, string first, string follow, string mapped, string other, string otherValue) => new(
        name,
        [
            new("a", first, WatchExpect.Ask, null),
            new("a", follow, WatchExpect.Stay, null),
            new("a", mapped, WatchExpect.Subscribe, mapped),
            new("b", other, WatchExpect.Subscribe, otherValue),
        ]);

    private sealed record WatchScenario(string Name, WatchTurnSpec[] Turns, string? ConsumerCancels = null);
    private sealed record WatchTurnSpec(string Session, string Text, WatchExpect Expect, string? Value);
    private enum WatchExpect { Subscribe, Ask, Stay, SplitSubscribe, Manage }
}
