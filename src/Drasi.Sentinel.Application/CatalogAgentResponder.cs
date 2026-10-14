using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public sealed class CatalogAgentResponder(
    SafeCatalog catalog,
    IConversationSubscriptions commands,
    ILanguageModel model,
    IQueryWorkbench? workbench = null,
    QueryAuthoringPrompt? authoringPrompt = null,
    TtlPolicy? ttlPolicy = null,
    TimeProvider? time = null) : IAgentResponder
{
    public Task<string> RespondAsync(
        Principal principal,
        TaskTurnId turn,
        string input,
        IReadOnlyList<AgentMessage> priorTurns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var tools = AgentToolSession.Create(
            commands, principal, turn, cancellationToken, workbench, AgentMessages.CallerText(priorTurns, input), ttlPolicy, time);
        return model.CompleteAsync(
            AgentTurn.Render(catalog, authoringPrompt, ttlPolicy),
            tools,
            AgentMessages.Create(priorTurns, input),
            cancellationToken);
    }
}
