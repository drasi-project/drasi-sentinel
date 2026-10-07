using System.Text.Json;
using Azure.Identity;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using System.ClientModel.Primitives;

namespace Drasi.Sentinel.Agent.Maf;

public sealed record MafAgentSettings(Uri Endpoint, string Model);
public interface IMafInnerAgent
{
    Task<string> RunAsync(
        MafAgentSettings settings,
        Principal principal,
        TaskTurnId turn,
        AgentCapabilityProjection capabilities,
        IReadOnlyList<string> priorAssistantTexts,
        string input,
        CancellationToken cancellationToken);
}

public sealed class AzureOpenAiMafInnerAgent(IConversationSubscriptions commands) : IMafInnerAgent
{
    public Task<string> RunAsync(
        MafAgentSettings settings,
        Principal principal,
        TaskTurnId turn,
        AgentCapabilityProjection capabilities,
        IReadOnlyList<string> priorAssistantTexts,
        string input,
        CancellationToken cancellationToken)
    {
        var agent = AzureOpenAiMafAgentFactory.Create(
            settings.Endpoint,
            settings.Model,
            new MafGovernedToolFactory(commands).Create(principal, turn, capabilities),
            capabilities);
        return MafTurnRunner.RunAsync(agent, priorAssistantTexts, input, cancellationToken);
    }
}

public sealed class MafGovernedToolFactory(IConversationSubscriptions commands)
{
    public MafGovernedToolSet Create(Principal principal, TaskTurnId turn, AgentCapabilityProjection capabilities) =>
        new(
        [
            AIFunctionFactory.Create(
                (Func<string, string, Task<string>>)((query, reaction) =>
                    InvokeAsync(principal, turn, new SubscribeToChanges(new SubscriptionRequest(
                        new QueryId(query), new ReactionId(reaction),
                        new SubscriptionRequestKey($"a2a:{turn.TaskId}:{turn.MessageId}:request"))))),
                "subscribe_to_changes", SentinelAgentInstructions.SubscribeToolDescription, null),
            AIFunctionFactory.Create(
                (Func<Task<string>>)(() => InvokeAsync(principal, turn, new ListTaskSubscriptions())),
                "list_task_subscriptions", SentinelAgentInstructions.ListSubscriptionsToolDescription, null),
            AIFunctionFactory.Create(
                (Func<Task<string>>)(() => InvokeAsync(principal, turn, new GetCurrentTaskResults())),
                "get_current_task_results", SentinelAgentInstructions.CurrentResultsToolDescription, null),
            AIFunctionFactory.Create(
                (Func<string, Task<string>>)(id => InvokeAsync(principal, turn, new CancelTaskSubscription(new SubscriptionId(Guid.Parse(id))))),
                "cancel_task_subscription", SentinelAgentInstructions.CancelSubscriptionToolDescription, null),
        ]);

    private async Task<string> InvokeAsync(Principal principal, TaskTurnId turn, GovernedToolCall call)
    {
        try
        {
            return JsonSerializer.Serialize(await commands.InvokeAsync(principal, turn, call, CancellationToken.None));
        }
        catch (SentinelException exception) when (IsRecoverable(exception))
        {
            return JsonSerializer.Serialize(new
            {
                Kind = SentinelAgentInstructions.ToolRejectedKind,
                exception.Code,
                exception.Message,
                Next = NextStep(exception.Code),
            });
        }
    }

    private static bool IsRecoverable(SentinelException exception) =>
        exception.Code is "subscription_not_found" or "task_already_has_subscription";

    private static string NextStep(string code) =>
        code == "subscription_not_found"
            ? "Call subscribe_to_changes for the one matching catalog query, then call get_current_task_results in this same turn, and report the rows."
            : "Do not call subscribe_to_changes again. Call get_current_task_results.";

}

public static class SentinelAgentInstructions
{
    public const string ToolRejectedKind = "tool_rejected";

    public const string SubscribeToolDescription =
        "Create this task's one curated change subscription. Do not call this when the task already has a subscription. " +
        "On a request to list, show, or return the current rows, call get_current_task_results in the same turn after this succeeds. " +
        "If this returns task_already_has_subscription, do not subscribe again; call get_current_task_results.";

    public const string ListSubscriptionsToolDescription =
        "List subscriptions owned by this conversation task.";

    public const string CurrentResultsToolDescription =
        "Return the current rows for this task's curated subscription. " +
        "Call this for a request to list, show, or return the current rows, for any other request for current results, and for a short affirmative reply to an earlier offer to check current results. " +
        "If it returns subscription_not_found, call subscribe_to_changes for the matching catalog query, then call this again in the same turn. " +
        "Do not stop to ask whether to fetch results.";

    public const string CancelSubscriptionToolDescription =
        "Cancel one subscription on this conversation task.";

    public const string Prompt =
        "You are a governed Drasi Sentinel assistant. Use only supplied tools. Never expose source configuration, Cypher, or secrets. " +
        "A request to detect changes, including a capability-catalog example, is a watch. For a supported watch, call subscribe_to_changes once and report that observations will arrive on this A2A conversation. Do not call get_current_task_results for a watch-only request. " +
        "A request to list, show, or return the current rows, or any other request for current results, is a current-results request, not a watch-only request, even when it names the same subject as a catalog example. Do not stop after subscribing and ask whether to fetch results. " +
        "If this task has no subscription, call subscribe_to_changes for the one matching catalog query, then call get_current_task_results in the same turn, and report the rows from that tool. " +
        "If this task already has a subscription, call get_current_task_results only. Do not call subscribe_to_changes again. " +
        "A short affirmative reply to an earlier assistant offer to check current results is a current-results request. Call get_current_task_results. Do not answer with only a greeting or ask how you can help. " +
        "If get_current_task_results returns subscription_not_found, call subscribe_to_changes for the one matching catalog query, then call get_current_task_results again in the same turn. " +
        "If subscribe_to_changes returns task_already_has_subscription, do not subscribe again. Call get_current_task_results. " +
        "A tool result whose Kind is tool_rejected is not a failed task. Read its Code and follow Next in this turn. " +
        "Use list_task_subscriptions when you are not sure whether this task already has a subscription. Use list_task_subscriptions, get_current_task_results, or cancel_task_subscription only for this task. Never invent subscription IDs, query IDs, reaction IDs, or states. " +
        "The following JSON is the complete authorized capability catalog: ";

    public static string Create(AgentCapabilityProjection capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return Prompt + JsonSerializer.Serialize(capabilities);
    }
}

public sealed class MafGovernedToolSet
{
    public static IReadOnlyList<string> Names { get; } = ["subscribe_to_changes", "list_task_subscriptions", "get_current_task_results", "cancel_task_subscription"];
    internal MafGovernedToolSet(IReadOnlyList<AIFunction> functions) => Functions = functions;
    internal IReadOnlyList<AIFunction> Functions { get; }
}

public static class MafTurnMessages
{
    public static IReadOnlyList<ChatMessage> Create(IReadOnlyList<string> priorAssistantTexts, string input)
    {
        ArgumentNullException.ThrowIfNull(priorAssistantTexts);
        ArgumentNullException.ThrowIfNull(input);
        var messages = new List<ChatMessage>(priorAssistantTexts.Count + 1);
        foreach (var text in priorAssistantTexts)
            messages.Add(new ChatMessage(ChatRole.Assistant, text));
        messages.Add(new ChatMessage(ChatRole.User, input));
        return messages;
    }
}

internal static class MafTurnRunner
{
    public static Task<string> RunAsync(
        AIAgent agent,
        IReadOnlyList<string> priorAssistantTexts,
        string input,
        CancellationToken cancellationToken) =>
        new MafAIAgentRunner(agent).RunAsync(MafTurnMessages.Create(priorAssistantTexts, input), cancellationToken);
}

internal sealed class MafAIAgentRunner(AIAgent agent)
{
    public async Task<string> RunAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken) =>
        (await agent.RunAsync(messages, session: null, options: null, cancellationToken)).ToString();
}

public static class AzureOpenAiMafAgentFactory
{
#pragma warning disable OPENAI001
    public static AIAgent Create(Uri endpoint, string deployment, MafGovernedToolSet tools, AgentCapabilityProjection capabilities) =>
        new OpenAIClient(
            new BearerTokenPolicy(new AzureCliCredential(new AzureCliCredentialOptions { ProcessTimeout = TimeSpan.FromSeconds(30) }), "https://ai.azure.com/.default"),
            new OpenAIClientOptions { Endpoint = NormalizeEndpoint(endpoint) })
        .GetResponsesClient()
        .AsAIAgent(
            model: deployment,
            name: "DrasiSentinel",
            instructions: SentinelAgentInstructions.Create(capabilities),
            tools: [.. tools.Functions]);
#pragma warning restore OPENAI001

    internal static Uri NormalizeEndpoint(Uri endpoint)
    {
        const string suffix = "/chat/completions";
        var path = endpoint.AbsolutePath;
        if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) path = path[..^suffix.Length];
        return new UriBuilder(endpoint) { Path = path.TrimEnd('/') + "/" }.Uri;
    }
}
