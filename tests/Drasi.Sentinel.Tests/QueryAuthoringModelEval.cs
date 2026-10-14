using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Drasi.Sentinel.Agent.Maf;
using Drasi.Sentinel.Host;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Tests;

public sealed class QueryAuthoringModelEval(ITestOutputHelper output)
{
    [Fact]
    public async Task Configured_model_authors_a_cross_source_join()
    {
        if (Environment.GetEnvironmentVariable("SENTINEL_QUERY_EVAL") != "1")
        {
            output.WriteLine("Skipped. Set SENTINEL_QUERY_EVAL=1 to call AZURE_OPENAI_MODEL.");
            return;
        }

        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var model = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(endpoint), "AZURE_OPENAI_ENDPOINT is required when SENTINEL_QUERY_EVAL=1.");
        Assert.False(string.IsNullOrWhiteSpace(model), "AZURE_OPENAI_MODEL is required when SENTINEL_QUERY_EVAL=1.");
        output.WriteLine($"Calling model {model}.");

        var runtime = new RecordingRuntime();
        var store = new InMemoryAuthoredQueryStore();
        var catalog = new LiveCatalog(QueryAuthoringEvalSupport.Catalog());
        var session = AgentToolSession.Create(
            new ConversationSubscriptions(
                catalog.Catalog,
                new FakeNativeEngine(),
                new InMemorySentinelRepository(),
                new TaskEventNotifier(),
                TimeProvider.System),
            new Principal(new("tenant"), new("owner")),
            new("task", "eval"),
            CancellationToken.None,
            QueryAuthoringEvalSupport.Workbench(runtime, store, catalog));
        var tools = new RecordingTools(session);
        var language = MafLanguageModel.Create(new Uri(endpoint!), model!, ModelCredentialKind.AzureCli);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var reply = await language.CompleteAsync(
            AgentTurn.Render(catalog.Catalog, new QueryAuthoringPrompt(QueryAuthoringEvalSupport.SourceIds)),
            tools,
            AgentMessages.Create(Array.Empty<string>(), "Tell me when an open order has a matching payment. Build the query against the configured sources and subscribe."),
            timeout.Token);

        var authored = await store.ListAsync(CancellationToken.None);
        Assert.True(
            authored.Count == 1,
            "The model did not commit one query. Reply: " + reply + " Tools: " + string.Join(" | ", tools.Calls.Select(call => call.Name + " " + System.Text.Json.JsonSerializer.Serialize(call.Arguments))));
        QueryAuthoringEvalSupport.AssertCrossSourceJoin(authored[0], relationship: null);
        Assert.Contains(tools.Calls, call => call.Name is "get_graph_schema" or "get_source_schema" or "profile_source_schema" or "list_sources");
    }

    [Fact(Timeout = 480_000)]
    public async Task Configured_model_uses_a_mapping_named_in_later_turns()
    {
        if (Environment.GetEnvironmentVariable("SENTINEL_QUERY_EVAL") != "1")
        {
            output.WriteLine("Skipped. Set SENTINEL_QUERY_EVAL=1 to call AZURE_OPENAI_MODEL.");
            return;
        }

        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var model = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(endpoint), "AZURE_OPENAI_ENDPOINT is required when SENTINEL_QUERY_EVAL=1.");
        Assert.False(string.IsNullOrWhiteSpace(model), "AZURE_OPENAI_MODEL is required when SENTINEL_QUERY_EVAL=1.");
        output.WriteLine($"Calling model {model} for the later-turn mapping.");

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
        runtime.LabelSamples["vehicles"] =
        [
            JsonNode.Parse("""{"plate":"A1234","location":"Parking"}""")!,
            JsonNode.Parse("""{"plate":"G2000","location":"Curbside"}""")!,
        ];

        var store = new InMemoryAuthoredQueryStore();
        var catalog = new LiveCatalog(QueryAuthoringEvalSupport.Catalog());
        var repository = new InMemorySentinelRepository();
        var engine = new FakeNativeEngine();
        await engine.StartAsync(CancellationToken.None);
        var principal = new Principal(new("tenant"), new("owner"));
        var taskId = new ConversationTaskId("mapping-task");
        await repository.GetOrCreateTaskAsync(principal, taskId, CancellationToken.None);
        var sources = sourceIds.ToHashSet(StringComparer.Ordinal);
        var workbench = new QueryWorkbench(
            new QueryAuthoring(runtime, store, catalog, sources, new ReactionId("notify")),
            runtime,
            store,
            catalog,
            sources);
        var responder = new CatalogAgentResponder(
            catalog.Catalog,
            new ConversationSubscriptions(catalog.Catalog, engine, repository, new TaskEventNotifier(), TimeProvider.System),
            MafLanguageModel.Create(new Uri(endpoint!), model!, ModelCredentialKind.AzureCli),
            workbench,
            new QueryAuthoringPrompt(sourceIds));

        var turns = new[]
        {
            "Tell me when an order is ready and the customer is waiting. Keep the watch for 1h.",
            "the customer is at the curbside",
            "vehicle location",
            "Curbside",
            "yes",
        };
        string? subscribedAfter = null;
        var replies = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        for (var index = 0; index < turns.Length; index++)
        {
            var prior = await ConversationTurnHistory.LoadTurnsAsync(repository, principal.Owner, taskId, timeout.Token);
            await repository.AppendEventAsync(
                principal.Owner,
                new ConversationEvent(taskId, default, ConversationEventKind.CallerUtterance, null, turns[index], DateTimeOffset.UtcNow),
                timeout.Token);
            var reply = await responder.RespondAsync(
                principal, new(taskId.Value, $"turn-{index}"), turns[index], prior, timeout.Token);
            await repository.AppendAssistantArtifactAsync(principal, taskId, reply, timeout.Token);
            replies.Add(reply);
            output.WriteLine($"Turn {index}: {turns[index]}");
            output.WriteLine(reply);
            var subscriptions = await repository.ListSubscriptionsAsync(principal.Owner, taskId, timeout.Token);
            if (index == 0)
            {
                Assert.Empty(subscriptions);
                Assert.False(reply.Contains("is active", StringComparison.OrdinalIgnoreCase), reply);
            }
            else if (subscriptions.Count > 0)
            {
                subscribedAfter = turns[index];
                Assert.False(AsksAgain(reply), "The model asked again after subscribing. Reply: " + reply);
                break;
            }
            else if (index >= 1 && turns[index].Contains("curbside", StringComparison.OrdinalIgnoreCase))
            {
                Assert.False(AsksAgain(reply), $"The model asked again after the caller named the stored value. Reply: {reply}");
            }
        }

        var active = await repository.ListSubscriptionsAsync(principal.Owner, taskId, CancellationToken.None);
        Assert.True(
            active.Count == 1,
            "The later mapping did not become one watch. Replies: " + string.Join(" || ", replies));
        var query = await store.FindAsync(active[0].Request.QueryId, CancellationToken.None);
        Assert.NotNull(query);
        Assert.Contains("ready", query.Cypher, StringComparison.Ordinal);
        Assert.Contains("Curbside", query.Cypher, StringComparison.Ordinal);
        Assert.Contains("location", query.Cypher, StringComparison.Ordinal);
        output.WriteLine($"Subscribed after '{subscribedAfter}' to {query.Id.Value}: {query.Cypher}");
    }

    [Fact(Timeout = 720_000)]
    public async Task Configured_model_asks_which_value_when_a_later_turn_names_only_the_property()
    {
        if (Environment.GetEnvironmentVariable("SENTINEL_QUERY_EVAL") != "1")
        {
            output.WriteLine("Skipped. Set SENTINEL_QUERY_EVAL=1 to call AZURE_OPENAI_MODEL.");
            return;
        }

        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var model = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(endpoint), "AZURE_OPENAI_ENDPOINT is required when SENTINEL_QUERY_EVAL=1.");
        Assert.False(string.IsNullOrWhiteSpace(model), "AZURE_OPENAI_MODEL is required when SENTINEL_QUERY_EVAL=1.");
        output.WriteLine($"Calling model {model} through Inspector-shaped A2A messages.");

        var (responder, repository, store, subscriptions) = PropertyMappingHarness(endpoint!, model!);
        var principal = A2aPrincipalFactory.Create(new ClaimsPrincipal(), new A2aAnonymousManifest());
        var turns = new[]
        {
            "Tell me when an order is ready for pickup and the customer is waiting. Keep the watch for 1h.",
            "vehicle location",
            "Curbside",
        };
        var replies = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(11));
        string? contextId = null;
        for (var index = 0; index < turns.Length; index++)
        {
            var messageId = $"msg-{index + 1}";
            var body = InspectorMessage(messageId, turns[index], contextId);
            var before = contextId is null
                ? 0
                : (await repository.ReadEventsAsync(principal.Owner, new(contextId), new(0), 100, timeout.Token))
                    .Count(item => item.Kind == ConversationEventKind.AssistantArtifact);
            await PostStreamingAsync(body, repository, subscriptions, responder);
            contextId ??= messageId;
            var reply = await WaitForReplyAsync(repository, principal, new(contextId), before, timeout.Token);
            replies.Add(reply);
            output.WriteLine($"Turn {index}: {turns[index]}");
            output.WriteLine(reply);
            if (messageId != contextId)
                Assert.False(
                    await repository.TaskExistsAsync(principal.Owner, new(messageId), timeout.Token),
                    $"Follow-up {messageId} opened a new task instead of continuing {contextId}.");
            var events = await repository.ReadEventsAsync(principal.Owner, new(contextId), new(0), 100, timeout.Token);
            Assert.Contains(events, item => item.Kind == ConversationEventKind.CallerUtterance && item.Content == turns[index]);
            var subscriptionsOnTask = await repository.ListSubscriptionsAsync(principal.Owner, new(contextId), timeout.Token);
            if (index < 2)
                Assert.Empty(subscriptionsOnTask);
            if (index == 1)
            {
                Assert.True(
                    reply.Contains('?') ||
                    reply.Contains("which", StringComparison.OrdinalIgnoreCase) ||
                    reply.Contains("please", StringComparison.OrdinalIgnoreCase),
                    reply);
                Assert.Contains("location", reply, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("is active", reply, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.False(await repository.TaskExistsAsync(principal.Owner, new("msg-2"), CancellationToken.None));
        Assert.False(await repository.TaskExistsAsync(principal.Owner, new("msg-3"), CancellationToken.None));
        var authored = await store.ListAsync(CancellationToken.None);
        Assert.DoesNotContain(authored, query => query.Cypher.Contains("IS NOT NULL", StringComparison.OrdinalIgnoreCase));
        var active = await repository.ListSubscriptionsAsync(principal.Owner, new(contextId!), CancellationToken.None);
        Assert.True(active.Count == 1, "Naming the stored value did not become one watch. Replies: " + string.Join(" || ", replies));
        var committed = await store.FindAsync(active[0].Request.QueryId, CancellationToken.None);
        Assert.NotNull(committed);
        Assert.Contains("ready", committed.Cypher, StringComparison.Ordinal);
        Assert.Contains("Curbside", committed.Cypher, StringComparison.Ordinal);
        Assert.Contains("location", committed.Cypher, StringComparison.Ordinal);
        Assert.Contains("orders", committed.Cypher, StringComparison.Ordinal);
        Assert.Contains("vehicles", committed.Cypher, StringComparison.Ordinal);
        Assert.DoesNotContain("IS NOT NULL", committed.Cypher, StringComparison.OrdinalIgnoreCase);
        var join = Assert.Single(committed.Joins);
        Assert.Contains(":" + join.Id, committed.Cypher, StringComparison.Ordinal);
        Assert.Contains(join.Keys, key => key.Label.Equals("orders", StringComparison.OrdinalIgnoreCase) && key.Property.Equals("plate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(join.Keys, key => key.Label.Equals("vehicles", StringComparison.OrdinalIgnoreCase) && key.Property.Equals("plate", StringComparison.OrdinalIgnoreCase));
        output.WriteLine(committed.Cypher);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(committed.Joins));
    }

    private static string InspectorMessage(string messageId, string text, string? contextId)
    {
        var context = contextId is null ? "" : ",\"contextId\":" + System.Text.Json.JsonSerializer.Serialize(contextId);
        return "{\"jsonrpc\":\"2.0\",\"id\":" + System.Text.Json.JsonSerializer.Serialize(messageId)
            + ",\"method\":\"SendStreamingMessage\",\"params\":{\"message\":{\"role\":\"ROLE_USER\",\"messageId\":"
            + System.Text.Json.JsonSerializer.Serialize(messageId)
            + context
            + ",\"parts\":[{\"text\":" + System.Text.Json.JsonSerializer.Serialize(text) + "}]}}}";
    }

    private static async Task PostStreamingAsync(
        string body,
        IConversationRepository repository,
        IConversationSubscriptions subscriptions,
        IAgentResponder responder)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
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

    private static async Task<string> WaitForReplyAsync(
        IConversationRepository repository,
        Principal principal,
        ConversationTaskId taskId,
        int priorArtifacts,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await repository.TaskExistsAsync(principal.Owner, taskId, cancellationToken))
            {
                var events = await repository.ReadEventsAsync(principal.Owner, taskId, new(0), 100, cancellationToken);
                var artifacts = events.Where(item => item.Kind == ConversationEventKind.AssistantArtifact).ToArray();
                if (artifacts.Length > priorArtifacts)
                    return artifacts[^1].Content;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("The A2A turn did not store an assistant reply.");
    }

    private static (CatalogAgentResponder Responder, InMemorySentinelRepository Repository, InMemoryAuthoredQueryStore Store, IConversationSubscriptions Subscriptions) PropertyMappingHarness(string endpoint, string model)
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
        var subscriptions = new ConversationSubscriptions(catalog.Catalog, engine, repository, new TaskEventNotifier(), TimeProvider.System);
        var responder = new CatalogAgentResponder(
            catalog.Catalog,
            subscriptions,
            MafLanguageModel.Create(new Uri(endpoint), model, ModelCredentialKind.AzureCli),
            workbench,
            new QueryAuthoringPrompt(sourceIds));
        return (responder, repository, store, subscriptions);
    }

    private static bool AsksAgain(string reply) =>
        reply.Contains('?', StringComparison.Ordinal) &&
        (reply.Contains("which", StringComparison.OrdinalIgnoreCase) ||
         reply.Contains("what", StringComparison.OrdinalIgnoreCase) ||
         reply.Contains("more detail", StringComparison.OrdinalIgnoreCase));
}
