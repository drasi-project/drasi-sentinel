using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Application;

public sealed class CatalogAgentResponder(
    SafeCatalog catalog,
    IConversationSubscriptions commands,
    ILanguageModel model,
    IQueryWorkbench? workbench = null,
    QueryAuthoringPrompt? authoringPrompt = null) : IAgentResponder
{
    public Task<string> RespondAsync(
        Principal principal,
        TaskTurnId turn,
        string input,
        IReadOnlyList<string> priorAssistantTexts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var tools = AgentToolSession.Create(commands, principal, turn, cancellationToken, workbench);
        return model.CompleteAsync(
            AgentTurn.Render(catalog, authoringPrompt),
            tools,
            AgentMessages.Create(priorAssistantTexts, input),
            cancellationToken);
    }
}
