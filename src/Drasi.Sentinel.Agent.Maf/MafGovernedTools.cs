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
    Task<string> RunAsync(MafAgentSettings settings, Principal principal, TaskTurnId turn, AgentCapabilityProjection capabilities, string input, CancellationToken cancellationToken);
}

public sealed class AzureOpenAiMafInnerAgent(IConversationSubscriptions commands) : IMafInnerAgent
{
    public async Task<string> RunAsync(MafAgentSettings settings, Principal principal, TaskTurnId turn, AgentCapabilityProjection capabilities, string input, CancellationToken cancellationToken) =>
        await new MafAIAgentRunner(AzureOpenAiMafAgentFactory.Create(settings.Endpoint, settings.Model, new MafGovernedToolFactory(commands).Create(principal, turn, capabilities), capabilities)).RunAsync(input, cancellationToken);
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
                "subscribe_to_changes", "Create an active curated change subscription on this task.", null),
            AIFunctionFactory.Create(
                (Func<Task<string>>)(() => InvokeAsync(principal, turn, new ListTaskSubscriptions())),
                "list_task_subscriptions", "List subscriptions owned by this conversation task.", null),
            AIFunctionFactory.Create(
                (Func<Task<string>>)(() => InvokeAsync(principal, turn, new GetCurrentTaskResults())),
                "get_current_task_results", "Return current results for this task's curated subscription.", null),
            AIFunctionFactory.Create(
                (Func<string, Task<string>>)(id => InvokeAsync(principal, turn, new CancelTaskSubscription(new SubscriptionId(Guid.Parse(id))))),
                "cancel_task_subscription", "Cancel one subscription on this conversation task.", null),
        ]);

    private async Task<string> InvokeAsync(Principal principal, TaskTurnId turn, GovernedToolCall call) =>
        JsonSerializer.Serialize(await commands.InvokeAsync(principal, turn, call, CancellationToken.None));

}

public sealed class MafGovernedToolSet
{
    public static IReadOnlyList<string> Names { get; } = ["subscribe_to_changes", "list_task_subscriptions", "get_current_task_results", "cancel_task_subscription"];
    internal MafGovernedToolSet(IReadOnlyList<AIFunction> functions) => Functions = functions;
    internal IReadOnlyList<AIFunction> Functions { get; }
}

internal sealed class MafAIAgentRunner(AIAgent agent)
{
    public async Task<string> RunAsync(string input, CancellationToken cancellationToken) =>
        (await agent.RunAsync(input, cancellationToken: cancellationToken)).ToString();
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
            instructions:
                "You are a governed Drasi Sentinel assistant. Use only supplied tools. Never expose source configuration, Cypher, or secrets. " +
                "For a supported request to detect changes, use subscribe_to_changes immediately and report observations on this A2A conversation. " +
                "For a request for current results, use get_current_task_results immediately. Use list_task_subscriptions, get_current_task_results, or cancel_task_subscription only for this task. Never invent subscription IDs or states. " +
                "The following JSON is the complete authorized capability catalog: " + JsonSerializer.Serialize(capabilities),
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
