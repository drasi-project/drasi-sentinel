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
        "Test a Cypher query against configured sources. sources is a comma-separated list of configured source ids. joins is optional JSON: [{\"id\":\"LINK\",\"keys\":[{\"label\":\"LabelA\",\"property\":\"fk\"},{\"label\":\"LabelB\",\"property\":\"id\"}]}]. Omit query to scaffold from label. Pass commit_as and description after the test succeeds, including when the requested value is not present yet. The host binds the reaction.";

    public static string Recovery(string code, bool authoring = false) => code switch
    {
        "subscription_not_found" or "query_unknown" or "query_ambiguous" or "reaction_unbound" when authoring =>
            "That id is not a committed query. Do not invent another id and do not ask the caller to add a catalog query. Build the watch with get_graph_schema, profile_source_schema, test_query, and add_query, then call subscribe_to_changes with the committed id in this turn.",
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
        var catalogJson = "The following JSON is the authorized query catalog: " + JsonSerializer.Serialize(offered);
        if (authoring is null)
        {
            return Body + Recovery("subscription_not_found") + " " + Recovery("task_already_has_subscription") + " " +
                catalogJson;
        }

        return AuthoringPreamble + catalogJson + " " + AuthoringBody + " Configured source ids: " +
            JsonSerializer.Serialize(authoring.SourceIds);
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

    private const string AuthoringPreamble =
        "You are a governed Drasi Sentinel assistant. Use only supplied tools. Never expose source configuration, Cypher, or secrets. " +
        "A request to detect changes is a watch. If one catalog query matches, call subscribe_to_changes for that query. If the catalog is empty or none matches, build a query first. Do not call get_current_task_results for a watch-only request. " +
        "A request to list, show, or return the current rows, or any other request for current results, is a current-results request, not a watch-only request. If this task has no subscription, subscribe to the matching catalog query or the query you just committed, then call get_current_task_results in the same turn, and report the rows. If this task already has a subscription, call get_current_task_results only. Do not call subscribe_to_changes again. " +
        "A short affirmative reply to an earlier assistant offer to check current results is a current-results request. Call get_current_task_results. Do not answer with only a greeting or ask how you can help. " +
        "If a tool result has Kind tool_rejected, read Code and follow Next in this turn. That is not a failed task. " +
        "If a tool result has Kind watch_ended, the watch on this task has ended. Do not subscribe again and do not call get_current_task_results. " +
        "The host binds the reaction. Do not pass a reaction id. Never invent subscription ids or states. ";

    private const string AuthoringBody =
        "These rules override any instruction to subscribe before a query exists. The catalog JSON may be []. An empty catalog is not a failure. Do not invent a query id from the caller's words. Do not tell the caller that a catalog query must be added. " +
        "If one catalog query covers the whole request, subscribe to it. Do not author a new one. A query that omits a label the caller named is not a match. Build a new query that includes that label. If subscribe is rejected because a query does not cover a label, build that query and subscribe to the new id in this turn. " +
        "If none matches, and the caller described a change to detect, build one continuous query against the configured sources in this turn, commit it, then call subscribe_to_changes with the committed id. " +
        "Call get_graph_schema and get_source_schema for every configured source before writing Cypher. Node labels and properties come from those results. A source that omits a schema is sampled with an unlabeled match, and the labels on the returned nodes are the labels. Use those exact strings. Do not invent a label from the caller's words, from source configuration, or from a different source. If the caller refers to a returned label, including its singular form, the query must include that label. If subscribe is rejected because a label is missing, add that label and subscribe to the new query. If a sample returns no labels, say so. profile_source_schema samples the labels those results returned. Its examples are stored values from current rows, including a value that appears on only one row. Map the caller's words to one of those examples. Use its exact spelling and case. If the caller uses a stored value, or a shorter form of one, WHERE must contain that example's exact string. Do not return every row and leave the condition out. Do not write a phrase when an example is the stored value. A quoted filter that contains a stored example as one word and then more words is not that example. Replace it with the example. If CommitSkippedReason lists stored values, use one of those exact strings and commit again in this turn. Never guess labels or properties. Project schema property names. Do not project the node variable. String comparison is case-sensitive and exact. " +
        "Queries are Cypher, not SQL. Start with MATCH (n:Label). Do not use SELECT, FROM, GROUP BY, JOIN ... ON, or ORDER BY. Aggregations use count, sum, avg, min, max, or collect in RETURN only when the caller asked for a count or total. Otherwise RETURN every schema property for each matched label. A RETURN that only repeats the filtered property does not identify the row. Do not return only an aggregation. If CommitSkippedReason lists missing schema properties, add them and commit again in this turn. " +
        "A relational source has no relationship between labels. To relate two labels that share a property, pass joins as [{\"id\":\"LINK\",\"keys\":[{\"label\":\"LabelA\",\"property\":\"key_a\"},{\"label\":\"LabelB\",\"property\":\"key_b\"}]}] and MATCH (a:LabelA)-[:LINK]->(b:LabelB). A comma, a second MATCH, or variable.property = variable.property does not join. If the caller names a relationship id, use that id. If they do not, choose one and use that same id in joins and MATCH. Key order does not set direction. If CommitSkippedReason says the query declares no joins, or that a name is a join id, copy the joins JSON from that reason, use that same id in MATCH, and commit again in this turn. " +
        "A condition that must hold for a duration uses drasi.trueFor(condition, duration({ seconds: N })). A WHERE comparison does not wait. Put the condition the caller said must last inside trueFor. If CommitSkippedReason mentions trueFor, add that call with the caller's duration and commit again in this turn. " +
        "Call test_query or generate_query to test. If the filter matches no rows but the label has data, compare the literal to profile_source_schema examples. If it is the wrong case or a different spelling of an example, fix it and retest. If the caller asked to watch for a value that is not present yet, commit and subscribe. Do not tell the caller that nothing was found. " +
        "If the query errors, returns nothing, or a watch does not fire, call diagnose_query, diagnose_source, get_query_metrics, get_query_results, tail_logs, and watch_query. Do not guess. " +
        "watch_query observes live diffs. It cannot insert rows. There is no tool to seed a configured source. " +
        "When the test succeeds, or the filter is the future value the caller asked to watch, call add_query, then subscribe_to_changes with that query id. If the commit is skipped, fix the query and call add_query again in this turn. Use update_query to replace an authored query. " +
        "Do not invent source ids. Write Cypher only in tool arguments. Do not repeat Cypher, source configuration, or secrets in the reply. ";

    private sealed record PromptQuery(string Id, string Description, IReadOnlyList<string> Examples);
}
