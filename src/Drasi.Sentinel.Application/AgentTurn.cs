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

    public static string Recovery(string code) => code switch
    {
        "subscription_not_found" or "query_unknown" or "query_ambiguous" or "reaction_unbound" =>
            "Call subscribe_to_changes for one catalog query, then call get_current_task_results in this same turn, and report the rows.",
        "task_already_has_subscription" or "invalid_subscription_id" =>
            "Do not call subscribe_to_changes again. Call get_current_task_results.",
        "invalid_tool_arguments" =>
            "Pass the required tool argument and continue in this turn.",
        "watch_ended" =>
            "The watch on this task has ended. Do not subscribe again and do not fetch current rows as a live watch.",
        _ => "Follow the instructions and continue in this turn.",
    };

    public static string Render(SafeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var offered = CatalogBinding.From(catalog)
            .OfType<BoundQuery>()
            .Select(bound => catalog.Queries[bound.Query])
            .Select(query => new PromptQuery(query.Id.Value, query.Description, query.Examples))
            .ToArray();
        return Body + Recovery("subscription_not_found") + " " + Recovery("task_already_has_subscription") + " " +
            "The following JSON is the authorized query catalog: " + JsonSerializer.Serialize(offered);
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

    private sealed record PromptQuery(string Id, string Description, IReadOnlyList<string> Examples);
}
