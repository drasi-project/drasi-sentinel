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

public sealed class QueryLanguageModelEval(ITestOutputHelper output)
{
    [Fact]
    public void Language_suite_lists_fifty_distinct_scenarios()
    {
        var scenarios = QueryLanguageScenarios.All;
        Assert.Equal(50, scenarios.Count);
        Assert.Equal(50, scenarios.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(scenarios, item => Assert.False(string.IsNullOrWhiteSpace(item.Group)));
        Assert.All(scenarios, item => Assert.False(string.IsNullOrWhiteSpace(item.Prompt)));
    }

    [Fact(Timeout = 14_400_000)]
    public async Task Configured_model_scores_drasi_query_language()
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
        var selected = QueryLanguageScenarios.All
            .Where(item => string.IsNullOrWhiteSpace(only) || only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(item.Name))
            .Take(limit)
            .ToArray();
        output.WriteLine($"Calling model {model} for {selected.Length} query-language scenarios.");
        var failures = new List<string>();
        foreach (var scenario in selected)
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

    private async Task RunAsync(LanguageScenario scenario, string endpoint, string model)
    {
        var (responder, repository, store, subscriptions) = Harness(endpoint, model);
        var principal = A2aPrincipalFactory.Create(new ClaimsPrincipal(), new A2aAnonymousManifest());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        if (scenario.Expect == LanguageExpect.TwoSessions)
        {
            await ScoreIndependentAsync(scenario, repository, store, subscriptions, responder, principal, scenario.Prompt, scenario.Must, scenario.MustNot, timeout.Token);
            await ScoreIndependentAsync(scenario, repository, store, subscriptions, responder, principal, scenario.Follow!, scenario.FollowMust, scenario.FollowMustNot ?? [], timeout.Token);
            var queries = await store.ListAsync(timeout.Token);
            Assert.True(queries.Count >= 2, scenario.Name + " did not keep two queries.");
            Assert.NotEqual(queries[^2].Cypher, queries[^1].Cypher);
            return;
        }

        var firstId = scenario.Name + "-1";
        var (taskId, reply) = await SendAsync(repository, subscriptions, responder, principal, firstId, scenario.Prompt, null, timeout.Token);
        output.WriteLine($"{scenario.Name}: {reply}");
        if (scenario.Expect == LanguageExpect.Ask)
        {
            Assert.Empty(await repository.ListSubscriptionsAsync(principal.Owner, new(taskId), timeout.Token));
            Assert.DoesNotContain("watching", reply, StringComparison.OrdinalIgnoreCase);
            return;
        }

        if (scenario.Expect == LanguageExpect.ThenSubscribe)
        {
            Assert.Empty(await repository.ListSubscriptionsAsync(principal.Owner, new(taskId), timeout.Token));
            var (next, secondReply) = await SendAsync(repository, subscriptions, responder, principal, scenario.Name + "-2", scenario.Follow!, taskId, timeout.Token);
            output.WriteLine($"{scenario.Name} follow: {secondReply}");
            Assert.Equal(taskId, next);
            await ScoreQueryAsync(scenario, store, repository, principal, next, scenario.FollowMust, scenario.MustNot, timeout.Token);
            return;
        }

        if (scenario.Expect == LanguageExpect.Split)
        {
            await ScoreQueryAsync(scenario, store, repository, principal, taskId, scenario.Must, [], timeout.Token);
            var (next, secondReply) = await SendAsync(repository, subscriptions, responder, principal, scenario.Name + "-2", scenario.Follow!, taskId, timeout.Token);
            output.WriteLine($"{scenario.Name} follow: {secondReply}");
            Assert.NotEqual(taskId, next);
            await ScoreQueryAsync(scenario, store, repository, principal, next, scenario.FollowMust, scenario.MustNot, timeout.Token);
            return;
        }

        await ScoreQueryAsync(scenario, store, repository, principal, taskId, scenario.Must, scenario.MustNot, timeout.Token);
    }

    private async Task ScoreIndependentAsync(
        LanguageScenario scenario,
        InMemorySentinelRepository repository,
        InMemoryAuthoredQueryStore store,
        IConversationSubscriptions subscriptions,
        IAgentResponder responder,
        Principal principal,
        string text,
        string[] must,
        string[] mustNot,
        CancellationToken cancellationToken)
    {
        var messageId = scenario.Name + "-" + text.GetHashCode().ToString("x");
        var (taskId, reply) = await SendAsync(repository, subscriptions, responder, principal, messageId, text, null, cancellationToken);
        output.WriteLine($"{scenario.Name}: {reply}");
        await ScoreQueryAsync(scenario, store, repository, principal, taskId, must, mustNot, cancellationToken);
    }

    private static async Task ScoreQueryAsync(
        LanguageScenario scenario,
        InMemoryAuthoredQueryStore store,
        InMemorySentinelRepository repository,
        Principal principal,
        string taskId,
        string[] must,
        string[] mustNot,
        CancellationToken cancellationToken)
    {
        var subscription = Assert.Single(await repository.ListSubscriptionsAsync(principal.Owner, new(taskId), cancellationToken));
        var committed = await store.FindAsync(subscription.Request.QueryId, cancellationToken);
        Assert.NotNull(committed);
        var cypher = committed!.Cypher;
        foreach (var token in must)
            Assert.Contains(token, cypher, StringComparison.Ordinal);
        foreach (var token in mustNot)
            Assert.DoesNotContain(token, cypher, StringComparison.Ordinal);
        Assert.Contains("MATCH", cypher, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", cypher, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ORDER BY", cypher, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GROUP BY", cypher, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IS NOT NULL", cypher, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JOIN ", cypher, StringComparison.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match node in System.Text.RegularExpressions.Regex.Matches(cypher, @"\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*:"))
            Assert.DoesNotMatch(@"\bRETURN\s+" + System.Text.RegularExpressions.Regex.Escape(node.Groups[1].Value) + @"\b(?!\s*\.)", cypher);
        if (!scenario.Join)
            return;
        var join = Assert.Single(committed.Joins);
        Assert.Contains(join.Id, cypher, StringComparison.Ordinal);
        Assert.Contains("pumps", cypher, StringComparison.Ordinal);
        Assert.Contains("gauges", cypher, StringComparison.Ordinal);
    }

    private async Task<(string TaskId, string Reply)> SendAsync(
        InMemorySentinelRepository repository,
        IConversationSubscriptions subscriptions,
        IAgentResponder responder,
        Principal principal,
        string messageId,
        string text,
        string? contextId,
        CancellationToken cancellationToken)
    {
        var before = await ArtifactCountAsync(repository, principal, contextId, cancellationToken);
        var messageBefore = await ArtifactCountAsync(repository, principal, messageId, cancellationToken);
        await PostAsync(repository, subscriptions, responder, InspectorMessage(messageId, text, contextId));
        return await WaitAsync(repository, principal, contextId, messageId, before, messageBefore, cancellationToken);
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

    private static (CatalogAgentResponder Responder, InMemorySentinelRepository Repository, InMemoryAuthoredQueryStore Store, IConversationSubscriptions Subscriptions) Harness(string endpoint, string model)
    {
        var sourceIds = new[] { "plant-db", "field-db" };
        var runtime = new RecordingRuntime
        {
            Graph = new SourceGraph(
                [
                    new GraphNode("pumps", [new GraphProperty("id", "string"), new GraphProperty("mode", "string"), new GraphProperty("pressure", "long"), new GraphProperty("zone", "string")], ["plant-db"]),
                    new GraphNode("gauges", [new GraphProperty("id", "string"), new GraphProperty("link_id", "string"), new GraphProperty("band", "string")], ["field-db"]),
                ],
                [],
                []),
            GraphSchema = JsonNode.Parse("""{"nodes":{"pumps":{"properties":["id","mode","pressure","zone"]},"gauges":{"properties":["id","link_id","band"]}},"relations":{},"sourcesWithoutSchema":[]}"""),
            ListedSourceIds = sourceIds,
            Row = JsonNode.Parse("""{"id":"P1"}""")!,
        };
        runtime.Schemas["plant-db"] = JsonNode.Parse("""{"nodes":[{"label":"pumps","properties":[{"name":"id"},{"name":"mode"},{"name":"pressure"},{"name":"zone"}]}],"relations":[]}""");
        runtime.Schemas["field-db"] = JsonNode.Parse("""{"nodes":[{"label":"gauges","properties":[{"name":"id"},{"name":"link_id"},{"name":"band"}]}],"relations":[]}""");
        runtime.LabelSamples["pumps"] =
        [
            JsonNode.Parse("""{"id":"P1","mode":"running","pressure":40,"zone":"North"}""")!,
            JsonNode.Parse("""{"id":"P2","mode":"idle","pressure":12,"zone":"South"}""")!,
            JsonNode.Parse("""{"id":"P3","mode":"running","pressure":12,"zone":"Northeast"}""")!,
            JsonNode.Parse("""{"id":"P4","mode":"idle","pressure":40,"zone":"Dockside"}""")!,
        ];
        runtime.LabelSamples["gauges"] =
        [
            JsonNode.Parse("""{"id":"G1","link_id":"P1","band":"High"}""")!,
            JsonNode.Parse("""{"id":"G2","link_id":"P2","band":"Low"}""")!,
        ];
        var store = new InMemoryAuthoredQueryStore();
        var reaction = new ReactionId("notify");
        var catalog = new LiveCatalog(new SafeCatalog(
            new Dictionary<QueryId, QueryCatalogItem>(),
            new Dictionary<ReactionId, ReactionCatalogItem> { [reaction] = new(reaction) }));
        var repository = new InMemorySentinelRepository();
        var engine = new FakeNativeEngine();
        engine.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var sources = sourceIds.ToHashSet(StringComparer.Ordinal);
        var workbench = new QueryWorkbench(
            new QueryAuthoring(runtime, store, catalog, sources, reaction),
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
}

internal enum LanguageExpect
{
    Subscribe,
    Ask,
    ThenSubscribe,
    Split,
    TwoSessions,
}

internal sealed record LanguageScenario(
    string Name,
    string Group,
    LanguageExpect Expect,
    string Prompt,
    string? Follow,
    string[] Must,
    string[] FollowMust,
    string[] MustNot,
    bool Join,
    string[]? FollowMustNot = null);

internal static class QueryLanguageScenarios
{
    internal static IReadOnlyList<LanguageScenario> All { get; } =
    [
        Watch("mode-running", "stored value", "Tell me when a pump mode is running. Keep the watch for 1 hour.", "running", "idle"),
        Watch("band-high", "stored value", "Tell me when a gauge band is High. Keep the watch for 1 day.", "High", "Low"),
        Watch("zone-south", "stored value", "Tell me when a pump zone is South. Keep the watch for 2 hours.", "South", "North"),
        Watch("token-dock", "stored value", "Tell me when a pump is at the dock. Keep the watch for 1 hour.", "Dockside", "North"),
        Watch("exact-high", "stored value", "Tell me when a gauge band is High. Keep the watch for 1 hour.", "High", "'high'"),
        Watch("future-stalled", "stored value", "Tell me when a pump mode is stalled. Keep the watch for 1 hour.", "stalled", "running"),
        Watch("unnamed-excluded", "stored value", "Tell me when a pump mode is running. Keep the watch for 1 hour.", "running", "idle|South|High|Dockside"),
        Watch("mode-idle", "stored value", "Tell me when a pump mode is idle. Keep the watch for 1 hour.", "idle", "running"),

        Watch("running-projects-id", "identity", "Tell me when a pump mode is running. Keep the watch for 1 hour.", ".id", ""),
        Watch("high-projects-id", "identity", "Tell me when a gauge band is High. Keep the watch for 1 hour.", ".id", ""),
        Watch("row-not-only-count", "identity", "Tell me when a pump mode is running. Keep the watch for 1 hour.", ".id", "count("),
        Watch("no-bare-node", "identity", "Tell me when a pump zone is Dockside. Keep the watch for 1 hour.", "Dockside|.id", ""),

        Watch("count-running", "aggregation", "How many pumps have mode running? Keep the watch for 1 hour.", "count(|running", "SELECT"),
        Watch("sum-pressure", "aggregation", "What is the total pressure of pumps whose mode is running? Keep the watch for 1 hour.", "sum(|running", "SELECT"),
        Watch("avg-idle", "aggregation", "What is the average pressure of pumps whose mode is idle? Keep the watch for 1 hour.", "avg(|idle", "SELECT"),
        Watch("min-running", "aggregation", "What is the minimum pressure of pumps whose mode is running? Keep the watch for 1 hour.", "min(|running", "SELECT"),
        Watch("max-north", "aggregation", "What is the maximum pressure of pumps whose zone is North? Keep the watch for 1 hour.", "max(|North", "SELECT"),
        Watch("collect-high", "aggregation", "Collect the gauge ids whose band is High. Keep the watch for 1 hour.", "collect(|High", "SELECT"),

        Watch("stays-10-minutes", "trueFor", "Tell me when a pump stays running for 10 minutes. Keep the watch for 1 hour.", "trueFor|minutes: 10|running", "hours: 1"),
        Watch("remains-30-seconds", "trueFor", "Tell me when a gauge remains High for 30 seconds. Keep the watch for 1 day.", "trueFor|seconds: 30|High", "days: 1"),
        Watch("holds-5-minutes", "trueFor", "Tell me when pump pressure stays above 40 for 5 minutes. Keep the watch for 1 hour.", "trueFor|minutes: 5|40", "hours: 1"),
        Watch("lifetime-not-truefor", "trueFor", "Tell me when a pump mode is running. Keep the watch for 4 hours.", "running", "trueFor"),
        Watch("running-for-2-hours", "trueFor", "Tell me when a pump is running for 2 hours. Keep the watch for 1 day.", "trueFor|hours: 2|running", "days: 1"),
        Watch("persisted-3-days", "trueFor", "Tell me when a pump has persisted running for 3 days. Keep the watch for 12 hours.", "trueFor|days: 3|running", "hours: 12"),

        Watch("floor-pressure", "functions", "Tell me when a pump mode is running, and round the pressure down to a whole number. Keep the watch for 1 hour.", "floor(|running|.id", ""),
        Watch("no-floor", "functions", "Tell me when a pump mode is running. Keep the watch for 1 hour.", "running", "floor("),
        Watch("case-otherwise", "functions", "Tell me when a pump mode is running. Use 1 when it is running, otherwise 0. Keep the watch for 1 hour.", "CASE|running|.id", ""),
        Watch("no-case", "functions", "Tell me when a pump mode is idle. Keep the watch for 1 hour.", "idle", "CASE"),

        Join("join-running-high", "join", "Tell me when a pump mode is running and a gauge band is High. Keep the watch for 1 hour.", "running|High"),
        Join("join-idle-low", "join", "Tell me when a pump mode is idle and a gauge band is Low. Keep the watch for 1 hour.", "idle|Low"),
        Join("join-running-low", "join", "Tell me when a pump mode is running and a gauge band is Low. Keep the watch for 1 hour.", "running|Low"),
        Join("join-idle-high", "join", "Tell me when a pump mode is idle and a gauge band is High. Keep the watch for 1 hour.", "idle|High"),
        Join("join-not-sql", "join", "Tell me when a pump mode is running and a gauge band is High. Write a continuous query, not SQL. Keep the watch for 1 hour.", "running|High"),
        Join("join-match-relationship", "join", "Tell me when a pump mode is running and a gauge band is High. There is no declared relationship. Keep the watch for 1 hour.", "running|-[:"),

        Watch("pressure-exceeds-40", "comparison", "Tell me when pump pressure exceeds 40. Keep the watch for 1 hour.", ">|40|.id", "running"),
        Watch("pressure-under-12", "comparison", "Tell me when pump pressure is under 12. Keep the watch for 1 hour.", "<|12|.id", "idle"),
        Watch("pressure-greater-than-40", "comparison", "Tell me when pump pressure is greater than 40. Keep the watch for 1 hour.", ">|40", ">="),
        Watch("no-invented-threshold", "comparison", "Tell me when a pump mode is running. Keep the watch for 1 hour.", "running", ">"),

        Ask("ask-blocked", "unmapped", "Tell me when the line is blocked. Keep the watch for 1 hour."),
        Ask("ask-ambiguous-north", "unmapped", "Tell me when a pump is at the north. Keep the watch for 1 hour."),
        Then("then-north", "unmapped", "Tell me when a pump is at the north. Keep the watch for 1 hour.", "North", "North", "Northeast"),
        Then("then-south", "unmapped", "Which pump zone should I watch? Keep the watch for 1 hour.", "South", "South", "North"),

        Split("split-idle-after-running", "narrower", "Tell me when a pump mode is running. Keep the watch for 1 hour.", "running", "Tell me when a pump mode is idle. Keep the watch for 1 hour.", "idle", "running"),
        Split("split-low-after-high", "narrower", "Tell me when a gauge band is High. Keep the watch for 1 hour.", "High", "Tell me when a gauge band is Low. Keep the watch for 1 hour.", "Low", "High"),
        Watch("running-no-zone", "narrower", "Tell me when a pump mode is running. Keep the watch for 1 hour.", "running", "North|South|Dockside|Northeast"),
        Sessions("two-sessions-running-high", "narrower", "Tell me when a pump mode is running. Keep the watch for 1 hour.", "running", "High", "Tell me when a gauge band is High. Keep the watch for 1 hour.", "High", "running"),

        Watch("count-not-sql", "cypher shape", "How many pumps have mode running? Keep the watch for 1 hour.", "MATCH|count(|running", "SELECT|GROUP BY"),
        Watch("no-order-by", "cypher shape", "Tell me when a pump mode is running, sorted by pressure. Keep the watch for 1 hour.", "running", "ORDER BY"),
        Watch("avg-not-group-by", "cypher shape", "What is the average pressure of pumps, grouped by zone? Keep the watch for 1 hour.", "avg(|zone", "GROUP BY|SELECT"),
        Join("join-not-select", "cypher shape", "Tell me when a pump mode is running and a gauge band is High. Keep the watch for 1 hour.", "MATCH|running|High"),
    ];

    private static LanguageScenario Watch(string name, string group, string prompt, string must, string mustNot) =>
        new(name, group, LanguageExpect.Subscribe, prompt, null, Parts(must), [], Parts(mustNot), false);

    private static LanguageScenario Join(string name, string group, string prompt, string must) =>
        new(name, group, LanguageExpect.Subscribe, prompt, null, Parts(must), [], [], true);

    private static LanguageScenario Ask(string name, string group, string prompt) =>
        new(name, group, LanguageExpect.Ask, prompt, null, [], [], [], false);

    private static LanguageScenario Then(string name, string group, string prompt, string follow, string must, string mustNot) =>
        new(name, group, LanguageExpect.ThenSubscribe, prompt, follow, [], Parts(must), Parts(mustNot), false);

    private static LanguageScenario Split(string name, string group, string first, string prior, string second, string must, string mustNot) =>
        new(name, group, LanguageExpect.Split, first, second, Parts(prior), Parts(must), Parts(mustNot), false);

    private static LanguageScenario Sessions(string name, string group, string first, string firstMust, string firstMustNot, string second, string secondMust, string secondMustNot) =>
        new(name, group, LanguageExpect.TwoSessions, first, second, Parts(firstMust), Parts(secondMust), Parts(firstMustNot), false, Parts(secondMustNot));

    private static string[] Parts(string text) =>
        text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
