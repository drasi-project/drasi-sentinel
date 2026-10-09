using System.Text.Json;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public static class AgentTurn
{
    public const string ToolRejectedKind = "tool_rejected";
    public const string SubscribeTool = "subscribe_to_changes";
    public const string ListTool = "list_task_subscriptions";
    public const string CurrentResultsTool = "get_current_task_results";
    public const string CancelTool = "cancel_task_subscription";
    public const string DescribeSourcesTool = "get_graph_schema";
    public const string AuthorQueryTool = "generate_query";

    public static IReadOnlyList<string> ToolNames { get; } =
        [SubscribeTool, ListTool, CurrentResultsTool, CancelTool];

    public const string SubscribeToolDescription =
        "Create this task's one subscription for one catalog query. The host binds the reaction. Follow the instructions.";

    public const string CurrentResultsToolDescription =
        "Return the current rows for this task's subscription. Follow the instructions.";

    public const string ListToolDescription =
        "List subscriptions owned by this conversation task.";

    public const string CancelToolDescription =
        "Cancel one subscription on this conversation task. Pass the subscription id from a previous tool result.";

    public const string DescribeSourcesToolDescription =
        "Return labels, properties, and source ids for the configured sources. Connection settings are not included.";

    public const string AuthorQueryToolDescription =
        "Test a Cypher query against configured sources. sources is a comma-separated list of configured source ids. joins is optional JSON: [{\"id\":\"LINK\",\"keys\":[{\"label\":\"LabelA\",\"property\":\"fk\"},{\"label\":\"LabelB\",\"property\":\"id\"}]}]. Omit query to scaffold from label. Pass commit_as and description only after the test rows match the request. The host binds the reaction.";

    public static string Recovery(string code) => code switch
    {
        "subscription_not_found" or "query_unknown" or "query_ambiguous" or "reaction_unbound" =>
            "Call subscribe_to_changes for one catalog query, then call get_current_task_results in this same turn, and report the rows.",
        "task_already_has_subscription" or "invalid_subscription_id" =>
            "Do not call subscribe_to_changes again. Call get_current_task_results.",
        "invalid_tool_arguments" =>
            "Pass the required tool argument and continue in this turn.",
        "query_rejected" or "source_unknown" =>
            "Fix the query with get_graph_schema, profile_source_schema, and test_query. Do not subscribe until add_query commits.",
        "query_exists" =>
            "That query id already exists. Subscribe to it, or choose another commit_as id.",
        "watch_ended" =>
            "The watch on this task has ended. Do not subscribe again and do not fetch current rows as a live watch.",
        _ => "Follow the instructions and continue in this turn.",
    };

    public static string Render(SafeCatalog catalog, QueryAuthoringPrompt? authoring = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var offered = CatalogBinding.From(catalog)
            .OfType<BoundQuery>()
            .Select(bound => catalog.Queries[bound.Query])
            .Select(query => new PromptQuery(query.Id.Value, query.Description, query.Examples))
            .ToArray();
        var text = Body + Recovery("subscription_not_found") + " " + Recovery("task_already_has_subscription") + " " +
            "The following JSON is the authorized query catalog: " + JsonSerializer.Serialize(offered);
        if (authoring is null)
            return text;
        return text + " " + AuthoringBody + " Configured source ids: " + JsonSerializer.Serialize(authoring.SourceIds);
    }

    private const string Body =
        "You are a governed Drasi Sentinel assistant. Use only supplied tools. Never expose source configuration, Cypher, or secrets. " +
        "A request to detect changes, including a catalog example, is a watch. For a supported watch, call subscribe_to_changes once for the matching catalog query and report that observations will arrive on this A2A conversation. Do not call get_current_task_results for a watch-only request. " +
        "A request to list, show, or return the current rows, or any other request for current results, is a current-results request, not a watch-only request, even when it names the same subject as a catalog example. Do not stop after subscribing and ask whether to fetch results. " +
        "If this task has no subscription, call subscribe_to_changes for the one matching catalog query, then call get_current_task_results in the same turn, and report the rows from that tool. " +
        "If this task already has a subscription, call get_current_task_results only. Do not call subscribe_to_changes again. " +
        "A short affirmative reply to an earlier assistant offer to check current results is a current-results request. Call get_current_task_results. Do not answer with only a greeting or ask how you can help. " +
        "If a tool result has Kind tool_rejected, read Code and follow Next in this turn. That is not a failed task. " +
        "If a tool result has Kind watch_ended, the watch on this task has ended. Do not subscribe again and do not call get_current_task_results. " +
        "The host binds the reaction. Do not pass a reaction id. Never invent subscription ids, query ids, or states. ";

    private const string AuthoringBody =
        "If the request matches a catalog query, subscribe to that query. Do not author a new one. " +
        "If it does not match, and the caller described a change to detect, build one continuous query against the configured sources. " +
        "Call get_graph_schema and get_source_schema first. If a source has no schema, or before filtering on a value, call profile_source_schema and use the examples. Never guess labels, properties, or filter values. String comparison is case-sensitive and exact. " +
        "Queries are Cypher, not SQL. Start with MATCH (n:Label). Do not use SELECT, FROM, GROUP BY, JOIN ... ON, or ORDER BY. Aggregations use count, sum, avg, min, max, or collect in RETURN. " +
        "A relational source has no relationship between labels. To relate two labels, pass joins as [{\"id\":\"LINK\",\"keys\":[{\"label\":\"LabelA\",\"property\":\"key_a\"},{\"label\":\"LabelB\",\"property\":\"key_b\"}]}] and MATCH (a:LabelA)-[:LINK]->(b:LabelB). A comma or multi-MATCH does not join. " +
        "Call test_query or generate_query to test. If the filter matches no rows but the label has data, do not report that nothing was found and do not commit. Fix the literal or join. " +
        "If the query errors, returns nothing, or a watch does not fire, call diagnose_query, diagnose_source, get_query_metrics, get_query_results, tail_logs, and watch_query. Do not guess. " +
        "watch_query observes live diffs. It cannot insert rows. There is no tool to seed a configured source. " +
        "When the test rows match, or both the filtered and unfiltered queries are empty, call add_query, then subscribe_to_changes with that query id. Use update_query to replace an authored query. " +
        "Do not invent source ids. Write Cypher only in tool arguments. Do not repeat Cypher, source configuration, or secrets in the reply. ";

    private sealed record PromptQuery(string Id, string Description, IReadOnlyList<string> Examples);
}
