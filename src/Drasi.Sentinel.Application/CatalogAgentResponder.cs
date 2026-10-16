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
    public async Task<string> RespondAsync(
        Principal principal,
        TaskTurnId turn,
        string input,
        IReadOnlyList<AgentMessage> priorTurns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var callerText = AgentMessages.CallerText(priorTurns, input);
        var tools = AgentToolSession.Create(
            commands, principal, turn, cancellationToken, workbench, callerText, ttlPolicy, time);
        var instructions = AgentTurn.Render(catalog, authoringPrompt, ttlPolicy);
        var messages = AgentMessages.Create(priorTurns, input);
        var reply = await model.CompleteAsync(instructions, tools, messages, cancellationToken);
        for (var attempt = 0; attempt < 2 && workbench is not null && !tools.Subscribed; attempt++)
        {
            var continuation = tools.CorrectableSkip
                ?? tools.NarrowerQuery
                ?? await workbench.ContinuationAsync(callerText, cancellationToken);
            if (continuation is null)
                break;
            var continued = new List<AgentMessage>(messages)
            {
                new(AgentRole.Assistant, reply),
                new(AgentRole.User, continuation),
            };
            reply = await model.CompleteAsync(instructions, tools, continued, cancellationToken);
        }
        if (workbench is null || tools.Subscribed)
            return reply;
        reply = await workbench.IncludeOmittedLabelsAsync(callerText, reply, cancellationToken);
        return await workbench.RepairJoinReplyAsync(callerText, reply, cancellationToken);
    }
}
