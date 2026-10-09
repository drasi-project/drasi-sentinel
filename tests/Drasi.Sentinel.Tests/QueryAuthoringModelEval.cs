using Drasi.Sentinel.Agent.Maf;
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
            AgentMessages.Create([], "Tell me when an open order has a matching payment. Build the query against the configured sources and subscribe."),
            timeout.Token);

        var authored = await store.ListAsync(CancellationToken.None);
        Assert.True(
            authored.Count == 1,
            "The model did not commit one query. Reply: " + reply + " Tools: " + string.Join(" | ", tools.Calls.Select(call => call.Name + " " + System.Text.Json.JsonSerializer.Serialize(call.Arguments))));
        QueryAuthoringEvalSupport.AssertCrossSourceJoin(authored[0], relationship: null);
        Assert.Contains(tools.Calls, call => call.Name is "get_graph_schema" or "get_source_schema" or "profile_source_schema" or "list_sources");
    }
}
